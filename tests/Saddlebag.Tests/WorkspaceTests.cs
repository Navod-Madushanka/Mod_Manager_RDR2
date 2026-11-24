using System.IO.Compression;
using Saddlebag.Core;
using Saddlebag.Core.Baseline;
using Saddlebag.Core.Deploy;
using Saddlebag.Core.Safety;
using Saddlebag.Core.State;

namespace Saddlebag.Tests;

public class WorkspaceTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private Workspace Ready()
    {
        var ws = _sb.Init();
        ws.CaptureBaseline(force: false);
        return ws;
    }

    /// <summary>Five fixture mods covering the main package types; one overwrites a vanilla file.</summary>
    private void InstallFive(Workspace ws)
    {
        _sb.EnableFrameworks(ws);
        var mods = new List<string>
        {
            _sb.Zip("lml", ("vfs.asi", "vfs"), ("lml/mods.xml", "<mods/>")),
            _sb.Zip("saddles", ("Saddles/install.xml", "<xml/>"), ("Saddles/stream/saddle.ytd", "saddle")),
            _sb.Zip("trainer", ("Trainer.asi", "asi"), ("Trainer.ini", "ini"), ("ReadMe.txt", "doc")),
            _sb.Zip("coats", ("coat_01.ytd", "coat")),
            _sb.Zip("meta-tweak", ("x64/data/vanilla.meta", "MODDED meta"), ("lml/stream/x.ytd", "x")),
        };
        foreach (var m in mods)
        {
            var r = ws.Library.Import(m);
            Assert.True(r.Plan.IsComplete, $"{m} should route without questions");
            ws.Store.SetEnabled(r.Mod.Id, true);
        }
    }

    private static void AssertGreen(VerifyReport r) =>
        Assert.True(r.Status == VerifyStatus.Green, "Expected green, got: " + string.Join("; ", r.Findings.Select(f => $"{f.Kind} {f.Path}")));

    [Fact]
    public void Install_five_go_online_green_then_story_restores()
    {
        using var ws = Ready();
        InstallFive(ws);

        var deploy = ws.GoStory();
        Assert.Equal(13, deploy.Placed);
        Assert.Equal(1, deploy.BackedUp);
        Assert.Equal("MODDED meta", File.ReadAllText(_sb.GameFile("x64/data/vanilla.meta")));
        Assert.True(File.Exists(_sb.GameFile("lml/Saddles/stream/saddle.ytd")));
        Assert.True(File.Exists(_sb.GameFile("lml/stream/coat_01.ytd")));
        Assert.False(File.Exists(_sb.GameFile("ReadMe.txt")));
        Assert.All(ws.Store.GetDeployed(), d => Assert.Equal("hardlink", d.Method));
        Assert.Equal(VerifyStatus.Red, ws.Baseline.Verify().Status);

        var online = ws.GoOnline();
        AssertGreen(online.Report);
        Assert.Equal("vanilla meta", File.ReadAllText(_sb.GameFile("x64/data/vanilla.meta")));
        Assert.False(Directory.Exists(_sb.GameFile("lml")), "empty mod folders are pruned");
        Assert.Empty(ws.Store.GetDeployed());
        Assert.Empty(ws.Store.GetBackups());
        AssertGreen(ws.Baseline.Verify(deep: true));

        var story = ws.GoStory();
        Assert.Equal(13, story.Placed);
        Assert.True(ws.Deployer.Plan().IsEmpty, "a second deploy has nothing to do");
    }

    [Fact]
    public void Process_killed_at_any_step_recovers_on_next_run()
    {
        // Crash after each step in turn, until the crash point passes the last step.
        var total = 0;
        for (var crashAt = 0; ; crashAt++)
        {
            using var sb = new Sandbox();
            using (var ws = sb.Init())
            {
                ws.CaptureBaseline(false);
                sb.EnableFrameworks(ws, lml: true);
                var fixtures = new[]
                {
                    sb.Zip("trainer", ("Trainer.asi", "asi"), ("Trainer.ini", "ini")),
                    sb.Zip("meta", ("x64/data/vanilla.meta", "MODDED"), ("bink2w64.dll", "MODDED bink"), ("lml/stream/y.ytd", "y")),
                };
                foreach (var f in fixtures) ws.Store.SetEnabled(ws.Library.Import(f).Mod.Id, true);
                var plan = ws.Deployer.Plan();
                total = plan.Steps.Count;
                if (crashAt >= total) break;

                var n = 0;
                ws.Deployer.AfterFileOp = _ => { if (n++ == crashAt) throw new IOException("simulated power cut"); };
                Assert.Throws<IOException>(() => ws.Deploy());
                Assert.Single(ws.Store.GetPendingOps());
            }

            using (var ws = Workspace.Open(sb.Home, sb.Guard)) // "next run"
            {
                ws.Prepare();
                Assert.Empty(ws.Store.GetPendingOps());
                Assert.True(ws.Deployer.Plan().IsEmpty, $"crash at step {crashAt}: deploy should be complete after recovery");
                Assert.Equal("MODDED", File.ReadAllText(sb.GameFile("x64/data/vanilla.meta")));
                AssertGreen(ws.GoOnline().Report);
                Assert.Equal("vanilla meta", File.ReadAllText(sb.GameFile("x64/data/vanilla.meta")));
            }
        }
        Assert.True(total >= 6);
    }

    [Fact]
    public void Crash_during_purge_recovers_and_still_goes_green()
    {
        using var ws = Ready();
        InstallFive(ws);
        ws.GoStory();
        var n = 0;
        ws.Deployer.AfterFileOp = _ => { if (n++ == 4) throw new IOException("killed"); };
        Assert.Throws<IOException>(() => ws.GoOnline());
        ws.Deployer.AfterFileOp = null;

        AssertGreen(ws.GoOnline().Report);
    }

    [Fact]
    public void Running_game_blocks_every_file_operation()
    {
        using var ws = Ready();
        InstallFive(ws);
        _sb.Guard.Running.Add("RDR2");
        Assert.Throws<GameRunningException>(() => ws.Deploy());
        Assert.Throws<GameRunningException>(() => ws.GoOnline());
        Assert.Throws<GameRunningException>(() => ws.CaptureBaseline(false));
        Assert.Empty(ws.Store.GetDeployed());
        Assert.False(File.Exists(_sb.GameFile("Trainer.asi")));
    }

    [Fact]
    public void Edited_hardlinked_file_is_kept_in_library_on_purge()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws);
        var mod = ws.Library.Import(_sb.Zip("trainer", ("Trainer.asi", "asi"), ("Trainer.ini", "speed=1"))).Mod;
        ws.Store.SetEnabled(mod.Id, true);
        ws.Deploy();

        File.WriteAllText(_sb.GameFile("Trainer.ini"), "speed=9"); // your in-game tweak
        File.SetLastWriteTimeUtc(_sb.GameFile("Trainer.ini"), DateTime.UtcNow.AddMinutes(5));
        var purge = ws.Purge();

        Assert.Equal(1, purge.Rescued);
        var lib = Path.Combine(ws.Library.FilesRoot(mod), "Trainer.ini");
        Assert.Equal("speed=9", File.ReadAllText(lib));
        ws.Deploy();
        Assert.Equal("speed=9", File.ReadAllText(_sb.GameFile("Trainer.ini")));
    }

    [Fact]
    public void Linked_dev_folder_deploys_copies_and_redeploys_only_the_diff()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws);
        var build = _sb.Folder("bin/Release");
        Sandbox.WriteBytes(build, "HorsePersonalities.dll", Dlls.Script);
        Sandbox.Write(build, "HorsePersonalities.ini", "a=1");
        var mod = ws.Library.Link(build, "Horse Personalities").Mod;
        ws.Store.SetEnabled(mod.Id, true);

        ws.Deploy();
        Assert.All(ws.Store.GetDeployed().Where(d => d.ModId == mod.Id), d => Assert.Equal("copy", d.Method));
        Assert.True(File.Exists(_sb.GameFile("scripts/HorsePersonalities.dll")));

        Sandbox.Write(build, "HorsePersonalities.ini", "a=2"); // a fresh build
        var plan = ws.Deployer.Plan();
        Assert.Equal([StepType.Remove, StepType.Place], plan.Steps.Select(s => s.Type));
        Assert.All(plan.Steps, s => Assert.Equal("scripts/HorsePersonalities.ini", s.Dest));
        ws.Deploy();
        Assert.Equal("a=2", File.ReadAllText(_sb.GameFile("scripts/HorsePersonalities.ini")));
    }

    [Fact]
    public void Verify_flags_strays_and_respects_allow_and_expect_rules()
    {
        using var ws = Ready();
        Sandbox.Write(_sb.Game, "x64/data/startup.meta", "lobby password");
        Sandbox.Write(_sb.Game, "dinput8.dll", "leftover loader");
        File.WriteAllText(_sb.GameFile("bink2w64.dll"), "launcher swapped this");

        var r = ws.Baseline.Verify();
        Assert.Equal(VerifyStatus.Red, r.Status);
        Assert.Contains(r.Findings, f => f is { Kind: FindingKind.ModSignature, Path: "dinput8.dll" });
        Assert.Contains(r.Findings, f => f is { Kind: FindingKind.Extra, Path: "x64/data/startup.meta" });
        Assert.Contains(r.Findings, f => f is { Kind: FindingKind.Changed, Path: "bink2w64.dll" });

        File.Delete(_sb.GameFile("dinput8.dll"));
        ws.Store.SetPathRule("x64/data/startup.meta", StateStore.RuleAllow);
        ws.Store.SetPathRule("bink2w64.dll", StateStore.RuleExpectChange);
        Assert.Equal(VerifyStatus.Amber, ws.Baseline.Verify().Status);

        File.Delete(_sb.GameFile("anim_0.rpf"));
        Assert.Contains(ws.Baseline.Verify().Findings, f => f is { Kind: FindingKind.Missing, Path: "anim_0.rpf" });
    }

    [Fact]
    public void Same_size_tampering_is_caught_by_hash_when_mtime_moves_and_by_deep_otherwise()
    {
        using var ws = Ready();
        var p = _sb.GameFile("hd_0.rpf");
        var mtime = File.GetLastWriteTimeUtc(p);
        File.WriteAllText(p, "HD ARCHIVE"); // same length, different bytes
        Assert.Equal(VerifyStatus.Red, ws.Baseline.Verify().Status);

        File.SetLastWriteTimeUtc(p, mtime); // hide the mtime change
        Assert.Equal(VerifyStatus.Green, ws.Baseline.Verify().Status);
        Assert.Equal(VerifyStatus.Red, ws.Baseline.Verify(deep: true).Status);
    }

    [Fact]
    public void Baseline_refuses_a_modded_folder_unless_forced()
    {
        using var ws = _sb.Init();
        Sandbox.Write(_sb.Game, "ScriptHookRDR2.dll", "x");
        var ex = Assert.Throws<InvalidOperationException>(() => ws.CaptureBaseline(false));
        Assert.Contains("ScriptHookRDR2.dll", ex.Message);
    }

    [Fact]
    public void Online_without_baseline_refuses()
    {
        using var ws = _sb.Init();
        Assert.Throws<InvalidOperationException>(() => ws.GoOnline());
    }

    [Fact]
    public void Conflicting_mods_report_winner_by_priority()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws, lml: true);
        var a = ws.Library.Import(_sb.Zip("horse-a", ("horse.ytd", "A"))).Mod;
        var b = ws.Library.Import(_sb.Zip("horse-b", ("sub/horse.ytd", "B"))).Mod;
        ws.Store.SetEnabled(a.Id, true);
        ws.Store.SetEnabled(b.Id, true);

        var plan = ws.Deployer.Plan();
        var c = Assert.Single(plan.Checks, f => f.Id == DeployChecks.SameDest);
        Assert.StartsWith("lml/stream/horse.ytd: horse-b wins over horse-a", c.Message);
        ws.Deployer.Apply(plan);
        Assert.Equal("B", File.ReadAllText(_sb.GameFile("lml/stream/horse.ytd")));
    }

    [Fact]
    public void Unresolved_files_block_deploy_until_routed()
    {
        using var ws = Ready();
        _sb.EnableFrameworks(ws, lml: true);
        var mod = ws.Library.Import(_sb.Zip("weather", ("weather.ymt", "x"))).Mod;
        ws.Store.SetEnabled(mod.Id, true);
        Assert.Throws<DeployException>(() => ws.Deploy());

        ws.Library.SetOverride(mod.Id, "weather.ymt", "lml/replace/x64/data/weather.ymt");
        ws.Deploy();
        Assert.True(File.Exists(_sb.GameFile("lml/replace/x64/data/weather.ymt")));
        Assert.Equal("lml/replace/x64/data/weather.ymt", ws.Library.LoadManifest(mod.Id).Overrides.Files["weather.ymt"]);
    }

    [Fact]
    public void Reimporting_the_same_archive_is_a_no_op()
    {
        using var ws = Ready();
        var zip = _sb.Zip("trainer", ("Trainer.asi", "asi"));
        var first = ws.Library.Import(zip);
        var again = ws.Library.Import(zip);
        Assert.True(again.AlreadyImported);
        Assert.Equal(first.Mod.Id, again.Mod.Id);
    }

    [Fact]
    public void Zip_slip_entries_are_rejected()
    {
        using var ws = Ready();
        var zip = Path.Combine(_sb.Src, "evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("../../game/RDR2.exe").Open()))
            w.Write("pwned");

        Assert.ThrowsAny<Exception>(() => ws.Library.Import(zip));
        Assert.Equal("vanilla exe", File.ReadAllText(_sb.GameFile("RDR2.exe")));
    }

    [Fact]
    public void Home_inside_game_folder_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => Workspace.Init(Path.Combine(_sb.Game, "Saddlebag"), _sb.Game, _sb.Guard));
    }

    [Fact]
    public void Game_update_is_detected()
    {
        using var ws = Ready();
        Assert.False(ws.Baseline.GameUpdatedSinceBaseline());
        File.WriteAllText(_sb.GameFile("RDR2.exe"), "patched exe 1.32");
        Assert.True(ws.Baseline.GameUpdatedSinceBaseline());
    }
}
