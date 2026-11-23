using System.Collections.Concurrent;
using Saddlebag.Core;
using Saddlebag.Core.Deploy;
using Saddlebag.Core.Watch;

namespace Saddlebag.Tests;

public class WatchStateTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Burst_of_changes_deploys_once_after_the_quiet_period()
    {
        var s = new WatchState(TimeSpan.FromSeconds(2));
        Assert.Equal(WatchAction.None, s.Tick(At(0), false));
        s.Change(At(0), "bin/a.dll");
        s.Change(At(1), "bin/a.pdb");
        s.Change(At(1.5), "bin/a.dll");
        Assert.Equal(WatchAction.None, s.Tick(At(3), false));   // 1.5s since the last write: still building
        Assert.Equal(WatchAction.Deploy, s.Tick(At(3.5), false));
        Assert.Equal(2, s.ChangedPaths.Count);
        s.Done();
        Assert.Equal(WatchAction.None, s.Tick(At(10), false));
        Assert.Empty(s.ChangedPaths);
    }

    [Fact]
    public void Game_open_waits_reports_once_then_deploys_when_closed()
    {
        var s = new WatchState(TimeSpan.FromSeconds(1));
        s.Change(At(0));
        Assert.Equal(WatchAction.WaitForGame, s.Tick(At(2), true));
        Assert.Equal(WatchAction.None, s.Tick(At(3), true));     // don't repeat the message
        Assert.Equal(WatchAction.None, s.Tick(At(60), true));
        Assert.Equal(WatchAction.Deploy, s.Tick(At(61), false));
    }

    [Fact]
    public void Retry_waits_another_quiet_period()
    {
        var s = new WatchState(TimeSpan.FromSeconds(1));
        s.Change(At(0));
        Assert.Equal(WatchAction.Deploy, s.Tick(At(1), false));
        s.Retry(At(1));
        Assert.Equal(WatchAction.None, s.Tick(At(1.5), false));
        Assert.Equal(WatchAction.Deploy, s.Tick(At(2), false));
    }
}

public class BuildWatcherTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private (Workspace Ws, string Build, string ModId) Setup()
    {
        var ws = _sb.Init();
        ws.CaptureBaseline(false);
        _sb.EnableFrameworks(ws);
        var build = _sb.Folder("HorsePersonalities/bin/Release");
        Sandbox.WriteBytes(build, "HorsePersonalities.dll", Dlls.Script);
        Sandbox.Write(build, "HorsePersonalities.ini", "v=1");
        var id = ws.Library.Link(build, "Horse Personalities").Mod.Id;
        ws.Store.SetEnabled(id, true);
        ws.GoStory();
        return (ws, build, id);
    }

    private BuildWatcher Watcher(Workspace ws, List<WatchEvent> events, TimeSpan? quiet = null) =>
        new(ws, [], quiet ?? TimeSpan.Zero, events.Add);

    [Fact]
    public void A_build_redeploys_only_the_changed_file()
    {
        var (ws, build, _) = Setup();
        using var owned = ws;
        var events = new List<WatchEvent>();
        var w = Watcher(ws, events);
        var dllDeployedAt = ws.Store.GetDeployed().Single(d => d.DestPath == "scripts/HorsePersonalities.dll").DeployedAt;

        Sandbox.Write(build, "HorsePersonalities.ini", "v=2");
        w.NotifyChange(Path.Combine(build, "HorsePersonalities.ini"));
        w.Step();

        var e = Assert.Single(events, x => x.Kind == WatchEventKind.Deployed);
        Assert.Equal((1, 1), (e.Result!.Removed, e.Result.Placed));
        Assert.Equal(["Release/HorsePersonalities.ini"], Assert.Single(events, x => x.Kind == WatchEventKind.Changed).Paths!);
        Assert.Equal("v=2", File.ReadAllText(_sb.GameFile("scripts/HorsePersonalities.ini")));
        Assert.Equal(dllDeployedAt, ws.Store.GetDeployed().Single(d => d.DestPath == "scripts/HorsePersonalities.dll").DeployedAt);
    }

    [Fact]
    public void Changed_paths_are_matched_regardless_of_separators_and_sibling_names()
    {
        var (ws, build, _) = Setup();
        using var owned = ws;
        var events = new List<WatchEvent>();
        var w = Watcher(ws, events);
        var mixed = build.Replace(Path.DirectorySeparatorChar, '/') + "/HorsePersonalities.ini"; // as some tools report it
        w.NotifyChange(mixed);
        w.NotifyChange(build + "2" + Path.DirectorySeparatorChar + "other.ini");             // a sibling folder, not ours
        Sandbox.Write(build, "HorsePersonalities.ini", "v=9");
        w.Step();
        Assert.Equal(["Release/HorsePersonalities.ini"], Assert.Single(events, x => x.Kind == WatchEventKind.Changed).Paths!);
    }

    [Fact]
    public void While_the_game_runs_it_waits_then_deploys_after_it_closes()
    {
        var (ws, build, _) = Setup();
        using var owned = ws;
        var events = new List<WatchEvent>();
        var w = Watcher(ws, events);

        _sb.Guard.Running.Add("RDR2");
        Sandbox.Write(build, "HorsePersonalities.ini", "v=3");
        w.NotifyChange();
        w.Step();
        w.Step();
        Assert.Single(events, x => x.Kind == WatchEventKind.WaitingForGame);
        Assert.Equal("v=1", File.ReadAllText(_sb.GameFile("scripts/HorsePersonalities.ini")));

        _sb.Guard.Running.Clear();
        w.Step();
        Assert.Contains(events, x => x.Kind == WatchEventKind.Deployed);
        Assert.Equal("v=3", File.ReadAllText(_sb.GameFile("scripts/HorsePersonalities.ini")));
    }

    [Fact]
    public void Nothing_changed_reports_up_to_date()
    {
        var (ws, _, _) = Setup();
        using var owned = ws;
        var events = new List<WatchEvent>();
        var w = Watcher(ws, events);
        w.NotifyChange();
        w.Step();
        Assert.Single(events, x => x.Kind == WatchEventKind.UpToDate);
    }

    [Fact]
    public void A_build_that_breaks_routing_is_reported_and_waits_for_the_next_build()
    {
        var (ws, build, _) = Setup();
        using var owned = ws;
        var events = new List<WatchEvent>();
        var w = Watcher(ws, events);
        Sandbox.Write(build, "weather.ymt", "x"); // loose data file: rule 8 asks where it goes
        w.NotifyChange();
        w.Step();
        Assert.Single(events, x => x.Kind == WatchEventKind.Blocked);
        w.Step();
        Assert.Single(events, x => x.Kind == WatchEventKind.Blocked); // not retried in a loop

        File.Delete(Path.Combine(build, "weather.ymt"));
        Sandbox.Write(build, "HorsePersonalities.ini", "v=4");
        w.NotifyChange();
        w.Step();
        Assert.Contains(events, x => x.Kind == WatchEventKind.Deployed);
    }

    [Fact]
    public void Refuses_online_mode_bisects_and_unlinked_mods()
    {
        var (ws, _, _) = Setup();
        using var owned = ws;
        var trainer = ws.Library.Import(_sb.Zip("trainer", ("Trainer.asi", "a"))).Mod.Id;
        ws.Store.SetEnabled(trainer, true);
        Assert.Throws<InvalidOperationException>(() => new BuildWatcher(ws, [trainer], TimeSpan.Zero, _ => { }));

        ws.GoOnline();
        Assert.Throws<InvalidOperationException>(() => new BuildWatcher(ws, [], TimeSpan.Zero, _ => { }));
    }

    [Fact]
    public void No_linked_mods_is_refused()
    {
        using var ws = _sb.Init();
        ws.CaptureBaseline(false);
        var ex = Assert.Throws<InvalidOperationException>(() => new BuildWatcher(ws, [], TimeSpan.Zero, _ => { }));
        Assert.Contains("saddlebag link", ex.Message);
    }

    [Fact]
    public void Real_file_system_events_trigger_a_deploy()
    {
        var (ws, build, _) = Setup();
        using var owned = ws;
        var events = new ConcurrentQueue<WatchEvent>();
        var w = new BuildWatcher(ws, [], TimeSpan.FromMilliseconds(300), events.Enqueue) { PollInterval = TimeSpan.FromMilliseconds(50) };
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(() => w.Run(cts.Token));

        Assert.True(SpinWait.SpinUntil(() => events.Any(e => e.Kind == WatchEventKind.UpToDate), TimeSpan.FromSeconds(10)), "initial sync");
        Sandbox.Write(build, "HorsePersonalities.ini", "v=5"); // what a build does
        Assert.True(SpinWait.SpinUntil(() => events.Any(e => e.Kind == WatchEventKind.Deployed), TimeSpan.FromSeconds(10)),
            "no deploy after a change: " + string.Join("; ", events.Select(e => e.Kind)));
        cts.Cancel();
        loop.Wait(TimeSpan.FromSeconds(10));

        Assert.Equal("v=5", File.ReadAllText(_sb.GameFile("scripts/HorsePersonalities.ini")));
        Assert.Contains(events, e => e.Kind == WatchEventKind.Stopped);
    }
}

public class SourceChangedMidDeployTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    [Fact]
    public void A_file_that_changes_mid_deploy_is_skipped_and_placed_next_time()
    {
        using var ws = _sb.Init();
        ws.CaptureBaseline(false);
        _sb.EnableFrameworks(ws);
        var build = _sb.Folder("bin", ("a.asi", "a1"), ("b.asi", "b1"));
        ws.Store.SetEnabled(ws.Library.Link(build, "Dev").Mod.Id, true);

        var plan = ws.Deployer.Plan();
        ws.Deployer.AfterFileOp = s => { if (s.Dest == "a.asi") Sandbox.Write(build, "b.asi", "b2-still-building"); };
        var r = ws.Deployer.Apply(plan);
        ws.Deployer.AfterFileOp = null;

        Assert.Equal(1, r.Skipped);
        Assert.Contains(r.Notes, n => n.StartsWith("b.asi"));
        Assert.False(File.Exists(_sb.GameFile("b.asi")));
        Assert.Empty(ws.Store.GetPendingOps());             // the journal finished; nothing stuck

        var again = ws.Deploy();
        Assert.Equal((1, 0), (again.Placed, again.Skipped));
        Assert.Equal("b2-still-building", File.ReadAllText(_sb.GameFile("b.asi")));
    }
}
