using Saddlebag.App.ViewModels;
using Saddlebag.Core.Baseline;
using Saddlebag.Core.Bisect;
using Saddlebag.Core.Deploy;
using Saddlebag.Core.Nexus;

namespace Saddlebag.Tests;

/// <summary>Scripted answers for every dialog, and a record of the errors shown.</summary>
public sealed class FakeDialogs : IDialogs
{
    public Queue<bool> Confirms { get; } = new();
    public Queue<string?> Prompts { get; } = new();
    public Queue<string?> Folders { get; } = new();
    public Queue<string[]?> Files { get; } = new();
    public List<(string Title, string Message)> Errors { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirms.Count > 0 ? Confirms.Dequeue() : true);
    public Task<string?> PromptAsync(string title, string message, string? initial = null, bool secret = false) =>
        Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() : initial);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult(Folders.Count > 0 ? Folders.Dequeue() : null);
    public Task<string[]?> PickFilesAsync(string title) => Task.FromResult(Files.Count > 0 ? Files.Dequeue() : null);
    public void ShowError(string title, string message) => Errors.Add((title, message));
}

/// <summary>The GUI's behaviour, driven through its view models exactly as the buttons would.</summary>
[Collection("NexusKeyEnv")]
public class GuiTests : IDisposable
{
    private readonly Sandbox _sb = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ShellViewModel _shell;

    public GuiTests() => _shell = new ShellViewModel(_dialogs, _sb.Guard) { RememberHome = _ => { }, DocumentsDir = _sb.Documents };

    public void Dispose()
    {
        _shell.Dispose();
        _sb.Dispose();
    }

    private void NoErrors() => Assert.True(_dialogs.Errors.Count == 0, string.Join("; ", _dialogs.Errors.Select(e => e.Message)));

    private async Task SetUpAsync()
    {
        await _shell.StartAsync(_sb.Home);
        Assert.True(_shell.NeedsSetup);
        _dialogs.Folders.Enqueue(_sb.Game);
        await _shell.Setup.PickGameCommand.ExecuteAsync(null);
        _shell.Setup.HomeDir = _sb.Home;
        await _shell.Setup.InitCommand.ExecuteAsync(null);
        Assert.False(_shell.NeedsSetup);
        Assert.NotNull(_shell.BaselineBanner);
        await _shell.Settings.CaptureBaselineCommand.ExecuteAsync(null);
        Assert.Null(_shell.BaselineBanner);
        NoErrors();
    }

    private string Hooks() => _sb.Zip("hooks", ("ScriptHookRDR2.dll", "h"), ("dinput8.dll", "l"), ("ScriptHookRDRDotNet.asi", "d"),
        ("ScriptHookRDRNetAPI.dll", "a"), ("vfs.asi", "v"), ("lml/mods.xml", "<x/>"));

    [Fact]
    public async Task Setup_import_deploy_online_story_round_trip()
    {
        await SetUpAsync();
        Assert.Equal(1, _shell.Settings.Progress);

        _dialogs.Files.Enqueue([Hooks(), _sb.Zip("trainer", ("Trainer.asi", "t"), ("Trainer.ini", "i"))]);
        await _shell.Mods.ImportFilesCommand.ExecuteAsync(null);
        NoErrors();
        Assert.Equal(2, _shell.Mods.Items.Count);
        Assert.All(_shell.Mods.Items, m => Assert.True(m.Enabled));
        Assert.Equal(8, _shell.PendingChanges);
        Assert.True(_shell.Plan.CanApply);

        await _shell.Plan.DeployCommand.ExecuteAsync(null);
        Assert.Equal(0, _shell.PendingChanges);
        Assert.Equal("story", _shell.ModeText);
        Assert.True(File.Exists(_sb.GameFile("Trainer.asi")));

        await _shell.Mode.ToggleModeCommand.ExecuteAsync(null);
        Assert.True(_shell.Mode.IsOnline);
        Assert.Equal(VerifyStatus.Green, _shell.Mode.Status);
        Assert.StartsWith("GREEN", _shell.Mode.StatusText);
        Assert.False(File.Exists(_sb.GameFile("Trainer.asi")));

        await _shell.Mode.ToggleModeCommand.ExecuteAsync(null);
        Assert.False(_shell.Mode.IsOnline);
        Assert.True(File.Exists(_sb.GameFile("Trainer.asi")));
        NoErrors();
    }

    [Fact]
    public async Task Starting_again_opens_the_existing_folder()
    {
        await SetUpAsync();
        using var second = new ShellViewModel(new FakeDialogs(), _sb.Guard) { RememberHome = _ => { } };
        _shell.Host.Dispose(); // one process at a time on state.db in this test
        await second.StartAsync(_sb.Home);
        Assert.False(second.NeedsSetup);
        Assert.Null(second.BaselineBanner);
    }

    [Fact]
    public async Task Routing_questions_are_answered_in_the_staging_table()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([Hooks()]);
        await _shell.Mods.ImportAsync([_sb.Zip("weather", ("weather.ymt", "w"))]);
        var weather = _shell.Mods.Items.Single(m => m.Name == "weather");
        Assert.False(weather.Enabled);
        Assert.Equal(1, weather.OpenQuestions);

        await _shell.Mods.Staging.LoadAsync(weather.Id);
        var row = Assert.Single(_shell.Mods.Staging.Rows);
        Assert.True(row.NeedsDecision);
        row.EditDest = "lml/replace/x64/data/weather.ymt";
        await _shell.Mods.Staging.ApplyDestCommand.ExecuteAsync(row);
        Assert.True(_shell.Mods.Staging.Rows.Single().IsOverride);

        await _shell.Mods.Staging.EnableCommand.ExecuteAsync(null);
        await _shell.Plan.DeployCommand.ExecuteAsync(null);
        NoErrors();
        Assert.True(File.Exists(_sb.GameFile("lml/replace/x64/data/weather.ymt")));
    }

    [Fact]
    public async Task Variants_are_chosen_from_the_staging_panel()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([Hooks()]);
        await _shell.Mods.ImportAsync([_sb.Zip("opts", ("Mod/Main/install.xml", "<x/>"), ("Mod/Main/a.ytd", "a"), ("Mod/Dark/install.xml", "<x/>"), ("Mod/Dark/a.ytd", "b"))]);
        var mod = _shell.Mods.Items.Single(m => m.Name == "opts");
        await _shell.Mods.Staging.LoadAsync(mod.Id);
        Assert.True(_shell.Mods.Staging.NeedsVariant);

        _shell.Mods.Staging.SelectedVariant = "Mod/Dark";
        await _shell.Mods.Staging.ChooseVariantCommand.ExecuteAsync(null);
        Assert.False(_shell.Mods.Staging.NeedsVariant);
        Assert.True(_shell.Mods.Items.Single(m => m.Id == mod.Id).Enabled);
        NoErrors();
    }

    [Fact]
    public async Task Priority_buttons_change_who_wins_and_the_winner_is_listed_first()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([Hooks(), _sb.Zip("a", ("horse.ytd", "A")), _sb.Zip("b", ("horse.ytd", "B"))]);
        Assert.Equal("b", _shell.Mods.Items[0].Name); // imported last → highest priority → top
        Assert.Contains(_shell.Plan.Checks, c => c.Id == DeployChecks.SameDest && c.Message.Contains("b wins"));

        await _shell.Mods.RaisePriorityCommand.ExecuteAsync(_shell.Mods.Items.Single(m => m.Name == "a"));
        Assert.Equal("a", _shell.Mods.Items[0].Name);
        Assert.Contains(_shell.Plan.Checks, c => c.Id == DeployChecks.SameDest && c.Message.Contains("a wins"));
    }

    [Fact]
    public async Task Blocked_plans_explain_and_ignore_deps_unblocks()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([_sb.Zip("standalone", ("Standalone.asi", "s"))]);
        Assert.False(_shell.Plan.CanApply);
        Assert.Contains(_shell.Plan.Checks, c => c.Severity == Severity.Block);
        Assert.StartsWith("Blocked", _shell.Plan.Summary);

        _shell.Plan.IgnoreDeps = true;
        await _shell.Plan.RefreshAsync();
        Assert.True(_shell.Plan.CanApply);
        await _shell.Plan.DeployCommand.ExecuteAsync(null);
        Assert.True(File.Exists(_sb.GameFile("Standalone.asi")));
    }

    [Fact]
    public async Task Errors_show_a_dialog_instead_of_crashing()
    {
        await SetUpAsync();
        _sb.Guard.Running.Add("RDR2");
        await _shell.Mode.GoOnlineCommand.ExecuteAsync(null);
        var (title, message) = Assert.Single(_dialogs.Errors);
        Assert.Equal("Close the game first", title);
        Assert.Contains("RDR2", message);
        Assert.Equal(message, _shell.LastError);
    }

    [Fact]
    public async Task Strays_found_by_verify_can_be_adopted_or_allowed()
    {
        await SetUpAsync();
        Sandbox.Write(_sb.Game, "dinput8.dll", "by hand");
        Sandbox.Write(_sb.Game, "x64/data/startup.meta", "lobby");
        await _shell.Mode.VerifyCommand.ExecuteAsync(null);
        Assert.Equal(VerifyStatus.Red, _shell.Mode.Status);

        _shell.Mode.Findings.Single(f => f.Path == "dinput8.dll").IsSelected = true;
        _dialogs.Prompts.Enqueue("Manual loader");
        await _shell.Mode.AdoptSelectedCommand.ExecuteAsync(null);
        Assert.Contains(_shell.Mods.Items, m => m.Name == "Manual loader" && m.Enabled);

        await _shell.Mode.AllowCommand.ExecuteAsync(_shell.Mode.Findings.Single(f => f.Path == "x64/data/startup.meta"));
        Assert.Equal(VerifyStatus.Amber, _shell.Mode.Status);
        Assert.Single(_shell.Settings.Rules);
        NoErrors();
    }

    [Fact]
    public async Task Profiles_create_and_switch_moving_only_the_diff()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([Hooks(), _sb.Zip("coat", ("coat.ytd", "c"))]);
        await _shell.Plan.DeployCommand.ExecuteAsync(null);

        _dialogs.Prompts.Enqueue("Story-Dev");
        await _shell.Profiles.CreateCommand.ExecuteAsync(null);
        await _shell.Profiles.SwitchCommand.ExecuteAsync(_shell.Profiles.Items.Single(p => p.Name == "Story-Dev"));
        Assert.Equal("Story-Dev", _shell.ProfileName);
        await _shell.Mods.ToggleEnabledCommand.ExecuteAsync(_shell.Mods.Items.Single(m => m.Name == "coat"));
        await _shell.Plan.DeployCommand.ExecuteAsync(null);
        Assert.False(File.Exists(_sb.GameFile("lml/stream/coat.ytd")));

        // The header picker switches too.
        _shell.Profiles.Picked = _shell.Profiles.Items.Single(p => p.Name == "Default");
        Assert.True(SpinWait.SpinUntil(() => _shell.ProfileName == "Default" && !_shell.Host.IsBusy, TimeSpan.FromSeconds(10)));
        Assert.Contains("placed 1, removed 0", _shell.StatusText);
        Assert.Equal("Default", _shell.Profiles.Picked!.Name);
        Assert.True(File.Exists(_sb.GameFile("lml/stream/coat.ytd")));
        NoErrors();
    }

    [Fact]
    public async Task Bisect_runs_from_the_page_and_reset_restores()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([Hooks()]);
        for (var i = 1; i <= 6; i++) await _shell.Mods.ImportAsync([_sb.Zip($"m{i}", ($"t{i}.ytd", $"{i}"))]);
        await _shell.Plan.DeployCommand.ExecuteAsync(null);
        var culprit = _shell.Mods.Items.Single(m => m.Name == "m4").Id;

        Assert.Contains(_shell.Bisect.Choices, c => c.IsHook && c.Keep);
        await _shell.Bisect.StartCommand.ExecuteAsync(null);
        Assert.True(_shell.Bisect.IsActive);
        Assert.NotNull(_shell.BisectBanner);
        while (!_shell.Bisect.IsDone)
        {
            var crashed = File.Exists(_sb.GameFile("lml/stream/t4.ytd"));
            await (crashed ? _shell.Bisect.CrashedCommand : _shell.Bisect.RanFineCommand).ExecuteAsync(null);
        }
        Assert.Equal("m4", _shell.Bisect.Culprit);

        await _shell.Bisect.ResetDisableCulpritCommand.ExecuteAsync(null);
        Assert.False(_shell.Bisect.IsActive);
        Assert.False(_shell.Mods.Items.Single(m => m.Id == culprit).Enabled);
        Assert.True(File.Exists(_sb.GameFile("lml/stream/t3.ytd")));
        NoErrors();
    }

    [Fact]
    public async Task Logs_page_lists_filters_and_follows()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([Hooks(), _sb.Zip("trainer", ("Trainer.asi", "t"))]);
        await _shell.Plan.DeployCommand.ExecuteAsync(null);
        Sandbox.Write(_sb.Game, "ScriptHookRDR2.log", "start\nFailed to load Trainer.asi\nok\n");
        await _shell.Logs.RefreshAsync();

        var row = Assert.Single(_shell.Logs.Logs);
        Assert.Equal((1, "trainer (1)"), (row.Errors, row.Mentions));
        _shell.Logs.Selected = row;
        await _shell.Logs.LinesLoading;
        _shell.Logs.ErrorsOnly = true;
        await _shell.Logs.LinesLoading;
        Assert.Equal([2], _shell.Logs.Lines.Select(l => l.Number));

        _shell.Logs.ErrorsOnly = false;
        await _shell.Logs.LinesLoading;
        await _shell.Logs.ToggleFollowCommand.ExecuteAsync(null);
        File.AppendAllText(_sb.GameFile("ScriptHookRDR2.log"), "Unhandled exception in Trainer.asi\n");
        _shell.Logs.Poll();
        Assert.Equal("Unhandled exception in Trainer.asi", _shell.Logs.Lines[^1].Text);
        Assert.Equal("trainer", _shell.Logs.Lines[^1].Mentions);
        NoErrors();
    }

    [Fact]
    public async Task Updates_page_checks_nexus_with_the_key()
    {
        var fake = new FakeNexus();
        fake.Mod(1234, "1.1");
        fake.Files(1234, $"[{FakeNexus.File(10, 1, "1.0", "Trainer-1234-1-0-1700000000.zip", 1700000000)}]");
        using var shell = new ShellViewModel(_dialogs, _sb.Guard) { RememberHome = _ => { }, NexusHandler = fake, DocumentsDir = _sb.Documents };
        await shell.StartAsync(_sb.Home);
        _dialogs.Folders.Enqueue(_sb.Game);
        await shell.Setup.PickGameCommand.ExecuteAsync(null);
        shell.Setup.HomeDir = _sb.Home;
        await shell.Setup.InitCommand.ExecuteAsync(null);
        await shell.Mods.ImportAsync([_sb.Zip("Trainer-1234-1-0-1700000000", ("Trainer.asi", "t"))]);

        await shell.Updates.CheckCommand.ExecuteAsync(null);
        Assert.Contains("API key", _dialogs.Errors.Single().Message);

        var old = Environment.GetEnvironmentVariable(NexusKeyStore.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(NexusKeyStore.EnvVar, "k");
            await shell.Updates.RefreshAsync();
            Assert.StartsWith("API key set", shell.Updates.KeyStatus);
            await shell.Updates.CheckCommand.ExecuteAsync(null);
            var r = Assert.Single(shell.Updates.Results);
            Assert.Equal(UpdateStatus.UpToDate, r.Status); // exact file matched, no successor
            Assert.Contains("request(s) to Nexus", shell.Updates.LastRun);

            fake.Routes["/v1/users/validate"] = (System.Net.HttpStatusCode.OK, """{"name":"rider","is_premium":false}""");
            await shell.Updates.TestKeyCommand.ExecuteAsync(null);
            Assert.StartsWith("Key works: signed in as rider", shell.Updates.KeyStatus);

            var mod = shell.Mods.Items.Single();
            await shell.Mods.Staging.LoadAsync(mod.Id);
            await shell.Mods.Staging.UnlinkNexusCommand.ExecuteAsync(null);
            Assert.Equal("Not linked to Nexus", shell.Mods.Staging.NexusText);
        }
        finally { Environment.SetEnvironmentVariable(NexusKeyStore.EnvVar, old); }
    }

    [Fact]
    public async Task Watch_page_redeploys_a_linked_build()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([Hooks()]);
        var build = _sb.Folder("bin/Release", ("Horse.asi", "v1"));
        _dialogs.Folders.Enqueue(build);
        _dialogs.Prompts.Enqueue("Horse Personalities");
        await _shell.Mods.LinkFolderCommand.ExecuteAsync(null);
        _shell.Watch.QuietMs = 0;

        await _shell.Watch.ToggleCommand.ExecuteAsync(null);
        Assert.True(_shell.Watch.IsWatching);
        Assert.Equal("v1", File.ReadAllText(_sb.GameFile("Horse.asi"))); // initial sync

        Sandbox.Write(build, "Horse.asi", "v2");
        Assert.True(SpinWait.SpinUntil(() =>
        {
            _shell.Watch.TickAsync().GetAwaiter().GetResult();
            return File.ReadAllText(_sb.GameFile("Horse.asi")) == "v2";
        }, TimeSpan.FromSeconds(10)));
        Assert.Contains(_shell.Watch.Events, e => e.Contains("Deployed"));

        await _shell.Watch.ToggleCommand.ExecuteAsync(null);
        Assert.False(_shell.Watch.IsWatching);
        NoErrors();
    }

    [Fact]
    public async Task Lml_packaging_from_staging_uses_typed_game_paths()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([Hooks(), _sb.Zip("bars", ("cameras.ymt", "c"))]);
        var bars = _shell.Mods.Items.Single(m => m.Name == "bars");
        await _shell.Mods.Staging.LoadAsync(bars.Id);
        _shell.Mods.Staging.Rows.Single().GamePath = "update:/x64/data/metadata/cameras.ymt";
        await _shell.Mods.Staging.PackForLmlCommand.ExecuteAsync(null);
        Assert.True(_shell.Mods.Staging.IsLmlPacked);
        Assert.Contains(_shell.Mods.Staging.Rows, r => r.IsGenerated && r.Dest == "lml/bars/install.xml");

        await _shell.Mods.Staging.EnableCommand.ExecuteAsync(null);
        await _shell.Plan.DeployCommand.ExecuteAsync(null);
        Assert.True(File.Exists(_sb.GameFile("lml/bars/install.xml")));
        NoErrors();
    }

    [Fact]
    public async Task Lml_folder_and_author_are_saved_and_used()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([Hooks(), _sb.Zip("coats", ("coat.ytd", "c"))]);
        var coats = _shell.Mods.Items.Single(m => m.Name == "coats");
        await _shell.Mods.Staging.LoadAsync(coats.Id);
        _shell.Mods.Staging.LmlFolder = "Better Coats";
        _shell.Mods.Staging.LmlAuthor = "Navod";
        await _shell.Mods.Staging.PackForLmlCommand.ExecuteAsync(null);
        NoErrors();

        Assert.Contains(_shell.Mods.Staging.Rows, r => r.Dest == "lml/Better Coats/stream/coat.ytd");
        await _shell.Plan.DeployCommand.ExecuteAsync(null);
        var info = Saddlebag.Core.Routing.InstallXml.Read(_sb.GameFile("lml/Better Coats/install.xml"))!;
        Assert.Equal("Navod", info.Author);

        // Reloading shows the saved values; clearing the folder falls back to the mod's name.
        await _shell.Mods.Staging.LoadAsync(coats.Id);
        Assert.Equal(("Better Coats", "Navod"), (_shell.Mods.Staging.LmlFolder, _shell.Mods.Staging.LmlAuthor));
        _shell.Mods.Staging.LmlFolder = "";
        await _shell.Mods.Staging.PackForLmlCommand.ExecuteAsync(null);
        Assert.Contains(_shell.Mods.Staging.Rows, r => r.Dest == "lml/coats/stream/coat.ytd");
    }

    [Fact]
    public async Task Nexus_link_fields_set_page_exact_file_and_version()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([_sb.Zip("trainer", ("Trainer.asi", "t"))]);
        var mod = _shell.Mods.Items.Single();
        await _shell.Mods.Staging.LoadAsync(mod.Id);
        Assert.Equal("Not linked to Nexus", _shell.Mods.Staging.NexusText);

        _shell.Mods.Staging.NexusTarget = "https://www.nexusmods.com/reddeadredemption2/mods/1234?tab=files";
        _shell.Mods.Staging.NexusFile = @"C:\Downloads\Trainer-1234-1-2-1700000000.zip";
        await _shell.Mods.Staging.SaveNexusLinkCommand.ExecuteAsync(null);
        NoErrors();
        Assert.Contains("exact file known", _shell.Mods.Staging.NexusText);
        Assert.Equal("1.2", _shell.Mods.Staging.InstalledVersion);                // read from the file name
        Assert.Equal("Trainer-1234-1-2-1700000000.zip", _shell.Mods.Staging.NexusFile); // folder stripped
        var link = await _shell.Host.Run(ws => ws.Library.LoadManifest(mod.Id).Nexus!);
        Assert.Equal((1234L, 1700000000L), (link.ModId, link.UploadedTimestamp!.Value));

        // An explicit version wins; clearing the file field means "no exact file" in the GUI.
        _shell.Mods.Staging.InstalledVersion = "1.2.1";
        _shell.Mods.Staging.NexusFile = "";
        await _shell.Mods.Staging.SaveNexusLinkCommand.ExecuteAsync(null);
        Assert.Contains("no exact file", _shell.Mods.Staging.NexusText);
        Assert.Equal("1.2.1", _shell.Mods.Staging.InstalledVersion);

        _shell.Mods.Staging.NexusTarget = "not a link";
        await _shell.Mods.Staging.SaveNexusLinkCommand.ExecuteAsync(null);
        Assert.Contains("isn't a Nexus mod URL or id", _dialogs.Errors.Single().Message);
    }

    [Fact]
    public async Task Purge_preview_shows_removals_and_restores_without_touching_files()
    {
        await SetUpAsync();
        await _shell.Mods.ImportAsync([Hooks(), _sb.Zip("meta", ("x64/data/vanilla.meta", "MODDED"), ("lml/stream/m.ytd", "m"))]);
        await _shell.Plan.RefreshAsync();
        Assert.False(_shell.Plan.CanPurge); // nothing deployed yet
        await _shell.Plan.DeployCommand.ExecuteAsync(null);
        Assert.True(_shell.Plan.CanPurge);

        _shell.Plan.PreviewPurge = true;
        await _shell.Plan.RefreshAsync();
        Assert.False(_shell.Plan.CanApply);              // Deploy is off while previewing
        Assert.Equal(8, _shell.Plan.Steps.Count(st => st.Action == "remove"));
        Assert.Equal("x64/data/vanilla.meta", _shell.Plan.Steps.Single(st => st.Action == "restore").Dest);
        Assert.Contains("removes 8 file(s) and restores 1 original(s)", _shell.Plan.Summary);
        Assert.Equal("MODDED", File.ReadAllText(_sb.GameFile("x64/data/vanilla.meta"))); // preview only

        await _shell.Plan.PurgeCommand.ExecuteAsync(null);
        Assert.False(_shell.Plan.PreviewPurge);
        Assert.Equal("vanilla meta", File.ReadAllText(_sb.GameFile("x64/data/vanilla.meta")));
        NoErrors();
    }

    [Fact]
    public async Task Changed_game_settings_show_as_information_with_a_restore_button()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_sb.SystemXml)!);
        File.WriteAllText(_sb.SystemXml, "flat");
        await SetUpAsync();
        File.WriteAllText(_sb.SystemXml, "vr");

        await _shell.Mode.VerifyCommand.ExecuteAsync(null);
        Assert.Equal(VerifyStatus.Green, _shell.Mode.Status);
        var row = Assert.Single(_shell.Mode.Findings);
        Assert.Equal(("info", true, false, false), (row.Severity, row.CanRestore, row.CanAdopt, row.CanAllow));

        await _shell.Mode.RestoreSettingsCommand.ExecuteAsync(row);
        NoErrors();
        Assert.Equal("flat", File.ReadAllText(_sb.SystemXml));
        Assert.Empty(_shell.Mode.Findings);
        Assert.Contains("saved", _shell.StatusText);
    }
}
