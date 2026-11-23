using System.Runtime.CompilerServices;
using Saddlebag.Core;
using Saddlebag.Core.Baseline;
using Saddlebag.Core.Deploy;

namespace Saddlebag.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Fixture DLLs can't carry real version resources, so a file whose text starts with "ver:1.2.3" reports that
    /// version. Real PEs still go through FileVersionInfo. Installed once, before any test runs.
    /// </summary>
    [ModuleInitializer]
    internal static void Init()
    {
        var real = DeployChecks.FileVersion;
        DeployChecks.FileVersion = path =>
        {
            try
            {
                using var r = new StreamReader(path);
                var buf = new char[32];
                var n = r.Read(buf, 0, buf.Length);
                var head = new string(buf, 0, n);
                if (head.StartsWith("ver:")) return head[4..].Split(' ', '\n')[0];
            }
            catch { }
            return real(path);
        };
    }
}

/// <summary>Phase 2 done-when: every conflicting fixture pair is flagged, and a profile switch moves only the diff.</summary>
public class Phase2Tests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private Workspace Ready()
    {
        var ws = _sb.Init();
        ws.CaptureBaseline(false);
        return ws;
    }

    private string Enable(Workspace ws, string name, params (string, string)[] files)
    {
        var id = ws.Library.Import(_sb.Zip(name, files)).Mod.Id;
        ws.Store.SetEnabled(id, true);
        return id;
    }

    private static IEnumerable<string> Ids(DeployPlan p) => p.Checks.Select(c => c.Id);

    // ---------- conflict pairs ----------

    [Fact]
    public void Same_stream_name_in_different_folders_is_flagged()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws, lml: true);
        Enable(ws, "loose-horse", ("horse.ytd", "A"));                                          // → lml/stream/horse.ytd
        Enable(ws, "packaged-horse", ("Horse/install.xml", "<x/>"), ("Horse/stream/horse.ytd", "B")); // → lml/Horse/stream/horse.ytd

        var plan = ws.Deployer.Plan();
        var f = Assert.Single(plan.Checks, c => c.Id == DeployChecks.StreamName);
        Assert.Contains("horse.ytd", f.Message);
        Assert.Contains("packaged-horse", f.Hint);
        Assert.DoesNotContain(DeployChecks.SameDest, Ids(plan)); // different paths, so only the name check catches it
        Assert.True(plan.CanApply);
    }

    [Fact]
    public void Identical_copies_are_not_a_conflict()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws, lml: true);
        Enable(ws, "pack-a", ("horse.ytd", "same"));
        Enable(ws, "pack-b", ("horse.ytd", "same"));
        Assert.DoesNotContain(DeployChecks.SameDest, Ids(ws.Deployer.Plan()));
    }

    [Fact]
    public void Bundled_loader_never_downgrades_the_shared_slot()
    {
        using var ws = Ready();
        Enable(ws, "scripthook", ("ScriptHookRDR2.dll", "ver:1.0.1491.17 hook"), ("dinput8.dll", "ver:1.0.0.5 loader"));
        Enable(ws, "trainer", ("Trainer.asi", "asi"), ("dinput8.dll", "ver:1.0.0.1 old loader")); // enabled later: higher priority

        var plan = ws.Deployer.Plan();
        var f = Assert.Single(plan.Checks, c => c.Id == DeployChecks.SharedSlot);
        Assert.Contains("using scripthook's copy (v1.0.0.5)", f.Message);
        Assert.DoesNotContain(DeployChecks.SameDest, Ids(plan));
        ws.Deployer.Apply(plan);
        Assert.Equal("ver:1.0.0.5 loader", File.ReadAllText(_sb.GameFile("dinput8.dll")));
    }

    [Fact]
    public void Two_asi_loaders_warn()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws);
        Enable(ws, "old-loader", ("version.dll", "old asi loader"));
        var f = Assert.Single(ws.Deployer.Plan().Checks, c => c.Id == DeployChecks.TwoAsiLoaders);
        Assert.Equal(Severity.Warn, f.Severity);
    }

    [Fact]
    public void Hand_installed_loader_in_game_folder_counts_for_two_loader_check()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws);
        Sandbox.Write(_sb.Game, "version.dll", "put there by hand");
        Assert.Contains(DeployChecks.TwoAsiLoaders, Ids(ws.Deployer.Plan()));
    }

    // ---------- missing dependencies ----------

    [Fact]
    public void Lml_content_without_lml_blocks()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws);
        Enable(ws, "saddles", ("Saddles/install.xml", "<x/>"), ("Saddles/stream/s.ytd", "s"));
        var plan = ws.Deployer.Plan();
        Assert.Contains(DeployChecks.MissingLml, Ids(plan));
        Assert.False(plan.CanApply);
        Assert.Throws<DeployException>(() => ws.Deploy());
        Assert.False(File.Exists(_sb.GameFile("lml/Saddles/stream/s.ytd")));
    }

    [Fact]
    public void Asi_without_loader_or_script_hook_blocks()
    {
        using var ws = Ready();
        Enable(ws, "trainer", ("Trainer.asi", "asi"));
        var plan = ws.Deployer.Plan();
        Assert.Contains(DeployChecks.MissingAsiLoader, Ids(plan));
        Assert.Contains(DeployChecks.MissingScriptHook, Ids(plan));
        Assert.False(plan.CanApply);

        _sb.EnableFrameworks(ws);
        Assert.True(ws.Deployer.Plan().CanApply);
    }

    [Fact]
    public void DotNet_script_without_dotnet_hook_blocks()
    {
        using var ws = Ready();
        Enable(ws, "scripthook", ("ScriptHookRDR2.dll", "hook"), ("dinput8.dll", "loader"));
        var dir = _sb.Folder("horse");
        Sandbox.WriteBytes(dir, "HorsePersonalities.dll", Dlls.Script);
        ws.Store.SetEnabled(ws.Library.Import(dir).Mod.Id, true);

        var f = Assert.Single(ws.Deployer.Plan().Checks, c => c.Id == DeployChecks.MissingDotNetHook);
        Assert.Contains("horse", f.Message);
    }

    [Fact]
    public void Blocking_checks_can_be_overridden_explicitly()
    {
        using var ws = Ready();
        Enable(ws, "standalone", ("Standalone.asi", "no hook needed"));
        Assert.Throws<DeployException>(() => ws.Deploy());
        ws.Deploy(ignoreBlockingChecks: true);
        Assert.True(File.Exists(_sb.GameFile("Standalone.asi")));
    }

    [Fact]
    public void Loader_installed_by_hand_satisfies_the_dependency()
    {
        using var ws = Ready();
        Sandbox.Write(_sb.Game, "dinput8.dll", "by hand");
        Sandbox.Write(_sb.Game, "ScriptHookRDR2.dll", "by hand");
        Enable(ws, "trainer", ("Trainer.asi", "asi"));
        Assert.True(ws.Deployer.Plan().CanApply);
    }

    [Fact]
    public void DotNet_hook_and_api_from_different_releases_warn()
    {
        using var ws = Ready();
        Enable(ws, "dotnet", ("ScriptHookRDR2.dll", "h"), ("dinput8.dll", "l"),
            ("ScriptHookRDRDotNet.asi", "ver:1.0.5.0 hook"), ("ScriptHookRDRNetAPI.dll", "ver:1.0.4.0 api"));
        var f = Assert.Single(ws.Deployer.Plan().Checks, c => c.Id == DeployChecks.DotNetMismatch);
        Assert.Contains("v1.0.5.0", f.Message);
        Assert.Contains("v1.0.4.0", f.Message);
    }

    [Fact]
    public void Game_update_shows_in_plan()
    {
        using var ws = Ready();
        File.WriteAllText(_sb.GameFile("RDR2.exe"), "patched");
        Assert.Contains(DeployChecks.GameUpdated, Ids(ws.Deployer.Plan()));
    }

    // ---------- profiles ----------

    [Fact]
    public void Profile_switch_moves_only_the_diff()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws, lml: true);
        var shared = Enable(ws, "shared", ("shared.ytd", "S"));
        var full = Enable(ws, "full-only", ("full.ytd", "F"));
        ws.GoStory();

        ws.Store.CreateProfile("story-dev", "Story-Dev", copyFrom: ws.Store.ActiveProfile);
        ws.Store.SetEnabled(full, false, "story-dev");
        var dev = ws.Library.Import(_sb.Zip("dev-only", ("dev.ytd", "D"))).Mod.Id;
        ws.Store.SetEnabled(dev, true, "story-dev");

        var before = ws.Store.GetDeployed().Where(d => d.ModId != full).ToDictionary(d => d.DestPath, d => d.DeployedAt);
        var r = ws.SwitchProfile("Story-Dev")!;

        Assert.Equal((1, 1), (r.Removed, r.Placed));
        Assert.False(File.Exists(_sb.GameFile("lml/stream/full.ytd")));
        Assert.True(File.Exists(_sb.GameFile("lml/stream/dev.ytd")));
        var after = ws.Store.GetDeployed().ToDictionary(d => d.DestPath, d => d.DeployedAt);
        foreach (var (dest, at) in before) Assert.Equal(at, after[dest]); // untouched, not re-placed
        Assert.Equal("story-dev", ws.Store.ActiveProfile);

        r = ws.SwitchProfile("default")!;
        Assert.Equal((1, 1), (r.Removed, r.Placed));
        Assert.True(File.Exists(_sb.GameFile("lml/stream/full.ytd")));
        _ = shared;
    }

    [Fact]
    public void Profile_switch_in_online_mode_only_switches()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws);
        ws.GoOnline();
        ws.Store.CreateProfile("story-full", "Story-Full", copyFrom: "default");
        Assert.Null(ws.SwitchProfile("story-full"));
        Assert.Empty(ws.Store.GetDeployed());
        Assert.Equal(VerifyStatus.Green, ws.Baseline.Verify().Status);
    }

    [Fact]
    public void Order_changes_who_wins()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws, lml: true);
        var a = Enable(ws, "horse-a", ("horse.ytd", "A"));
        Enable(ws, "horse-b", ("horse.ytd", "B"));
        ws.Deploy();
        Assert.Equal("B", File.ReadAllText(_sb.GameFile("lml/stream/horse.ytd")));

        ws.Store.MoveMod(a, ws.Store.GetProfileMods().Count); // to the top: highest priority
        Assert.Equal(a, ws.Store.GetProfileMods()[^1].ModId);
        var r = ws.Deploy();
        Assert.Equal((1, 1), (r.Removed, r.Placed));
        Assert.Equal("A", File.ReadAllText(_sb.GameFile("lml/stream/horse.ytd")));
    }

    [Fact]
    public void Default_and_active_profiles_cannot_be_deleted()
    {
        using var ws = Ready();
        ws.Store.CreateProfile("dev", "Dev");
        ws.SwitchProfile("dev");
        Assert.Throws<InvalidOperationException>(() => ws.Store.DeleteProfile("default"));
        Assert.Throws<InvalidOperationException>(() => ws.Store.DeleteProfile("dev"));
        ws.SwitchProfile("default");
        ws.Store.DeleteProfile("dev");
        Assert.Single(ws.Store.GetProfiles());
    }

    // ---------- adopt ----------

    [Fact]
    public void Adopting_held_back_mods_turns_red_into_tracked_and_online_green()
    {
        using var ws = Ready();
        // Mods you dropped in by hand before Saddlebag.
        Sandbox.Write(_sb.Game, "ScriptHookRDR2.dll", "hook");
        Sandbox.Write(_sb.Game, "dinput8.dll", "loader");
        Sandbox.Write(_sb.Game, "Trainer.asi", "asi");
        Sandbox.Write(_sb.Game, "TrainerData/config.ini", "x=1");
        Assert.Equal(VerifyStatus.Red, ws.Baseline.Verify().Status);

        var r = ws.Adopt(["ScriptHookRDR2.dll", "dinput8.dll", "Trainer.asi", "TrainerData"], "Old manual install");
        Assert.Empty(r.ReplacedVanilla);
        Assert.True(r.Import.Plan.IsComplete);
        Assert.False(File.Exists(_sb.GameFile("Trainer.asi")), "adopted files move into the library");

        ws.GoStory();
        Assert.Equal("x=1", File.ReadAllText(_sb.GameFile("TrainerData/config.ini")));
        Assert.All(ws.Baseline.Verify().Findings, f => Assert.Equal(FindingKind.Deployed, f.Kind));

        Assert.Equal(VerifyStatus.Green, ws.GoOnline().Report.Status);
        ws.GoStory();
        Assert.True(File.Exists(_sb.GameFile("Trainer.asi")));
    }

    [Fact]
    public void Adopt_refuses_unchanged_vanilla_and_tracked_files_but_reports_modded_vanilla()
    {
        using var ws = Ready();
        Assert.Throws<InvalidOperationException>(() => ws.Adopt(["anim_0.rpf"], "nope"));

        File.WriteAllText(_sb.GameFile("x64/data/vanilla.meta"), "edited by an old mod");
        var r = ws.Adopt(["x64/data/vanilla.meta"], "meta edit");
        Assert.Equal(["x64/data/vanilla.meta"], r.ReplacedVanilla);

        _sb.EnableFrameworks(ws);
        ws.Deploy();
        Assert.Throws<InvalidOperationException>(() => ws.Adopt(["dinput8.dll"], "already tracked"));
    }

    [Fact]
    public void Adopt_is_blocked_while_the_game_runs()
    {
        using var ws = Ready();
        Sandbox.Write(_sb.Game, "Trainer.asi", "asi");
        _sb.Guard.Running.Add("RDR2");
        Assert.Throws<Saddlebag.Core.Safety.GameRunningException>(() => ws.Adopt(["Trainer.asi"], "t"));
        Assert.True(File.Exists(_sb.GameFile("Trainer.asi")));
    }
}
