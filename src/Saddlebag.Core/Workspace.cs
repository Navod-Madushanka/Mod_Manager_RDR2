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

    public ApplyResult Deploy()
    {
        Prepare();
        return Deployer.Apply(Deployer.Plan());
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
    public ApplyResult GoStory()
    {
        var r = Deploy();
        Store.SetSetting("mode", "story");
        return r;
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
