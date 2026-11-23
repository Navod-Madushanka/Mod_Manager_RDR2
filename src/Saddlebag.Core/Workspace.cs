using Saddlebag.Core.Baseline;
using Saddlebag.Core.Deploy;
using Saddlebag.Core.Library;
using Saddlebag.Core.Routing;
using Saddlebag.Core.Safety;
using Saddlebag.Core.State;
using Saddlebag.Core.Util;

namespace Saddlebag.Core;

public sealed record OnlineResult(ApplyResult Purge, VerifyReport Report);

/// <summary>Everything Saddlebag knows about one game install. The CLI and the GUI are thin shells over this.</summary>
public sealed class Workspace : IDisposable
{
    public string Home { get; }
    public string GameDir { get; }
    public StateStore Store { get; }
    public ModLibrary Library { get; }
    public BaselineService Baseline { get; }
    public Deployer Deployer { get; }
    public IGameGuard Guard { get; }

    private Workspace(string home, string gameDir, StateStore store, IGameGuard guard)
    {
        Home = home;
        GameDir = gameDir;
        Store = store;
        Guard = guard;
        Library = new ModLibrary(store, home, new Router());
        Baseline = new BaselineService(store, gameDir);
        Deployer = new Deployer(store, Library, home, gameDir);
    }

    public static string DbPath(string home) => Path.Combine(home, "state.db");

    public static Workspace Init(string home, string gameDir, IGameGuard? guard = null)
    {
        home = Path.GetFullPath(home);
        gameDir = Path.GetFullPath(gameDir);
        if (!File.Exists(Path.Combine(gameDir, "RDR2.exe")))
            throw new InvalidOperationException($"RDR2.exe not found in '{gameDir}'. Point --game at the folder that holds RDR2.exe.");
        if (IsInside(home, gameDir) || IsInside(gameDir, home))
            throw new InvalidOperationException("Keep Saddlebag's folder and the game folder separate: the game folder is a deploy target, never storage.");
        Directory.CreateDirectory(home);
        var store = new StateStore(DbPath(home));
        var existing = store.GetSetting("game_dir");
        if (existing is not null && !string.Equals(existing, gameDir, StringComparison.OrdinalIgnoreCase))
        {
            store.Dispose();
            throw new InvalidOperationException($"This Saddlebag folder already manages '{existing}'.");
        }
        store.SetSetting("game_dir", gameDir);
        return new Workspace(home, gameDir, store, guard ?? new ProcessGameGuard());
    }

    public static Workspace Open(string home, IGameGuard? guard = null)
    {
        home = Path.GetFullPath(home);
        if (!File.Exists(DbPath(home))) throw new InvalidOperationException($"No Saddlebag state in '{home}'. Run `saddlebag init` first.");
        var store = new StateStore(DbPath(home));
        var game = store.GetSetting("game_dir") ?? throw new InvalidOperationException("state.db has no game folder set.");
        return new Workspace(home, game, store, guard ?? new ProcessGameGuard());
    }

    public void Dispose() => Store.Dispose();

    private static bool IsInside(string child, string parent)
    {
        var rel = Path.GetRelativePath(parent, child);
        return rel == "." || (!rel.StartsWith("..") && !Path.IsPathRooted(rel));
    }

    /// <summary>True when hard links work between the library and the game folder (same volume, NTFS).</summary>
    public bool HardLinksWork()
    {
        var probe = Path.Combine(Home, ".linkprobe");
        var link = Path.Combine(GameDir, ".saddlebag-linkprobe");
        try
        {
            File.WriteAllText(probe, "probe");
            File.Delete(link);
            return FileOps.TryHardLink(probe, link);
        }
        catch { return false; }
        finally
        {
            try { File.Delete(link); } catch { }
            try { File.Delete(probe); } catch { }
        }
    }

    /// <summary>Guard, then finish anything a crash left half-done. Every command that touches files calls this first.</summary>
    public List<ApplyResult> Prepare()
    {
        Guard.ThrowIfRunning();
        return Deployer.Recover();
    }

    public ApplyResult Deploy(bool ignoreBlockingChecks = false)
    {
        Prepare();
        return Deployer.Apply(Deployer.Plan(ignoreBlockingChecks: ignoreBlockingChecks));
    }

    public ApplyResult Purge()
    {
        Prepare();
        return Deployer.Apply(Deployer.Plan(purge: true));
    }

    /// <summary>Online mode: guard, purge every tracked file, restore originals, then prove the folder matches the baseline.</summary>
    public OnlineResult GoOnline(bool deep = false)
    {
        if (Store.BaselineCount() == 0)
            throw new InvalidOperationException("No baseline yet, so there's nothing to verify against. Run `saddlebag baseline capture` on a clean install.");
        Prepare();
        var purge = Deployer.Apply(Deployer.Plan(purge: true));
        var report = Baseline.Verify(deep);
        Store.SetSetting("mode", "online");
        return new OnlineResult(purge, report);
    }

    /// <summary>Story mode: put the enabled mods back. Only files that differ from the ledger move.</summary>
    public ApplyResult GoStory(bool ignoreBlockingChecks = false)
    {
        var r = Deploy(ignoreBlockingChecks);
        Store.SetSetting("mode", "story");
        return r;
    }

    /// <summary>
    /// Makes another profile active. In Story mode it deploys straight away, and because deploy is a diff against
    /// the ledger, only files that differ between the two profiles move. In Online mode it only switches.
    /// </summary>
    public ApplyResult? SwitchProfile(string idOrName, bool deploy = true)
    {
        var p = Store.FindProfile(idOrName) ?? throw new InvalidOperationException($"No profile '{idOrName}'. See `saddlebag profile list`.");
        Prepare();
        Store.SetActiveProfile(p.Id);
        if (!deploy || Store.GetSetting("mode") == "online") return null;
        return Deployer.Apply(Deployer.Plan());
    }

    public sealed record AdoptResult(Library.ImportResult Import, IReadOnlyList<string> ReplacedVanilla);

    /// <summary>
    /// Adopts untracked files (game-relative files or folders) as a mod in the active profile. The files leave the
    /// game folder; `story` puts them back as tracked links. Unchanged vanilla files and deployed files are refused.
    /// </summary>
    public AdoptResult Adopt(IEnumerable<string> paths, string name)
    {
        Prepare();
        var deployed = Store.GetDeployed().Select(d => d.DestPath).ToHashSet(RelPath.Comparer);
        var baseline = Store.GetBaseline().ToDictionary(b => b.Path, RelPath.Comparer);
        var files = new List<string>();
        foreach (var raw in paths)
        {
            var rel = RelPath.Normalize(raw);
            if (!RelPath.IsSafe(rel)) throw new InvalidOperationException($"'{raw}' isn't a path inside the game folder.");
            var full = RelPath.ToFull(GameDir, rel);
            if (Directory.Exists(full))
                files.AddRange(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Select(f => RelPath.From(GameDir, f)));
            else if (File.Exists(full)) files.Add(rel);
            else throw new FileNotFoundException($"'{rel}' isn't in the game folder.");
        }
        files = files.Distinct(RelPath.Comparer).ToList();

        var replaced = new List<string>();
        foreach (var f in files)
        {
            if (deployed.Contains(f)) throw new InvalidOperationException($"{f} is already tracked as part of a mod.");
            if (RelPath.Comparer.Equals(f, "RDR2.exe")) throw new InvalidOperationException("RDR2.exe can't be adopted.");
            if (baseline.TryGetValue(f, out var b))
            {
                var fi = new FileInfo(RelPath.ToFull(GameDir, f));
                var same = fi.Length == b.Size && (fi.LastWriteTimeUtc.Ticks == b.MTime || FileOps.Sha256(fi.FullName) == b.Sha256);
                if (same) throw new InvalidOperationException($"{f} is an unchanged vanilla file; there's nothing to adopt.");
                replaced.Add(f);
            }
        }
        var import = Library.Adopt(GameDir, files, name);
        Store.SetEnabled(import.Mod.Id, true);
        return new AdoptResult(import, replaced);
    }

    public void CaptureBaseline(bool force, IProgress<(int, int, string)>? progress = null)
    {
        Prepare();
        if (Store.GetDeployed().Count > 0)
            throw new InvalidOperationException("Mods are deployed. Run `saddlebag purge`, verify game files in your launcher, then capture.");
        var sigs = Baseline.FindSignatures();
        if (sigs.Count > 0 && !force)
            throw new InvalidOperationException("This folder doesn't look clean; found: " + string.Join(", ", sigs.Take(8)) +
                (sigs.Count > 8 ? $" and {sigs.Count - 8} more" : "") + ". Move them out and verify in your launcher, or pass --force.");
        Baseline.Capture(progress);
    }
}
