using System.Text;
using System.Text.RegularExpressions;
using Saddlebag.Core.State;
using Saddlebag.Core.Util;

namespace Saddlebag.Core.Logs;

public enum LogLevel { Info, Warn, Error }

/// <summary>A log file in the game folder, or one Saddlebag archived out of it.</summary>
/// <param name="Display">Game-relative path, or "archive/&lt;stamp&gt;/&lt;path&gt;" for archived logs.</param>
/// <param name="OwnerModId">The mod that probably wrote it, from file names (e.g. Trainer.log beside Trainer.asi).</param>
public sealed record LogFile(string Display, string FullPath, string GamePath, long Size, DateTime ModifiedUtc,
    string? OwnerModId, string? OwnerName, bool Archived, bool Vanilla);

public sealed record LogLine(int Number, string Text, LogLevel Level, IReadOnlyList<string> MentionedMods);

public sealed record LogSummary(LogFile File, int Errors, int Warnings, IReadOnlyDictionary<string, int> ModMentions, string? LastError);

/// <summary>
/// Finds and reads hook and mod logs. Log names aren't hard-coded: every *.log in the game root, lml/ and scripts/
/// is a candidate, and the owning mod is inferred from the files that mod deployed.
/// </summary>
public sealed partial class LogService(StateStore store, string gameDir, string home, Routing.RuleSet? rules = null)
{
    private readonly Routing.RuleSet _rules = rules ?? Routing.RuleSet.Default;
    private static readonly string[] Areas = ["lml", "scripts"];
    public string ArchiveDir => Path.Combine(home, "logs");

    [GeneratedRegex(@"\b(error|errors|exception|fatal|failed|failure|crash(ed)?|unhandled|access violation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorWords();

    [GeneratedRegex(@"\b(warn|warning|warnings)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarnWords();

    public static LogLevel Classify(string line) =>
        ErrorWords().IsMatch(line) ? LogLevel.Error : WarnWords().IsMatch(line) ? LogLevel.Warn : LogLevel.Info;

    // ---------- discovery ----------

    private sealed record OwnerHint(string Stem, string Dir, string ModId, string ModName);

    private List<OwnerHint> OwnerHints()
    {
        var mods = store.GetMods().ToDictionary(m => m.Id, m => m.Name);
        var dests = store.GetDeployed().Select(d => (d.DestPath, d.ModId))
            .Concat(mods.Keys.SelectMany(id => store.GetModFiles(id).Where(f => f.DestPath is not null).Select(f => (f.DestPath!, id))));
        return dests
            .Where(d => d.Item1.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) || d.Item1.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(d => new OwnerHint(Path.GetFileNameWithoutExtension(d.Item1), RelPath.Dir(d.Item1), d.Item2, mods.GetValueOrDefault(d.Item2, d.Item2)))
            .Where(h => h.Stem.Length >= 3)
            .DistinctBy(h => (h.Stem.ToLowerInvariant(), h.Dir.ToLowerInvariant()))
            .ToList();
    }

    private static (string? Id, string? Name) GuessOwner(string gamePath, List<OwnerHint> hints, Dictionary<string, (string Id, string Name)> lmlPackages)
    {
        var stem = Path.GetFileNameWithoutExtension(gamePath);
        var dir = RelPath.Dir(gamePath);
        // Same folder and the log is named after the plugin (Trainer.log ↔ Trainer.asi, ScriptHookRDR2.log ↔ ScriptHookRDR2.dll).
        var hit = hints.Where(h => RelPath.Comparer.Equals(h.Dir, dir) && stem.StartsWith(h.Stem, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.Stem.Length).FirstOrDefault()
            ?? hints.Where(h => stem.Equals(h.Stem, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        if (hit is not null) return (hit.ModId, hit.ModName);
        // Inside an LML package folder: that package's mod.
        var segs = gamePath.Split('/');
        if (segs.Length >= 3 && segs[0].Equals("lml", StringComparison.OrdinalIgnoreCase) && lmlPackages.TryGetValue(segs[1], out var pkg))
            return (pkg.Id, pkg.Name);
        return (null, null);
    }

    private Dictionary<string, (string Id, string Name)> LmlPackages()
    {
        var names = store.GetMods().ToDictionary(m => m.Id, m => m.Name);
        return store.GetDeployed()
            .Where(d => d.DestPath.Split('/') is [var l, _, _, ..] && l.Equals("lml", StringComparison.OrdinalIgnoreCase))
            .GroupBy(d => d.DestPath.Split('/')[1], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (g.First().ModId, names.GetValueOrDefault(g.First().ModId, g.First().ModId)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Game-relative *.log files in the root, lml/ and scripts/.</summary>
    private IEnumerable<string> GameLogPaths()
    {
        if (!Directory.Exists(gameDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(gameDir, "*.log"))
            yield return RelPath.From(gameDir, f);
        foreach (var area in Areas)
        {
            var dir = Path.Combine(gameDir, area);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.log", SearchOption.AllDirectories))
                yield return RelPath.From(gameDir, f);
        }
    }

    public List<LogFile> Discover(bool includeArchived = true)
    {
        var hints = OwnerHints();
        var packages = LmlPackages();
        var baseline = store.GetBaseline().Select(b => b.Path).ToHashSet(RelPath.Comparer);
        var list = new List<LogFile>();
        foreach (var rel in GameLogPaths())
        {
            var fi = new FileInfo(RelPath.ToFull(gameDir, rel));
            var (id, name) = GuessOwner(rel, hints, packages);
            list.Add(new LogFile(rel, fi.FullName, rel, fi.Length, fi.LastWriteTimeUtc, id, name, Archived: false, Vanilla: baseline.Contains(rel)));
        }
        if (includeArchived && Directory.Exists(ArchiveDir))
            foreach (var stampDir in Directory.EnumerateDirectories(ArchiveDir))
                foreach (var f in Directory.EnumerateFiles(stampDir, "*.log", SearchOption.AllDirectories))
                {
                    var gamePath = RelPath.From(stampDir, f);
                    var fi = new FileInfo(f);
                    var (id, name) = GuessOwner(gamePath, hints, packages);
                    list.Add(new LogFile($"archive/{Path.GetFileName(stampDir)}/{gamePath}", f, gamePath, fi.Length, fi.LastWriteTimeUtc, id, name, Archived: true, Vanilla: false));
                }
        return list.OrderBy(l => l.Archived).ThenByDescending(l => l.ModifiedUtc).ToList();
    }

    /// <summary>Finds a log by file name, game path or display path. Live logs win over archived ones with the same name.</summary>
    public LogFile Find(string query)
    {
        var all = Discover();
        var q = RelPath.Normalize(query);
        var exact = all.Where(l => RelPath.Comparer.Equals(l.Display, q) || RelPath.Comparer.Equals(l.GamePath, q)).ToList();
        var matches = exact.Count > 0 ? exact
            : all.Where(l => RelPath.FileName(l.GamePath).Equals(q, StringComparison.OrdinalIgnoreCase) ||
                             RelPath.FileName(l.GamePath).StartsWith(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) throw new InvalidOperationException($"No log matching '{query}'. Run `saddlebag logs` to list them.");
        var live = matches.Where(m => !m.Archived).ToList();
        if (live.Count == 1) return live[0];
        if (live.Count == 0 && matches.Count >= 1) return matches.OrderByDescending(m => m.ModifiedUtc).First(); // newest archived copy
        throw new InvalidOperationException($"'{query}' matches {string.Join(", ", live.Select(m => m.Display))}. Use the full path.");
    }

    // ---------- reading ----------

    /// <summary>Opens a log the game may still have open for writing.</summary>
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    /// <summary>The last <paramref name="max"/> lines, read from the end so a huge log isn't loaded whole.</summary>
    public static List<string> Tail(string path, int max)
    {
        using var fs = OpenShared(path);
        var chunk = 64 * 1024;
        var pos = fs.Length;
        var buffer = new List<byte>();
        var newlines = 0;
        while (pos > 0 && newlines <= max)
        {
            var size = (int)Math.Min(chunk, pos);
            pos -= size;
            var bytes = new byte[size];
            fs.Position = pos;
            fs.ReadExactly(bytes);
            newlines += bytes.Count(b => b == (byte)'\n');
            buffer.InsertRange(0, bytes);
        }
        var lines = Decode(buffer.ToArray(), atStart: pos == 0).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (pos > 0 && lines.Count > 0) lines.RemoveAt(0); // first line is probably cut in half
        return lines.Count > max ? lines[^max..] : lines;
    }

    /// <summary>UTF-8 by default; UTF-16 when the file starts with its BOM (some Windows tools write that).</summary>
    internal static string Decode(byte[] bytes, bool atStart)
    {
        if (atStart && bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (atStart && bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Mod names whose file names appear in a line, e.g. an exception naming HorsePersonalities.dll.</summary>
    public Func<string, IReadOnlyList<string>> MentionMatcher()
    {
        var mods = store.GetMods().ToDictionary(m => m.Id, m => m.Name);
        var stems = mods.Keys
            .SelectMany(id => store.GetModFiles(id).Where(f => f.DestPath is not null)
                .Select(f => (Stem: Path.GetFileNameWithoutExtension(f.DestPath!), Name: mods[id])))
            .Where(s => s.Stem.Length >= 4 && !_rules.Frameworks.Any(fw => Path.GetFileNameWithoutExtension(fw).Equals(s.Stem, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(s => s.Stem.ToLowerInvariant())
            .ToList();
        return line => stems.Where(s => line.Contains(s.Stem, StringComparison.OrdinalIgnoreCase)).Select(s => s.Name).Distinct().ToList();
    }

    /// <summary>The last <paramref name="tail"/> matching lines, numbered by their line in the file.</summary>
    public List<LogLine> Read(LogFile log, int tail, bool errorsOnly = false, string? grep = null)
    {
        var mentions = MentionMatcher();
        var lines = Tail(log.FullPath, 2_000_000); // whole file in practice, so line numbers are real
        var result = lines.Select((t, i) => (t, i))
            .Where(x => grep is null || x.t.Contains(grep, StringComparison.OrdinalIgnoreCase))
            .Select(x => new LogLine(x.i + 1, x.t, Classify(x.t), mentions(x.t)))
            .Where(l => !errorsOnly || l.Level == LogLevel.Error)
            .ToList();
        return result.Count > tail ? result[^tail..] : result;
    }

    public LogSummary Summarize(LogFile log)
    {
        var mentions = MentionMatcher();
        int errors = 0, warns = 0;
        string? last = null;
        var counts = new Dictionary<string, int>();
        foreach (var line in Tail(log.FullPath, 20000))
        {
            var level = Classify(line);
            if (level == LogLevel.Warn) warns++;
            if (level != LogLevel.Error) continue;
            errors++;
            last = line;
            foreach (var m in mentions(line)) counts[m] = counts.GetValueOrDefault(m) + 1;
        }
        return new LogSummary(log, errors, warns, counts, last);
    }

    // ---------- archiving ----------

    /// <summary>
    /// Moves logs mods wrote (not vanilla, not tracked by a mod) out of the game folder into home/logs/&lt;stamp&gt;/.
    /// They'd otherwise turn Online mode red. Nothing is deleted.
    /// </summary>
    public List<string> ArchiveModLogs()
    {
        var tracked = store.GetDeployed().Select(d => d.DestPath).ToHashSet(RelPath.Comparer);
        var rules = store.GetPathRules();
        var logs = Discover(includeArchived: false).Where(l => !l.Vanilla && !tracked.Contains(l.GamePath) && !rules.ContainsKey(l.GamePath)).ToList();
        if (logs.Count == 0) return [];
        var stampDir = Path.Combine(ArchiveDir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        foreach (var l in logs)
        {
            var dest = RelPath.ToFull(stampDir, l.GamePath);
            FileOps.EnsureParent(dest);
            File.Move(l.FullPath, dest);
        }
        return logs.Select(l => l.GamePath).ToList();
    }
}

/// <summary>Follows a log as it grows (tail -f). Handles the game recreating or truncating the file on start.</summary>
public sealed class LogFollower(string path, bool fromEnd = true)
{
    private long _pos = -1;
    private readonly bool _fromEnd = fromEnd;
    private string _partial = "";

    public sealed record Chunk(IReadOnlyList<string> Lines, bool Restarted);

    /// <summary>Returns lines written since the last poll. Partial lines wait until their newline arrives.</summary>
    public Chunk Poll()
    {
        if (!File.Exists(path)) return new([], false);
        using var fs = LogService.OpenShared(path);
        var restarted = false;
        if (_pos < 0) _pos = _fromEnd ? fs.Length : 0;
        else if (fs.Length < _pos) { _pos = 0; _partial = ""; restarted = true; }
        if (fs.Length == _pos) return new([], restarted);

        fs.Position = _pos;
        var bytes = new byte[fs.Length - _pos];
        fs.ReadExactly(bytes);
        var text = _partial + LogService.Decode(bytes, atStart: _pos == 0);
        _pos = fs.Length;
        var parts = text.Split('\n');
        _partial = parts[^1];
        return new(parts[..^1].Select(l => l.TrimEnd('\r')).ToList(), restarted);
    }
}
