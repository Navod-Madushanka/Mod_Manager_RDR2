using System.Text.Json;
using Saddlebag.Core.Baseline;
using Saddlebag.Core.Bisect;
using Saddlebag.Core.Deploy;
using Saddlebag.Core.Library;
using Saddlebag.Core.Routing;
using Saddlebag.Core.Safety;
using Saddlebag.Core.State;
using Saddlebag.Core.Util;

namespace Saddlebag.Core;

/// <param name="ArchivedLogs">Logs mods wrote, moved out of the game folder into home/logs/ so they don't turn Online red.</param>
public sealed record OnlineResult(ApplyResult Purge, VerifyReport Report, IReadOnlyList<string>? ArchivedLogs = null);

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
    public Logs.LogService Logs { get; }

    private Workspace(string home, string gameDir, StateStore store, IGameGuard guard)
    {
        Home = home;
        GameDir = gameDir;
        Store = store;
        Guard = guard;
        Library = new ModLibrary(store, home, new Router());
        Baseline = new BaselineService(store, gameDir);
        Deployer = new Deployer(store, Library, home, gameDir);
        Logs = new Logs.LogService(store, gameDir, home);
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
        // Logs first: purge then prunes the mod folders they sat in (lml/, scripts/).
        var archived = Logs.ArchiveModLogs();
        var purge = Deployer.Apply(Deployer.Plan(purge: true));
        PruneEmptyModFolders();
        var report = Baseline.Verify(deep);
        Store.SetSetting("mode", "online");
        return new OnlineResult(purge, report, archived);
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
        ThrowIfBisecting();
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

    // ---------- crash bisect ----------

    private const string BisectKey = "bisect";
    private static readonly JsonSerializerOptions BisectJson = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Hooks and loaders other mods depend on; turning them off would block or break the rest.</summary>
    public static readonly string[] HookFiles = ["ScriptHookRDR2.dll", "vfs.asi", "ScriptHookRDRDotNet.asi"];

    public BisectSession? LoadBisect() =>
        Store.GetSetting(BisectKey) is { Length: > 0 } json ? JsonSerializer.Deserialize<BisectSession>(json, BisectJson) : null;

    private void SaveBisect(BisectSession? s) => Store.SetSetting(BisectKey, s is null ? "" : JsonSerializer.Serialize(s, BisectJson));

    public void ThrowIfBisecting()
    {
        if (LoadBisect() is not null)
            throw new InvalidOperationException("A crash bisect is running. Finish it, or `saddlebag bisect reset` to go back to your profile.");
    }

    /// <summary>Mods that ship a hook (Script Hook, LML, the .NET hook) and stay on by default.</summary>
    public bool IsHookMod(string modId) =>
        Store.GetModFiles(modId).Any(f => f.DestPath is not null && HookFiles.Contains(f.DestPath, RelPath.Comparer));

    /// <summary>
    /// Starts a bisect over the active profile's enabled mods. Works in a temporary copy of the profile, so your
    /// own profile is never edited, and deploys the first half to test.
    /// </summary>
    public (BisectSession Session, ApplyResult Applied) BisectStart(IEnumerable<string>? keep = null, IEnumerable<string>? suspect = null)
    {
        ThrowIfBisecting();
        Prepare();
        var original = Store.ActiveProfile;
        var enabled = Store.GetProfileMods(original).Where(p => p.Enabled).Select(p => p.ModId).ToList();
        var forceKeep = (keep ?? []).Select(k => Library.Require(k).Id).ToHashSet();
        var forceSuspect = (suspect ?? []).Select(k => Library.Require(k).Id).ToHashSet();
        foreach (var id in forceKeep.Concat(forceSuspect))
            if (!enabled.Contains(id)) throw new InvalidOperationException($"{id} isn't enabled in this profile.");

        bool Kept(string id) => forceKeep.Contains(id) || (!forceSuspect.Contains(id) && IsHookMod(id));
        var session = BisectSession.Start(enabled.Where(id => !Kept(id)).ToList(), enabled.Where(Kept).ToList());
        session.OriginalProfile = original;
        session.OriginalMode = Store.GetSetting("mode");
        session.BisectProfile = "bisect-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        Store.CreateProfile(session.BisectProfile, "Crash bisect", copyFrom: original);
        try
        {
            var applied = ApplyBisect(session);
            SaveBisect(session);
            return (session, applied);
        }
        catch
        {
            Store.SetActiveProfile(original);
            Store.DeleteProfile(session.BisectProfile);
            throw;
        }
    }

    /// <summary>Records whether the game crashed with the current set, then deploys the next set to test.</summary>
    public (BisectSession Session, ApplyResult Applied) BisectAnswer(bool crashed)
    {
        var s = LoadBisect() ?? throw new InvalidOperationException("No bisect running. Start one with `saddlebag bisect start`.");
        Prepare();
        s.Answer(crashed);
        // Save only once the next set is deployed: if it can't deploy, the answer isn't recorded and you can retry.
        var applied = s.IsDone ? new ApplyResult(0, 0, 0, 0, 0, []) : ApplyBisect(s);
        SaveBisect(s);
        return (s, applied);
    }

    public (BisectSession Session, ApplyResult Applied) BisectUndo()
    {
        var s = LoadBisect() ?? throw new InvalidOperationException("No bisect running.");
        Prepare();
        s.Undo();
        var applied = ApplyBisect(s);
        SaveBisect(s);
        return (s, applied);
    }

    /// <summary>Ends the bisect: back to your own profile, redeployed as it was (optionally with the culprit disabled).</summary>
    public ApplyResult BisectReset(bool disableCulprit = false)
    {
        var s = LoadBisect() ?? throw new InvalidOperationException("No bisect running.");
        Prepare();
        if (disableCulprit)
        {
            if (s.Culprit is null) throw new InvalidOperationException("The bisect hasn't found a culprit to disable.");
            Store.SetEnabled(s.Culprit, false, s.OriginalProfile);
        }
        Store.SetActiveProfile(s.OriginalProfile);
        var r = s.OriginalMode == "online"
            ? Deployer.Apply(Deployer.Plan(purge: true))
            : Deployer.Apply(Deployer.Plan());
        Store.SetSetting("mode", s.OriginalMode ?? "story");
        Store.DeleteProfile(s.BisectProfile);
        SaveBisect(null);
        return r;
    }

    private ApplyResult ApplyBisect(BisectSession s)
    {
        var on = s.Enabled.ToHashSet();
        foreach (var pm in Store.GetProfileMods(s.BisectProfile))
            Store.SetEnabled(pm.ModId, on.Contains(pm.ModId), s.BisectProfile);
        Store.SetActiveProfile(s.BisectProfile);
        var plan = Deployer.Plan();
        if (!plan.CanApply)
            throw new DeployException("This test set can't deploy:\n- " + string.Join("\n- ", plan.Blockers) +
                "\nThat answer wasn't recorded. Keep the mod they depend on enabled throughout: `saddlebag bisect reset`, then `bisect start --keep <mod>`.");
        var r = Deployer.Apply(plan);
        Store.SetSetting("mode", "story");
        return r;
    }

    /// <summary>lml/ and scripts/ left empty (e.g. after their logs were archived and nothing was deployed). Not in the baseline, so not vanilla.</summary>
    private void PruneEmptyModFolders()
    {
        var baselineDirs = Store.GetBaseline().SelectMany(b => Enumerable.Range(1, b.Path.Count(c => c == '/'))
            .Select(n => string.Join('/', b.Path.Split('/').Take(n)))).ToHashSet(RelPath.Comparer);
        foreach (var area in BaselineService.SignatureFolders)
        {
            var full = Path.Combine(GameDir, area);
            if (!Directory.Exists(full) || baselineDirs.Contains(area)) continue;
            foreach (var d in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            if (!Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
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
