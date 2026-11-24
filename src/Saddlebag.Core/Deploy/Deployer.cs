using System.Text.Json;
using Saddlebag.Core.Library;
using Saddlebag.Core.Routing;
using Saddlebag.Core.State;
using Saddlebag.Core.Util;

namespace Saddlebag.Core.Deploy;

public enum StepType { Remove, Restore, Backup, Place }

/// <summary>One journaled file operation. Every step can be re-run safely after a crash.</summary>
public sealed record Step(
    StepType Type,
    string Dest,
    string? ModId = null,
    string? LibraryPath = null,
    string? Source = null,
    string? Sha256 = null,
    string? BackupPath = null,
    string? Method = null,
    string? Note = null);

/// <param name="LeftOut">Mods not included because they still need a choice (only with leaveOutUnfinished).</param>
public sealed record DeployPlan(string Kind, IReadOnlyList<Step> Steps, IReadOnlyList<CheckFinding> Checks, IReadOnlyList<string> Blockers)
{
    public IReadOnlyList<string> LeftOut { get; init; } = [];
    public bool IsEmpty => Steps.Count == 0;
    public bool CanApply => Blockers.Count == 0;
}

/// <param name="Skipped">Files not placed because their source changed mid-deploy (e.g. a build still writing). The next deploy places them.</param>
public sealed record ApplyResult(int Placed, int Removed, int BackedUp, int Restored, int Rescued, IReadOnlyList<string> Notes, int Skipped = 0);

public sealed class DeployException(string message) : Exception(message);

/// <summary>
/// Makes the game folder match the enabled mods. Plan → journal → apply → record, one step at a time:
/// a crash mid-way leaves a pending journal that <see cref="Recover"/> finishes on the next run.
/// </summary>
public sealed class Deployer(StateStore store, ModLibrary library, string home, string gameDir, RuleSet? rules = null)
{
    private readonly RuleSet _rules = rules ?? RuleSet.Default;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Test hook: runs after a step's file operation and before its ledger commit, to simulate a crash.</summary>
    internal Action<Step>? AfterFileOp { get; set; }

    public string BackupsDir => Path.Combine(home, "backups");
    public string RescuedDir => Path.Combine(home, "rescued");

    private string GamePath(string rel) => RelPath.ToFull(gameDir, rel);

    // ---------- planning ----------

    /// <summary>
    /// Plans the changes that make the game folder match the enabled mods of the active profile.
    /// With <paramref name="purge"/>, the target is no mods at all. Blocking checks (missing dependencies)
    /// stop the plan unless <paramref name="ignoreBlockingChecks"/> is set.
    /// </summary>
    /// <param name="leaveOutUnfinished">Mods that still need a choice (a variant, or where some files go) are left out instead
    /// of blocking everything; they're listed in <see cref="DeployPlan.LeftOut"/>. The simple Home screen uses this.</param>
    public DeployPlan Plan(bool purge = false, bool ignoreBlockingChecks = false, bool leaveOutUnfinished = false)
    {
        var leftOut = new List<string>();
        var blockers = new List<string>();
        var candidates = new List<Candidate>();
        var checks = new List<CheckFinding>();
        var desired = new Dictionary<string, Candidate>(RelPath.Comparer);

        if (!purge)
        {
            var mods = store.GetMods().ToDictionary(m => m.Id);
            foreach (var pm in store.GetProfileMods().Where(p => p.Enabled))
            {
                if (!mods.TryGetValue(pm.ModId, out var mod)) continue;
                if (mod.IsLinked)
                {
                    try { library.Reroute(mod.Id); }
                    catch (DirectoryNotFoundException) { blockers.Add($"{mod.Name}: linked folder is missing ({mod.LinkedPath})."); continue; }
                }
                var manifest = library.LoadManifest(mod.Id);
                if (manifest.PackageType == "variants")
                {
                    if (leaveOutUnfinished) { leftOut.Add(mod.Id); continue; }
                    blockers.Add($"{mod.Name}: pick a variant first (`saddlebag route {mod.Id} --variant <folder>`).");
                    continue;
                }
                var files = store.GetModFiles(mod.Id);
                var unresolved = files.Where(f => f.DestPath is null && f.Kind is "Data" or "Unknown").ToList();
                if (unresolved.Count > 0)
                {
                    if (leaveOutUnfinished) { leftOut.Add(mod.Id); continue; }
                    blockers.Add($"{mod.Name}: {unresolved.Count} file(s) need a destination, e.g. '{unresolved[0].LibraryPath}' (`saddlebag route {mod.Id}`).");
                    continue;
                }
                candidates.AddRange(files.Where(f => f.DestPath is not null).Select(f =>
                    new Candidate(f.DestPath!, mod.Id, mod.Name, f.LibraryPath, library.SourcePath(mod, f.LibraryPath), f.Sha256,
                        mod.IsLinked || ModLibrary.IsGenerated(f.LibraryPath), pm.Priority)));
            }

            var (winners, resolveFindings) = DeployChecks.Resolve(candidates, _rules);
            desired = winners;
            checks.AddRange(resolveFindings);
            var deployedNow = store.GetDeployed().Select(d => d.DestPath).ToHashSet(RelPath.Comparer);
            var untracked = _rules.Frameworks.Where(f => !deployedNow.Contains(f) && File.Exists(GamePath(f))).ToHashSet(RelPath.Comparer);
            var gameUpdated = new Baseline.BaselineService(store, gameDir).GameUpdatedSinceBaseline();
            checks.AddRange(DeployChecks.Layout(winners, untracked, gameUpdated, _rules));
            if (!ignoreBlockingChecks)
                blockers.AddRange(checks.Where(c => c.Severity == Severity.Block).Select(c => c.Message + (c.Hint is null ? "" : " " + c.Hint)));
        }

        var deployed = store.GetDeployed().ToDictionary(d => d.DestPath, RelPath.Comparer);
        var backups = store.GetBackups().ToDictionary(b => b.DestPath, RelPath.Comparer);
        var removes = new List<Step>();
        var restores = new List<Step>();
        var backupSteps = new List<Step>();
        var places = new List<Step>();

        foreach (var cur in deployed.Values)
        {
            desired.TryGetValue(cur.DestPath, out var want);
            var same = want is not null && want.ModId == cur.ModId && RelPath.Comparer.Equals(want.LibraryPath, cur.LibraryPath) && want.Sha256 == cur.Sha256;
            if (same) continue;
            removes.Add(new Step(StepType.Remove, cur.DestPath, cur.ModId, cur.LibraryPath, Sha256: cur.Sha256));
            if (want is null && backups.TryGetValue(cur.DestPath, out var b))
                restores.Add(new Step(StepType.Restore, cur.DestPath, BackupPath: b.BackupPath, Sha256: b.Sha256));
        }

        var reservedBackupPaths = new HashSet<string>(backups.Values.Select(b => b.BackupPath), RelPath.Comparer);
        foreach (var want in desired.Values.OrderBy(d => d.Dest, RelPath.Comparer))
        {
            if (deployed.TryGetValue(want.Dest, out var cur) && want.ModId == cur.ModId &&
                RelPath.Comparer.Equals(want.LibraryPath, cur.LibraryPath) && want.Sha256 == cur.Sha256)
                continue;
            if (!File.Exists(want.Source)) { blockers.Add($"{want.ModId}: library file missing: {want.Source}"); continue; }

            // A file that's there and not ours is an original (or a stray). Back it up; never overwrite it.
            if (!deployed.ContainsKey(want.Dest) && File.Exists(GamePath(want.Dest)))
            {
                var bp = UniqueBackupPath(want.Dest, reservedBackupPaths);
                backupSteps.Add(new Step(StepType.Backup, want.Dest, want.ModId, BackupPath: bp, Note: "original replaced by a mod"));
            }
            places.Add(new Step(StepType.Place, want.Dest, want.ModId, want.LibraryPath, want.Source, want.Sha256,
                Method: want.Linked ? "copy" : "hardlink"));
        }

        var steps = removes.Concat(restores).Concat(backupSteps).Concat(places).ToList();
        return new DeployPlan(purge ? "purge" : "deploy", steps, checks, blockers) { LeftOut = leftOut };
    }

    private string UniqueBackupPath(string dest, HashSet<string> reserved)
    {
        var bp = RelPath.ToFull(BackupsDir, dest);
        for (var i = 1; reserved.Contains(bp) || File.Exists(bp); i++)
            bp = RelPath.ToFull(BackupsDir, dest) + $".{i}";
        reserved.Add(bp);
        return bp;
    }

    // ---------- applying ----------

    public ApplyResult Apply(DeployPlan plan)
    {
        if (!plan.CanApply) throw new DeployException("Plan has blockers:\n- " + string.Join("\n- ", plan.Blockers));
        if (store.GetPendingOps().Count > 0) throw new DeployException("An earlier operation didn't finish. Run recovery first.");
        if (plan.IsEmpty) return new ApplyResult(0, 0, 0, 0, 0, []);

        var opId = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{plan.Kind}";
        store.CreateOp(opId, plan.Kind, plan.Steps.Select(s => JsonSerializer.Serialize(s, Json)).ToList());
        return Run(opId);
    }

    /// <summary>Finishes any operation a crash interrupted. Steps are idempotent, so this rolls forward.</summary>
    public List<ApplyResult> Recover() => store.GetPendingOps().Select(Run).ToList();

    private ApplyResult Run(string opId)
    {
        int placed = 0, removed = 0, backedUp = 0, restored = 0, rescued = 0, skipped = 0;
        var notes = new List<string>();
        var allSteps = store.GetSteps(opId).Select(r => (Row: r, Step: JsonSerializer.Deserialize<Step>(r.Payload, Json)!)).ToList();
        // Prune folders for every step in the op, including ones a crashed run already finished.
        var touchedDirs = allSteps.Where(x => x.Step.Type is StepType.Remove or StepType.Backup)
            .Select(x => RelPath.Dir(x.Step.Dest)).ToHashSet(RelPath.Comparer);

        foreach (var (row, step) in allSteps.Where(x => x.Row.Status != "done"))
        {
            Action commit = step.Type switch
            {
                StepType.Remove => DoRemove(step, notes, ref rescued),
                StepType.Restore => DoRestore(step),
                StepType.Backup => DoBackup(step),
                StepType.Place => DoPlace(step, notes, ref skipped),
                _ => throw new InvalidOperationException(step.Type.ToString()),
            };
            AfterFileOp?.Invoke(step);
            using (var tx = store.Begin())
            {
                commit();
                store.MarkStepDone(opId, row.Step);
                tx.Commit();
            }
            switch (step.Type)
            {
                case StepType.Remove: removed++; break;
                case StepType.Restore: restored++; break;
                case StepType.Backup: backedUp++; break;
                case StepType.Place: placed++; break;
            }
        }
        store.CompleteOp(opId);
        PruneEmptyDirs(touchedDirs);
        return new ApplyResult(placed - skipped, removed, backedUp, restored, rescued, notes, skipped);
    }

    private Action DoRemove(Step s, List<string> notes, ref int rescued)
    {
        var full = GamePath(s.Dest);
        if (File.Exists(full))
        {
            var rec = store.GetDeployed().FirstOrDefault(d => RelPath.Comparer.Equals(d.DestPath, s.Dest));
            var fi = new FileInfo(full);
            var untouched = rec is not null && fi.Length == rec.Size && fi.LastWriteTimeUtc.Ticks == rec.MTime;
            if (!untouched)
            {
                var sha = FileOps.Sha256(full);
                if (sha != s.Sha256 && Rescue(s, full, sha, notes)) rescued++;
            }
            File.Delete(full);
        }
        return () => store.RemoveDeployed(s.Dest);
    }

    /// <summary>A deployed file changed in the game folder (e.g. you edited a mod's .ini). Keep that edit; never destroy it.</summary>
    private bool Rescue(Step s, string full, string sha, List<string> notes)
    {
        var mod = s.ModId is null ? null : store.GetMod(s.ModId);
        if (mod is not null && !mod.IsLinked && s.LibraryPath is not null && !ModLibrary.IsGenerated(s.LibraryPath))
        {
            var lib = library.SourcePath(mod, s.LibraryPath);
            if (!File.Exists(lib) || FileOps.Sha256(lib) != sha) // a hard link already shares the edit
            {
                FileOps.EnsureParent(lib);
                File.Copy(full, lib + ".saddlebag-tmp", true);
                File.Move(lib + ".saddlebag-tmp", lib, true);
            }
            store.UpdateModFileHash(mod.Id, s.LibraryPath, sha, new FileInfo(full).Length);
            notes.Add($"{s.Dest}: changed since deploy; kept your version in the library.");
            return true;
        }
        var dest = RelPath.ToFull(Path.Combine(RescuedDir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")), s.Dest);
        FileOps.EnsureParent(dest);
        File.Copy(full, dest, true);
        notes.Add($"{s.Dest}: changed since deploy; saved a copy to {dest}.");
        return true;
    }

    private Action DoRestore(Step s)
    {
        var full = GamePath(s.Dest);
        var bp = s.BackupPath!;
        if (File.Exists(bp))
        {
            if (File.Exists(full)) throw new DeployException($"Can't restore {s.Dest}: a file is already there.");
            FileOps.EnsureParent(full);
            File.Move(bp, full);
        }
        else if (!File.Exists(full))
            throw new DeployException($"Backup of {s.Dest} is missing: {bp}");
        return () => store.RemoveBackup(s.Dest);
    }

    private Action DoBackup(Step s)
    {
        var full = GamePath(s.Dest);
        var bp = s.BackupPath!;
        string sha;
        if (File.Exists(full))
        {
            if (File.Exists(bp)) throw new DeployException($"Backup target already exists: {bp}");
            sha = FileOps.Sha256(full);
            FileOps.EnsureParent(bp);
            File.Move(full, bp);
        }
        else if (File.Exists(bp)) sha = FileOps.Sha256(bp); // moved before a crash
        else throw new DeployException($"Expected an original at {s.Dest} to back up, found nothing.");
        return () => store.AddBackup(new BackupRecord(s.Dest, bp, sha, s.ModId!));
    }

    private Action DoPlace(Step s, List<string> notes, ref int skipped)
    {
        var full = GamePath(s.Dest);
        var method = s.Method!;
        FileOps.EnsureParent(full);
        File.Delete(full + ".saddlebag-tmp"); // a copy a crash cut short
        if (File.Exists(full))
        {
            // Already there after a crash? Accept it only if it's exactly the file we meant to place.
            if (FileOps.Sha256(full) != s.Sha256)
                throw new DeployException($"Unexpected file at {s.Dest}; not overwriting it. Move it away and run again.");
        }
        else
        {
            if (!File.Exists(s.Source!) || FileOps.Sha256(s.Source!) != s.Sha256)
            {
                // The source changed after planning (a build still writing). Placing it would make the ledger lie,
                // and throwing would leave a step recovery can never finish. Skip it; the next plan places the new file.
                skipped++;
                notes.Add($"{s.Dest}: its source changed while deploying; skipped, the next deploy places the new version.");
                return () => { };
            }
            if (method == "hardlink" && !FileOps.TryHardLink(s.Source!, full))
                method = "copy";
            if (method == "copy") FileOps.AtomicCopy(s.Source!, full);
        }
        return () =>
        {
            var fi = new FileInfo(full);
            store.AddDeployed(new DeployedRecord(s.Dest, s.ModId!, s.LibraryPath!, s.Sha256!, fi.Length, fi.LastWriteTimeUtc.Ticks, method, DateTime.UtcNow));
        };
    }

    /// <summary>Removes folders a mod created once they're empty. Folders that exist in the baseline are left alone.</summary>
    private void PruneEmptyDirs(IEnumerable<string> dirs)
    {
        var baseline = store.GetBaseline();
        if (baseline.Count == 0) return; // without a baseline we can't tell a vanilla folder from a mod folder
        var vanillaDirs = new HashSet<string>(RelPath.Comparer);
        foreach (var b in baseline)
            for (var d = RelPath.Dir(b.Path); d.Length > 0; d = RelPath.Dir(d)) vanillaDirs.Add(d);

        foreach (var start in dirs)
            for (var d = start; d.Length > 0 && !vanillaDirs.Contains(d); d = RelPath.Dir(d))
            {
                var full = GamePath(d);
                if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any()) break;
                Directory.Delete(full);
            }
    }
}
