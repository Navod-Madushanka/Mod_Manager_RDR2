using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Saddlebag.Core;
using Saddlebag.Core.Setup;

namespace Saddlebag.App.ViewModels;

public enum WizardStep { FindGame, NeedsAdmin, ExistingMods, Verify, Snapshot, Done }

public sealed record InstallChoice(GameInstall Install)
{
    public string Title => $"{Install.StoreName}";
    public string Folder => Install.Folder;
}

/// <summary>
/// First run, one question per screen: find the game (automatically), take over mods that are already installed,
/// repair game files in the launcher, take the clean snapshot. No jargon, nothing to type.
/// </summary>
public sealed partial class SetupWizardViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<InstallChoice> Installs { get; } = [];
    public ObservableCollection<string> ExistingMods { get; } = [];

    [ObservableProperty] private WizardStep _step = WizardStep.FindGame;
    [ObservableProperty] private InstallChoice? _selected;
    [ObservableProperty] private string _homeDir = "";
    [ObservableProperty] private bool _showHomeChoice;
    [ObservableProperty] private string? _findText;
    [ObservableProperty] private string? _verifyHelp;
    [ObservableProperty] private bool _canOpenVerify;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _doneText;
    [ObservableProperty] private bool _tookOverMods;

    private Workspace? _ws;
    /// <summary>A Saddlebag folder already in use (e.g. set up from the CLI); setup keeps it instead of suggesting another.</summary>
    internal string? FixedHome { get; set; }
    private GameInstall? _install;

    public int StepNumber => Step switch { WizardStep.FindGame or WizardStep.NeedsAdmin => 1, WizardStep.ExistingMods => 2, WizardStep.Verify => 3, WizardStep.Snapshot => 4, _ => 5 };
    partial void OnStepChanged(WizardStep value) => OnPropertyChanged(nameof(StepNumber));
    partial void OnSelectedChanged(InstallChoice? value)
    {
        if (value is not null) HomeDir = FixedHome ?? GameLocator.SuggestHome(value.Folder);
    }

    /// <summary>Looks for the game in Steam, Epic and the Rockstar launcher.</summary>
    [RelayCommand]
    public Task FindAsync() => Guard(async () =>
    {
        var found = await Task.Run(() => GameLocator.FindAll(Shell.InstallProbe));
        Installs.Clear();
        foreach (var g in found) Installs.Add(new InstallChoice(g));
        Selected = Installs.FirstOrDefault();
        FindText = found.Count switch
        {
            0 => "Couldn't find Red Dead Redemption 2 automatically. Click \"Choose folder…\" and pick the folder that contains RDR2.exe.",
            1 => "Found your game. Is this the right one?",
            _ => "Found more than one copy. Pick the one you play.",
        };
    });

    [RelayCommand]
    private Task BrowseAsync() => Guard(async () =>
    {
        if (await Dialogs.PickFolderAsync("Pick the Red Dead Redemption 2 folder (the one with RDR2.exe)") is not { } dir) return;
        if (!File.Exists(Path.Combine(dir, "RDR2.exe")))
        {
            FindText = "That folder doesn't have RDR2.exe in it. Pick the game's main folder.";
            return;
        }
        var g = await Task.Run(() => GameLocator.Describe(dir, Shell.InstallProbe));
        var choice = Installs.FirstOrDefault(i => i.Folder.Equals(g.Folder, StringComparison.OrdinalIgnoreCase)) ?? new InstallChoice(g);
        if (!Installs.Contains(choice)) Installs.Add(choice);
        Selected = choice;
        FindText = "Got it.";
    });

    [RelayCommand]
    private async Task PickHomeAsync()
    {
        if (await Dialogs.PickFolderAsync("Where Saddlebag keeps your mods (best on the same drive as the game)") is { } d) HomeDir = d;
    }

    /// <summary>Step 1 → 2: set Saddlebag up for the chosen game, after checking it may write there.</summary>
    [RelayCommand]
    private Task UseThisGameAsync() => Guard(async () =>
    {
        if (Selected is not { } choice) { FindText = "Pick your game first."; return; }
        if (!await Task.Run(() => Shell.CanWriteGame(choice.Folder))) { Step = WizardStep.NeedsAdmin; return; }
        _install = choice.Install;
        _ws = await Task.Run(() => Workspace.Init(HomeDir, choice.Folder, Shell.Guard));
        if (Shell.DocumentsDir is { } docs) _ws.Store.SetSetting(Core.Baseline.ExternalSettings.DocumentsOverrideKey, docs);
        _ws.RememberInstall(choice.Install);
        Shell.RememberHome(_ws.Home);
        await Shell.AttachAsync(_ws, refresh: false);
        VerifyHelp = choice.Install.VerifyHelp;
        CanOpenVerify = choice.Install.VerifyUri is not null;

        var existing = await Host.Run(ws => ws.FindExistingMods());
        ExistingMods.Clear();
        foreach (var f in existing.Take(50)) ExistingMods.Add(f);
        if (existing.Count > 50) ExistingMods.Add($"…and {existing.Count - 50} more");
        Step = existing.Count > 0 ? WizardStep.ExistingMods : WizardStep.Verify;
    });

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (!Dialogs.RestartAsAdministrator())
            FindText = "Saddlebag needs permission to change files in the game folder. Without it, it can't install mods there.";
    }

    [RelayCommand]
    private void BackToFind() => Step = WizardStep.FindGame;

    /// <summary>Step 2: the mods already installed become one Saddlebag mod (moved into the library, not deleted).</summary>
    [RelayCommand]
    private Task TakeOverModsAsync() => Guard(async () =>
    {
        var files = await Host.Run(ws => ws.FindExistingMods());
        if (files.Count > 0)
        {
            await Host.Run(ws => ws.Adopt(files, "Mods I already had"), "Moving your mods into Saddlebag…");
            TookOverMods = true;
        }
        Step = WizardStep.Verify;
    });

    [RelayCommand]
    private void OpenVerify()
    {
        if (_install?.VerifyUri is { } uri) Dialogs.Open(uri);
    }

    /// <summary>Step 3 → 4: the files are repaired, so take the clean snapshot.</summary>
    [RelayCommand]
    private Task VerifiedAsync() => Guard(async () =>
    {
        // Mod files still in the folder would spoil the snapshot: offer to take them over first.
        var leftovers = await Host.Run(ws => ws.FindExistingMods());
        if (leftovers.Count > 0)
        {
            ExistingMods.Clear();
            foreach (var f in leftovers.Take(50)) ExistingMods.Add(f);
            if (leftovers.Count > 50) ExistingMods.Add($"…and {leftovers.Count - 50} more");
            Step = WizardStep.ExistingMods;
            return;
        }
        Step = WizardStep.Snapshot;
        var progress = new Progress<(int Done, int Total, string Path)>(p => Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total);
        await Host.Run(ws => ws.CaptureBaseline(force: false, progress), "Taking a snapshot of your clean game…");
        Progress = 1;
        if (TookOverMods) await Host.Run(ws => ws.GoStory(ignoreBlockingChecks: true), "Turning your mods back on…");
        DoneText = TookOverMods
            ? "All set. Your existing mods are back on, now managed by Saddlebag. Add more any time, and use \"Get ready for Online\" before playing Online."
            : "All set. Add mods with \"Add mods\" or by dropping them on the window. Use \"Get ready for Online\" before playing Online.";
        Step = WizardStep.Done;
    });

    [RelayCommand]
    private Task FinishAsync() => Shell.FinishSetupAsync();
}
