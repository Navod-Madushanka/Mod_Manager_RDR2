using Saddlebag.App.ViewModels;
using Saddlebag.Core.Setup;

namespace Saddlebag.Tests;

/// <summary>A fake machine: Steam with two libraries, an Epic manifest folder, a Rockstar install folder.</summary>
public sealed class FakeProbe(string root) : IInstallProbe
{
    public string? Steam { get; set; }
    public string? Rockstar { get; set; }
    public string Epic { get; set; } = Path.Combine(root, "ProgramData", "Epic", "Manifests");
    public List<string> Drives { get; } = [];
    public string? SteamPath() => Steam;
    public string? RockstarInstallFolder() => Rockstar;
    public string EpicManifestsDir() => Epic;
    public IEnumerable<string> FixedDriveRoots() => Drives;
}

public class GameLocatorTests : IDisposable
{
    private readonly Sandbox _sb = new();
    public void Dispose() => _sb.Dispose();

    private string Game(string folder)
    {
        Sandbox.Write(folder, "RDR2.exe", "exe");
        return folder;
    }

    private static string Vdf(string s) => s.Replace(@"\", @"\\");

    [Fact]
    public void Finds_steam_installs_in_every_library_using_the_app_manifest()
    {
        var steam = Path.Combine(_sb.Root, "Steam");
        var lib2 = Path.Combine(_sb.Root, "SteamLibrary");
        Sandbox.Write(steam, "steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
                "0" { "path" "{{Vdf(steam)}}" "apps" { "228980" "1" } }
                "1" { "path" "{{Vdf(lib2)}}" "apps" { "1174180" "123" } }
            }
            """);
        Sandbox.Write(lib2, "steamapps/appmanifest_1174180.acf", "\"AppState\" { \"appid\" \"1174180\" \"installdir\" \"RDR2 Custom\" }");
        Game(Path.Combine(lib2, "steamapps", "common", "RDR2 Custom"));

        var g = Assert.Single(GameLocator.FindAll(new FakeProbe(_sb.Root) { Steam = steam }));
        Assert.Equal(GameStore.Steam, g.Store);
        Assert.EndsWith("RDR2 Custom", g.Folder);
        Assert.Equal("steam://validate/1174180", g.VerifyUri);
        Assert.Equal("steam://rungameid/1174180", g.LaunchUri);
    }

    [Fact]
    public void Finds_epic_installs_from_their_manifest_with_the_launch_link()
    {
        var probe = new FakeProbe(_sb.Root);
        var folder = Game(Path.Combine(_sb.Root, "Epic Games", "RedDeadRedemption2"));
        Sandbox.Write(probe.Epic, "ABC.item", $$"""{ "DisplayName": "Red Dead Redemption 2", "InstallLocation": {{System.Text.Json.JsonSerializer.Serialize(folder)}}, "AppName": "Heather" }""");
        Sandbox.Write(probe.Epic, "other.item", """{ "DisplayName": "Some Other Game", "InstallLocation": "/nowhere", "AppName": "X" }""");
        Sandbox.Write(probe.Epic, "broken.item", "{ not json");

        var g = Assert.Single(GameLocator.FindAll(probe));
        Assert.Equal((GameStore.Epic, "Heather"), (g.Store, g.EpicAppName));
        Assert.Equal("com.epicgames.launcher://apps/Heather?action=launch&silent=true", g.LaunchUri);
        Assert.Null(g.VerifyUri);
        Assert.Contains("Verify", g.VerifyHelp);
    }

    [Fact]
    public void Finds_the_rockstar_install_and_skips_folders_without_the_game()
    {
        var rockstar = Game(Path.Combine(_sb.Root, "Rockstar Games", "Red Dead Redemption 2"));
        Sandbox.Write(rockstar, "PlayRDR2.exe", "launcher");
        var probe = new FakeProbe(_sb.Root) { Rockstar = rockstar, Steam = Path.Combine(_sb.Root, "NoSteamHere") };
        var g = Assert.Single(GameLocator.FindAll(probe));
        Assert.Equal(GameStore.Rockstar, g.Store);
        Assert.Null(g.LaunchUri);
        Assert.EndsWith("PlayRDR2.exe", g.LaunchExe);
    }

    [Fact]
    public void The_same_folder_found_twice_is_listed_once()
    {
        var folder = Game(Path.Combine(_sb.Root, "Rockstar Games", "Red Dead Redemption 2"));
        var probe = new FakeProbe(_sb.Root) { Rockstar = folder + Path.DirectorySeparatorChar };
        probe.Drives.Add(_sb.Root);
        Assert.Single(GameLocator.FindAll(probe));
    }

    [Fact]
    public void A_picked_folder_is_classified_and_a_home_is_suggested_on_its_drive()
    {
        var steamGame = Game(Path.Combine(_sb.Root, "lib", "steamapps", "common", "Red Dead Redemption 2"));
        Assert.Equal(GameStore.Steam, GameLocator.Describe(steamGame, new FakeProbe(_sb.Root)).Store);
        Assert.Equal(GameStore.Unknown, GameLocator.Describe(_sb.Game, new FakeProbe(_sb.Root)).Store);
        Assert.Equal(Path.Combine(Path.GetPathRoot(_sb.Game)!, "Saddlebag"), GameLocator.SuggestHome(_sb.Game));
        Assert.True(GameLocator.CanWrite(_sb.Game));
    }
}

[Collection("NexusKeyEnv")]
public class WizardTests : IDisposable
{
    private readonly Sandbox _sb = new();
    private readonly FakeDialogs _dialogs = new();
    public void Dispose() => _sb.Dispose();

    private ShellViewModel Shell(IInstallProbe? probe = null, Func<string, bool>? canWrite = null) =>
        new(_dialogs, _sb.Guard)
        {
            RememberHome = _ => { }, DocumentsDir = _sb.Documents, InstallProbe = probe ?? new NoInstalls(),
            CanWriteGame = canWrite ?? (_ => true),
        };

    [Fact]
    public async Task The_game_is_found_without_asking_and_steam_verify_opens_directly()
    {
        var steam = Path.Combine(_sb.Root, "Steam");
        var game = Path.Combine(steam, "steamapps", "common", "Red Dead Redemption 2");
        Sandbox.Write(game, "RDR2.exe", "exe");
        using var shell = Shell(new FakeProbe(_sb.Root) { Steam = steam });

        await shell.StartAsync(_sb.Home);
        Assert.True(shell.NeedsSetup);
        Assert.Equal("Found your game. Is this the right one?", shell.Wizard.FindText);
        Assert.Equal("Steam", shell.Wizard.Selected!.Title);
        shell.Wizard.HomeDir = _sb.Home;
        await shell.Wizard.UseThisGameCommand.ExecuteAsync(null);

        Assert.Equal(WizardStep.Verify, shell.Wizard.Step);
        Assert.True(shell.Wizard.CanOpenVerify);
        shell.Wizard.OpenVerifyCommand.Execute(null);
        Assert.Equal(["steam://validate/1174180"], _dialogs.Opened);
    }

    [Fact]
    public async Task Existing_mods_are_taken_over_kept_through_the_snapshot_and_turned_back_on()
    {
        Sandbox.Write(_sb.Game, "ScriptHookRDR2.dll", "hook");
        Sandbox.Write(_sb.Game, "dinput8.dll", "loader");
        Sandbox.Write(_sb.Game, "Trainer.asi", "trainer");
        Sandbox.Write(_sb.Game, "lml/stream/horse.ytd", "horse");
        using var shell = Shell();
        await shell.StartAsync(_sb.Home);
        _dialogs.Folders.Enqueue(_sb.Game);
        await shell.Wizard.BrowseCommand.ExecuteAsync(null);
        shell.Wizard.HomeDir = _sb.Home;
        await shell.Wizard.UseThisGameCommand.ExecuteAsync(null);

        Assert.Equal(WizardStep.ExistingMods, shell.Wizard.Step);
        Assert.Equal(4, shell.Wizard.ExistingMods.Count);
        await shell.Wizard.TakeOverModsCommand.ExecuteAsync(null);
        Assert.False(File.Exists(_sb.GameFile("Trainer.asi")), "moved into the library before the snapshot");
        Assert.Equal(WizardStep.Verify, shell.Wizard.Step);

        await shell.Wizard.VerifiedCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Done, shell.Wizard.Step);
        Assert.Contains("existing mods are back on", shell.Wizard.DoneText);
        Assert.Equal("trainer", File.ReadAllText(_sb.GameFile("Trainer.asi")));
        await shell.Wizard.FinishCommand.ExecuteAsync(null);
        Assert.Contains(shell.Home.Mods, m => m.Name == "Mods I already had" && m.Enabled);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task Mods_left_in_the_folder_send_you_back_instead_of_spoiling_the_snapshot()
    {
        using var shell = Shell();
        await shell.StartAsync(_sb.Home);
        _dialogs.Folders.Enqueue(_sb.Game);
        await shell.Wizard.BrowseCommand.ExecuteAsync(null);
        shell.Wizard.HomeDir = _sb.Home;
        await shell.Wizard.UseThisGameCommand.ExecuteAsync(null);
        Sandbox.Write(_sb.Game, "dinput8.dll", "appeared later");

        await shell.Wizard.VerifiedCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.ExistingMods, shell.Wizard.Step);
        Assert.Equal(["dinput8.dll"], shell.Wizard.ExistingMods);
    }

    [Fact]
    public async Task A_folder_without_the_game_is_refused_and_no_write_access_offers_admin()
    {
        using var shell = Shell(canWrite: _ => false);
        await shell.StartAsync(_sb.Home);
        Assert.Contains("Couldn't find", shell.Wizard.FindText);

        _dialogs.Folders.Enqueue(_sb.Src);
        await shell.Wizard.BrowseCommand.ExecuteAsync(null);
        Assert.Contains("doesn't have RDR2.exe", shell.Wizard.FindText);

        _dialogs.Folders.Enqueue(_sb.Game);
        await shell.Wizard.BrowseCommand.ExecuteAsync(null);
        await shell.Wizard.UseThisGameCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.NeedsAdmin, shell.Wizard.Step);
        shell.Wizard.RestartAsAdminCommand.Execute(null);
        Assert.Equal(1, _dialogs.Restarts);
        Assert.Contains("needs permission", shell.Wizard.FindText); // you said no
    }

    [Fact]
    public async Task An_unfinished_setup_resumes_in_the_same_folder()
    {
        using (var ws = _sb.Init()) { } // set up from the CLI, no snapshot yet
        using var shell = Shell();
        await shell.StartAsync(_sb.Home);
        Assert.True(shell.NeedsSetup);
        _dialogs.Folders.Enqueue(_sb.Game);
        await shell.Wizard.BrowseCommand.ExecuteAsync(null);
        Assert.Equal(_sb.Home, shell.Wizard.HomeDir);
    }
}

[Collection("NexusKeyEnv")]
public class HomeTests : IDisposable
{
    private readonly Sandbox _sb = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ShellViewModel _shell;

    public HomeTests() => _shell = new ShellViewModel(_dialogs, _sb.Guard)
    {
        RememberHome = _ => { }, DocumentsDir = _sb.Documents, InstallProbe = new NoInstalls(), CanWriteGame = _ => true,
    };

    public void Dispose()
    {
        _shell.Dispose();
        _sb.Dispose();
    }

    private HomeViewModel Home => _shell.Home;
    private void NoErrors() => Assert.True(_dialogs.Errors.Count == 0, string.Join("; ", _dialogs.Errors.Select(e => e.Message)));
    private Task Ready() => GuiTests.WizardAsync(_shell, _dialogs, _sb);
    private string Hooks() => _sb.Zip("Script Hook RDR2", ("ScriptHookRDR2.dll", "h"), ("dinput8.dll", "l"), ("vfs.asi", "v"), ("lml/mods.xml", "<x/>"));

    [Fact]
    public async Task Adding_mods_applies_them_straight_away_and_the_status_says_so()
    {
        await Ready();
        Assert.Equal("Mods are on, but none are added yet", Home.StatusTitle);
        Assert.Equal("Get ready for Online", Home.PrimaryActionText);

        await Home.AddAsync([Hooks(), _sb.Zip("Trainer", ("Trainer.asi", "t"))]);
        NoErrors();
        Assert.True(File.Exists(_sb.GameFile("Trainer.asi")), "no separate Deploy step");
        Assert.Equal("Mods are on: 2 mod(s)", Home.StatusTitle);
        Assert.Equal("good", Home.StatusLevel);
        Assert.Equal("Added Script Hook RDR2, Trainer.", Home.Message);
        Assert.Equal("needed by other mods", Home.Mods.Single(m => m.Name == "Script Hook RDR2").Detail);
        Assert.Equal(0, _shell.PendingChanges);
    }

    [Fact]
    public async Task Switching_a_mod_off_takes_it_out_of_the_game_right_away()
    {
        await Ready();
        await Home.AddAsync([Hooks(), _sb.Zip("Trainer", ("Trainer.asi", "t"))]);
        await Home.ToggleModCommand.ExecuteAsync(Home.Mods.Single(m => m.Name == "Trainer"));
        Assert.False(File.Exists(_sb.GameFile("Trainer.asi")));
        Assert.False(Home.Mods.Single(m => m.Name == "Trainer").Enabled);
        await Home.ToggleModCommand.ExecuteAsync(Home.Mods.Single(m => m.Name == "Trainer"));
        Assert.True(File.Exists(_sb.GameFile("Trainer.asi")));
    }

    [Fact]
    public async Task A_missing_dependency_is_explained_with_a_download_button()
    {
        await Ready();
        await Home.AddAsync([_sb.Zip("Trainer", ("Trainer.asi", "t"))]);
        Assert.Equal("Mods are waiting", Home.StatusTitle);
        var p = Assert.Single(Home.Problems, x => x.Action == ProblemAction.OpenLink);
        Assert.Contains("need Script Hook RDR2", p.Text);
        await Home.FixCommand.ExecuteAsync(p);
        Assert.Equal([Downloads.ScriptHook], _dialogs.Opened);
        Assert.False(File.Exists(_sb.GameFile("Trainer.asi")));

        await Home.AddAsync([Hooks()]); // the fix: add Script Hook
        Assert.True(File.Exists(_sb.GameFile("Trainer.asi")));
        Assert.DoesNotContain(Home.Problems, x => x.Action == ProblemAction.OpenLink);
    }

    [Fact]
    public async Task Versions_and_unknown_files_are_plain_questions_with_a_safe_default()
    {
        await Ready();
        await Home.AddAsync([Hooks()]);
        _dialogs.Choices.Enqueue(1); // second version
        await Home.AddAsync([_sb.Zip("Saddles", ("Saddles/Normal/install.xml", "<x/>"), ("Saddles/Normal/s.ytd", "n"),
            ("Saddles/Dark/install.xml", "<x/>"), ("Saddles/Dark/s.ytd", "d"))]);
        var (title, options) = _dialogs.Asked.Last();
        Assert.Equal("Pick a version", title);
        Assert.Equal(["Dark", "Normal"], options.Order());
        Assert.True(Directory.Exists(_sb.GameFile("lml/" + options[1])));

        await Home.AddAsync([_sb.Zip("Weather", ("clouds.ytd", "w"), ("weather.ymt", "?"))]);
        Assert.Equal("Some files need a decision", _dialogs.Asked.Last().Title);
        Assert.True(File.Exists(_sb.GameFile("lml/stream/clouds.ytd")), "the recommended answer skips only the unknown file");
        NoErrors();
    }

    [Fact]
    public async Task Cancelling_a_question_leaves_the_mod_off_and_listed_as_needing_a_choice()
    {
        await Ready();
        await Home.AddAsync([Hooks()]);
        _dialogs.Choices.Enqueue(null);
        await Home.AddAsync([_sb.Zip("Opts", ("A/install.xml", "<x/>"), ("A/a.ytd", "a"), ("B/install.xml", "<x/>"), ("B/b.ytd", "b"))]);
        var row = Home.Mods.Single(m => m.Name == "Opts");
        Assert.Equal((false, true, "needs a choice"), (row.Enabled, row.NeedsChoice, row.Detail));

        _dialogs.Choices.Enqueue(0);
        await Home.ToggleModCommand.ExecuteAsync(row); // switching it on asks again
        Assert.True(Home.Mods.Single(m => m.Name == "Opts").Enabled);
    }

    [Fact]
    public async Task Getting_ready_for_online_goes_green_and_back_on_restores_mods()
    {
        await Ready();
        await Home.AddAsync([Hooks(), _sb.Zip("Trainer", ("Trainer.asi", "t"))]);
        await Home.PrimaryCommand.ExecuteAsync(null);
        Assert.False(Home.ModsOn);
        Assert.Equal(("Ready for Online", "good"), (Home.StatusTitle, Home.StatusLevel));
        Assert.Equal("Turn mods back on", Home.PrimaryActionText);
        Assert.False(File.Exists(_sb.GameFile("Trainer.asi")));

        await Home.PrimaryCommand.ExecuteAsync(null);
        Assert.True(Home.ModsOn);
        Assert.True(File.Exists(_sb.GameFile("Trainer.asi")));
        NoErrors();
    }

    [Fact]
    public async Task Strays_turn_online_red_and_clean_up_fixes_it_without_losing_them()
    {
        await Ready();
        Sandbox.Write(_sb.Game, "ReShade.ini", "by hand");
        Sandbox.Write(_sb.Game, "reshade-shaders/Shaders/Bloom.fx", "by hand");
        await Home.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(("Not ready for Online yet", "bad"), (Home.StatusTitle, Home.StatusLevel));
        var fix = Assert.Single(Home.Problems, p => p.Action == ProblemAction.TakeOverStrays);
        Assert.StartsWith("2 file(s)", fix.Text);

        await Home.FixCommand.ExecuteAsync(fix);
        Assert.Equal("Ready for Online", Home.StatusTitle);
        Assert.False(File.Exists(_sb.GameFile("ReShade.ini")));
        Assert.Contains(Home.Mods, m => m.Name == "Found in game folder" && m.Enabled);

        await Home.PrimaryCommand.ExecuteAsync(null); // mods back on
        Assert.Equal("by hand", File.ReadAllText(_sb.GameFile("ReShade.ini")));
        NoErrors();
    }

    [Fact]
    public async Task Changed_game_files_point_at_the_launchers_repair()
    {
        await Ready();
        await _shell.Host.Run(ws => ws.RememberInstall(new GameInstall(ws.GameDir, GameStore.Steam)));
        File.WriteAllText(_sb.GameFile("anim_0.rpf"), "corrupted");
        await Home.PrimaryCommand.ExecuteAsync(null);
        var fix = Assert.Single(Home.Problems, p => p.Action == ProblemAction.RepairGame);
        await Home.FixCommand.ExecuteAsync(fix);
        Assert.Equal(["steam://validate/1174180"], _dialogs.Opened);
        Assert.Contains("Check again", Home.Message);

        File.WriteAllText(_sb.GameFile("anim_0.rpf"), "anim archive"); // what the launcher's repair does
        File.SetLastWriteTimeUtc(_sb.GameFile("anim_0.rpf"), DateTime.UtcNow);
        await Home.CheckAgainCommand.ExecuteAsync(null);
        Assert.Equal("Ready for Online", Home.StatusTitle);
    }

    [Fact]
    public async Task Play_starts_the_game_through_its_launcher()
    {
        await Ready();
        await Home.PlayCommand.ExecuteAsync(null);
        Assert.Contains("Start Red Dead Redemption 2 from", Home.Message); // unknown launcher: says so
        await _shell.Host.Run(ws => ws.RememberInstall(new GameInstall(ws.GameDir, GameStore.Steam)));
        await Home.PlayCommand.ExecuteAsync(null);
        Assert.Equal(["steam://rungameid/1174180"], _dialogs.Opened);
    }

    [Fact]
    public async Task Removing_a_mod_takes_it_out_of_the_game_and_the_library()
    {
        await Ready();
        await Home.AddAsync([Hooks(), _sb.Zip("Trainer", ("Trainer.asi", "t"))]);
        await Home.RemoveModCommand.ExecuteAsync(Home.Mods.Single(m => m.Name == "Trainer"));
        Assert.False(File.Exists(_sb.GameFile("Trainer.asi")));
        Assert.DoesNotContain(Home.Mods, m => m.Name == "Trainer");
        Assert.Equal("Removed Trainer.", Home.Message);
    }

    [Fact]
    public async Task Advanced_mode_is_off_by_default_and_remembered()
    {
        await Ready();
        Assert.False(_shell.AdvancedMode);
        _shell.AdvancedMode = true;
        await _shell.PrefsSaving;
        using var again = new ShellViewModel(new FakeDialogs(), _sb.Guard) { RememberHome = _ => { }, InstallProbe = new NoInstalls() };
        _shell.Host.Dispose();
        await again.StartAsync(_sb.Home);
        Assert.True(again.AdvancedMode);
    }

    [Fact]
    public async Task The_game_running_gives_a_plain_message()
    {
        await Ready();
        _sb.Guard.Running.Add("RDR2");
        await Home.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal("Close the game first", Assert.Single(_dialogs.Errors).Title);
    }
}
