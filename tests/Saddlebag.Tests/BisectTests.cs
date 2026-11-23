using Saddlebag.Core;
using Saddlebag.Core.Baseline;
using Saddlebag.Core.Bisect;
using Saddlebag.Core.Deploy;

namespace Saddlebag.Tests;

public class BisectSessionTests
{
    private static List<string> Mods(int n) => Enumerable.Range(1, n).Select(i => $"mod{i:00}").ToList();

    /// <summary>Plays the bisect against a fake game that crashes whenever <paramref name="crashes"/> says so.</summary>
    private static BisectSession Run(List<string> suspects, Func<HashSet<string>, bool> crashes, List<string>? kept = null)
    {
        var s = BisectSession.Start(suspects, kept ?? []);
        for (var guard = 0; !s.IsDone; guard++)
        {
            Assert.True(guard < 50, "bisect didn't terminate");
            s.Answer(crashes(s.Enabled.ToHashSet()));
        }
        return s;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 4)]
    [InlineData(32, 6)]
    [InlineData(33, 7)]
    public void Finds_any_single_culprit_within_log2_plus_confirm(int count, int maxLaunches)
    {
        var mods = Mods(count);
        foreach (var culprit in mods)
        {
            var s = Run(mods, on => on.Contains(culprit));
            Assert.Equal(BisectPhase.Found, s.Phase);
            Assert.Equal(culprit, s.Culprit);
            Assert.True(s.Launches <= maxLaunches, $"{culprit}: {s.Launches} launches");
        }
    }

    [Fact]
    public void Estimate_matches_the_plan_for_32_mods()
    {
        var s = BisectSession.Start(Mods(32), []);
        Assert.Equal(6, s.RemainingEstimate); // 5 halvings + 1 confirm
        Assert.Equal(16, s.Testing.Count);
    }

    [Fact]
    public void Kept_mods_are_on_in_every_launch()
    {
        var s = BisectSession.Start(Mods(8), ["scripthook", "lml"]);
        while (!s.IsDone)
        {
            Assert.Contains("scripthook", s.Enabled);
            Assert.Contains("lml", s.Enabled);
            s.Answer(s.Enabled.Contains("mod03"));
        }
        Assert.Equal("mod03", s.Culprit);
    }

    [Fact]
    public void A_crash_that_needs_two_mods_is_reported_as_an_interaction()
    {
        var s = Run(Mods(16), on => on.Contains("mod02") && on.Contains("mod07"));
        Assert.Equal(BisectPhase.Interaction, s.Phase);
        Assert.Null(s.Culprit); // binary search split the pair, so it must not blame a single innocent mod
        Assert.Contains("mod02", s.CrashSet);
        Assert.Contains("mod07", s.CrashSet);
        Assert.Equal(8, s.CrashSet.Count);
    }

    [Fact]
    public void No_crash_at_all_means_not_found()
    {
        var s = Run(Mods(10), _ => false);
        Assert.Equal(BisectPhase.NotFound, s.Phase);
        Assert.Null(s.Culprit);
    }

    [Fact]
    public void Undo_takes_back_a_wrong_answer()
    {
        var s = BisectSession.Start(Mods(8), []);
        var firstTest = s.Testing.ToList();
        s.Answer(crashed: false); // oops, it did crash
        s.Undo();
        Assert.Equal(firstTest, s.Testing);
        Assert.Equal(8, s.Candidates.Count);
        Assert.Empty(s.History);
        s.Answer(crashed: true);
        Assert.Equal(firstTest, s.Candidates);
    }

    [Fact]
    public void Answering_after_done_and_starting_empty_are_refused()
    {
        var s = Run(Mods(2), on => on.Contains("mod01"));
        Assert.Throws<InvalidOperationException>(() => s.Answer(true));
        Assert.Throws<InvalidOperationException>(() => BisectSession.Start([], ["scripthook"]));
    }
}

public class BisectWorkspaceTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private (Workspace Ws, List<string> Mods) Setup(int count)
    {
        var ws = _sb.Init();
        ws.CaptureBaseline(false);
        _sb.EnableFrameworks(ws, lml: true);
        var mods = new List<string>();
        for (var i = 1; i <= count; i++)
        {
            var id = ws.Library.Import(_sb.Zip($"mod{i:00}", ($"tex{i:00}.ytd", $"m{i}"))).Mod.Id;
            ws.Store.SetEnabled(id, true);
            mods.Add(id);
        }
        ws.GoStory();
        return (ws, mods);
    }

    private HashSet<string> DeployedMods(Workspace ws) => ws.Store.GetDeployed().Select(d => d.ModId).ToHashSet();

    [Fact]
    public void Bisect_deploys_each_test_set_finds_the_culprit_and_reset_restores_everything()
    {
        var (ws, mods) = Setup(8);
        using var owned = ws;
        var culprit = mods[5];
        var before = ws.Store.GetDeployed().Select(d => (d.DestPath, d.ModId, d.Sha256)).ToHashSet();
        var originalProfile = ws.Store.ActiveProfile;

        var (s, _) = ws.BisectStart();
        Assert.Equal(2, s.Kept.Count);                    // Script Hook and LML stay on
        Assert.Equal(mods, s.Suspects);
        while (!s.IsDone)
        {
            var on = DeployedMods(ws);
            Assert.Equal(s.Enabled.ToHashSet(), on);      // the game folder holds exactly the test set
            (s, _) = ws.BisectAnswer(crashed: on.Contains(culprit));
        }
        Assert.Equal(BisectPhase.Found, s.Phase);
        Assert.Equal(culprit, s.Culprit);
        Assert.Equal(originalProfile, s.OriginalProfile);
        Assert.Equal(mods.Count + 2, ws.Store.GetProfileMods(originalProfile).Count(p => p.Enabled)); // your profile is untouched (8 mods + 2 hooks)

        ws.BisectReset();
        Assert.Equal(before, ws.Store.GetDeployed().Select(d => (d.DestPath, d.ModId, d.Sha256)).ToHashSet());
        Assert.Equal(originalProfile, ws.Store.ActiveProfile);
        Assert.Single(ws.Store.GetProfiles());             // temporary profile removed
        Assert.Null(ws.LoadBisect());
    }

    [Fact]
    public void Reset_can_disable_the_culprit()
    {
        var (ws, mods) = Setup(4);
        using var owned = ws;
        var (s, _) = ws.BisectStart();
        while (!s.IsDone) (s, _) = ws.BisectAnswer(DeployedMods(ws).Contains(mods[0]));

        ws.BisectReset(disableCulprit: true);
        Assert.DoesNotContain(mods[0], DeployedMods(ws));
        Assert.All(mods.Skip(1), m => Assert.Contains(m, DeployedMods(ws)));
        Assert.False(ws.Store.GetProfileMods().Single(p => p.ModId == mods[0]).Enabled);
    }

    [Fact]
    public void State_survives_closing_saddlebag_between_launches()
    {
        var (ws, mods) = Setup(8);
        ws.BisectStart();
        ws.Dispose();

        for (var i = 0; i < 20; i++)
        {
            using var again = _sb.Open(); // each launch is a fresh process
            var s = again.LoadBisect()!;
            if (s.IsDone) { Assert.Equal(mods[2], s.Culprit); again.BisectReset(); return; }
            again.BisectAnswer(DeployedMods(again).Contains(mods[2]));
        }
        Assert.Fail("bisect didn't finish");
    }

    [Fact]
    public void Profile_changes_are_refused_while_bisecting()
    {
        var (ws, _) = Setup(4);
        using var owned = ws;
        ws.Store.CreateProfile("other", "Other");
        ws.BisectStart();
        Assert.Throws<InvalidOperationException>(() => ws.SwitchProfile("other"));
        Assert.Throws<InvalidOperationException>(() => ws.BisectStart());
    }

    [Fact]
    public void Keep_and_suspect_override_the_hook_rule()
    {
        var (ws, mods) = Setup(4);
        using var owned = ws;
        var (s, _) = ws.BisectStart(keep: [mods[0]]);
        Assert.Contains(mods[0], s.Kept);
        Assert.DoesNotContain(mods[0], s.Suspects);
        ws.BisectReset();

        var hook = ws.Store.GetMods().First(m => m.Name.StartsWith("scripthook")).Id;
        // Suspecting the hook itself is allowed. The first set includes it; once a set leaves it out,
        // LML can't deploy without Script Hook, so that answer is refused and nothing is recorded.
        var (s2, _) = ws.BisectStart(suspect: [hook]);
        Assert.Contains(hook, s2.Testing);
        var ex = Assert.Throws<DeployException>(() => ws.BisectAnswer(crashed: false));
        Assert.Contains("--keep", ex.Message);
        Assert.Empty(ws.LoadBisect()!.History);
        ws.BisectReset();
        Assert.Null(ws.LoadBisect());
    }

    [Fact]
    public void Bisect_started_from_online_mode_returns_to_a_clean_folder()
    {
        var (ws, _) = Setup(4);
        using var owned = ws;
        ws.GoOnline();
        var (s, _) = ws.BisectStart();
        Assert.NotEmpty(ws.Store.GetDeployed());
        ws.BisectReset();
        Assert.Empty(ws.Store.GetDeployed());
        Assert.Equal("online", ws.Store.GetSetting("mode"));
        Assert.Equal(VerifyStatus.Green, ws.Baseline.Verify().Status);
        _ = s;
    }
}
