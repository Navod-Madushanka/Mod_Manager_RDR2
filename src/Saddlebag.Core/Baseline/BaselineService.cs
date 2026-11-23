using System.Collections.Concurrent;
using System.Diagnostics;
using Saddlebag.Core.State;
using Saddlebag.Core.Util;

namespace Saddlebag.Core.Baseline;

public enum FindingKind { Missing, Changed, Extra, ModSignature, AllowedExtra, ExpectedChange, Deployed }

public sealed record Finding(FindingKind Kind, string Path, string Detail);

public enum VerifyStatus { Green, Amber, Red }

public sealed record VerifyReport(VerifyStatus Status, IReadOnlyList<Finding> Findings, int FilesChecked, int FilesHashed, bool Deep)
{
    public IEnumerable<Finding> Offenders => Findings.Where(f => f.Kind is FindingKind.Missing or FindingKind.Changed or FindingKind.Extra or FindingKind.ModSignature or FindingKind.Deployed);
}

public sealed class BaselineService(StateStore store, string gameDir)
{
    /// <summary>Files and folders that only exist when something is modded. Matched against the path's file name or first folder.</summary>
    public static readonly string[] SignatureFiles = ["dinput8.dll", "version.dll", "ScriptHookRDR2.dll", "ScriptHookRDRNetAPI.dll", "vfs.asi", "ScriptHookRDRDotNet.asi"];
    public static readonly string[] SignatureFolders = ["lml", "scripts"];

    public static bool IsModSignature(string rel)
    {
        var name = RelPath.FileName(rel);
        if (name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)) return true;
        if (SignatureFiles.Contains(name, RelPath.Comparer) && !rel.Contains('/')) return true;
        return rel.Contains('/') && SignatureFolders.Contains(RelPath.FirstSegment(rel), RelPath.Comparer);
    }

    public IEnumerable<string> WalkGame()
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        return Directory.EnumerateFiles(gameDir, "*", opts).Select(f => RelPath.From(gameDir, f));
    }

    /// <summary>Signature files sitting in the folder right now; used to refuse a baseline of an install that isn't clean.</summary>
    public List<string> FindSignatures() => WalkGame().Where(IsModSignature).ToList();

    public static string? ReadGameVersion(string gameDir)
    {
        var exe = Path.Combine(gameDir, "RDR2.exe");
        if (!File.Exists(exe)) return null;
        var v = FileVersionInfo.GetVersionInfo(exe).FileVersion;
        return string.IsNullOrWhiteSpace(v) ? $"size:{new FileInfo(exe).Length}" : v;
    }

    public int Capture(IProgress<(int Done, int Total, string Path)>? progress = null)
    {
        var files = WalkGame().ToList();
        var rows = new ConcurrentBag<BaselineRecord>();
        var done = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) }, rel =>
        {
            var full = RelPath.ToFull(gameDir, rel);
            var fi = new FileInfo(full);
            rows.Add(new BaselineRecord(rel, fi.Length, fi.LastWriteTimeUtc.Ticks, FileOps.Sha256(full)));
            progress?.Report((Interlocked.Increment(ref done), files.Count, rel));
        });
        var version = ReadGameVersion(gameDir);
        store.ReplaceBaseline(rows, version);
        var exe = Path.Combine(gameDir, "RDR2.exe");
        if (File.Exists(exe)) store.SetSetting("game_exe_sha256", FileOps.Sha256(exe));
        return rows.Count;
    }

    /// <summary>True when RDR2.exe no longer matches the copy recorded with the baseline (a game update).</summary>
    public bool GameUpdatedSinceBaseline()
    {
        var recorded = store.GetSetting("game_exe_sha256");
        var exe = Path.Combine(gameDir, "RDR2.exe");
        if (recorded is null || !File.Exists(exe)) return false;
        var row = store.GetBaseline().FirstOrDefault(b => RelPath.Comparer.Equals(b.Path, "RDR2.exe"));
        var fi = new FileInfo(exe);
        if (row is not null && row.Size == fi.Length && row.MTime == fi.LastWriteTimeUtc.Ticks) return false;
        return FileOps.Sha256(exe) != recorded;
    }

    /// <summary>
    /// Compares the whole tree with the baseline. Size and mtime are checked first; a file whose size matches but
    /// whose mtime moved is hashed to confirm. With <paramref name="deep"/>, every file is hashed.
    /// </summary>
    public VerifyReport Verify(bool deep = false, IProgress<(int Done, int Total, string Path)>? progress = null)
    {
        if (store.BaselineCount() == 0) throw new InvalidOperationException("No baseline yet. Run `saddlebag baseline capture` on a clean, verified install first.");
        var baseline = store.GetBaseline().ToDictionary(b => b.Path, RelPath.Comparer);
        var rules = store.GetPathRules();
        var deployed = store.GetDeployed().ToDictionary(d => d.DestPath, RelPath.Comparer);
        var onDisk = WalkGame().ToList();
        var findings = new ConcurrentBag<Finding>();
        var hashed = 0;
        var done = 0;

        Parallel.ForEach(onDisk, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) }, rel =>
        {
            progress?.Report((Interlocked.Increment(ref done), onDisk.Count, rel));
            rules.TryGetValue(rel, out var rule);
            if (!baseline.TryGetValue(rel, out var b))
            {
                if (deployed.TryGetValue(rel, out var d)) findings.Add(new(FindingKind.Deployed, rel, $"deployed by {d.ModId}"));
                else if (rule == StateStore.RuleAllow) findings.Add(new(FindingKind.AllowedExtra, rel, "not in baseline; allowed by you"));
                else if (IsModSignature(rel)) findings.Add(new(FindingKind.ModSignature, rel, "mod file not in baseline"));
                else findings.Add(new(FindingKind.Extra, rel, "not in baseline"));
                return;
            }
            var full = RelPath.ToFull(gameDir, rel);
            var fi = new FileInfo(full);
            bool changed;
            if (fi.Length != b.Size) changed = true;
            else if (!deep && fi.LastWriteTimeUtc.Ticks == b.MTime) changed = false;
            else { Interlocked.Increment(ref hashed); changed = FileOps.Sha256(full) != b.Sha256; }
            if (!changed) return;
            if (deployed.TryGetValue(rel, out var dd)) findings.Add(new(FindingKind.Deployed, rel, $"vanilla file replaced by {dd.ModId}"));
            else if (rule == StateStore.RuleExpectChange) findings.Add(new(FindingKind.ExpectedChange, rel, "changed; marked expected to change"));
            else if (rule == StateStore.RuleAllow) findings.Add(new(FindingKind.AllowedExtra, rel, "changed; allowed by you"));
            else findings.Add(new(FindingKind.Changed, rel, fi.Length != b.Size ? $"size {b.Size} → {fi.Length}" : "contents differ"));
        });

        var present = new HashSet<string>(onDisk, RelPath.Comparer);
        foreach (var b in baseline.Values.Where(b => !present.Contains(b.Path)))
            findings.Add(new(FindingKind.Missing, b.Path, "in baseline, not on disk"));

        foreach (var dir in SignatureFolders)
            if (Directory.Exists(Path.Combine(gameDir, dir)) && !baseline.Keys.Any(k => RelPath.IsUnder(k, dir)) && !onDisk.Any(k => RelPath.IsUnder(k, dir)))
                findings.Add(new(FindingKind.ModSignature, dir + "/", "empty mod folder"));

        var list = findings.OrderBy(f => f.Kind).ThenBy(f => f.Path, RelPath.Comparer).ToList();
        var status = list.Any(f => f.Kind is FindingKind.Missing or FindingKind.Changed or FindingKind.Extra or FindingKind.ModSignature or FindingKind.Deployed)
            ? VerifyStatus.Red
            : list.Any(f => f.Kind == FindingKind.AllowedExtra) ? VerifyStatus.Amber : VerifyStatus.Green;
        return new VerifyReport(status, list, onDisk.Count, hashed, deep);
    }
}
