using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Saddlebag.Core;
using Saddlebag.Core.State;

namespace Saddlebag.App.ViewModels;

/// <summary>First run: pick the game folder and a Saddlebag folder on the same drive.</summary>
public sealed partial class SetupViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    [ObservableProperty] private string _gameDir = "";
    [ObservableProperty] private string _homeDir = "";
    [ObservableProperty] private string? _result;

    partial void OnGameDirChanged(string value)
    {
        // Suggest a library on the game's drive, so hard links work.
        if (string.IsNullOrWhiteSpace(HomeDir) && Path.GetPathRoot(value) is { Length: > 0 } root)
            HomeDir = Path.Combine(root, "Saddlebag");
    }

    [RelayCommand]
    private async Task PickGameAsync()
    {
        if (await Dialogs.PickFolderAsync("The folder that holds RDR2.exe") is { } d) GameDir = d;
    }

    [RelayCommand]
    private async Task PickHomeAsync()
    {
        if (await Dialogs.PickFolderAsync("Where Saddlebag keeps mods and backups (same drive as the game)") is { } d) HomeDir = d;
    }

    [RelayCommand]
    private Task InitAsync() => Guard(async () =>
    {
        var ws = await Task.Run(() => Workspace.Init(HomeDir, GameDir, Shell.Guard));
        if (Shell.DocumentsDir is { } docs) ws.Store.SetSetting(Core.Baseline.ExternalSettings.DocumentsOverrideKey, docs);
        Shell.RememberHome(ws.Home);
        var links = await Task.Run(ws.HardLinksWork);
        Result = links
            ? "Hard links work: mods are stored once and cost no extra disk."
            : "Hard links don't work here (different drive, or no write access to the game folder). Files will be copied instead.";
        await Shell.OnSetupCompleteAsync(ws);
        Shell.StatusText = "Set up. Next: move any mods out of the game folder, verify files in your launcher, then capture the baseline on the Settings page.";
    });
}

public sealed record PathRuleRow(string Path, string Rule);

/// <summary>Baseline, allowed extras and expected changes, and where things live.</summary>
public sealed partial class SettingsViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    [ObservableProperty] private string? _baselineText;
    [ObservableProperty] private string? _paths;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _capturing;
    public ObservableCollection<PathRuleRow> Rules { get; } = [];

    public override async Task RefreshAsync()
    {
        var info = await Host.Run(ws =>
        {
            var (version, at) = ws.Store.GetBaselineInfo();
            return (Count: ws.Store.BaselineCount(), version, at, ws.Home, ws.GameDir, Rules: ws.Store.GetPathRules());
        });
        BaselineText = info.at is null
            ? "No baseline yet."
            : $"{info.Count} files, captured {info.at:yyyy-MM-dd HH:mm} UTC, game {info.version ?? "?"}.";
        Paths = $"Game: {info.GameDir}\nSaddlebag: {info.Home}";
        Rules.Clear();
        foreach (var (p, r) in info.Rules.OrderBy(kv => kv.Key)) Rules.Add(new PathRuleRow(p, r == StateStore.RuleAllow ? "allowed extra" : "expected to change"));
    }

    [RelayCommand]
    private Task CaptureBaselineAsync() => Capture(force: false);

    [RelayCommand]
    private Task ForceCaptureBaselineAsync() => Capture(force: true);

    private Task Capture(bool force) => Guard(async () =>
    {
        if (!await Dialogs.ConfirmAsync("Capture baseline",
                "The baseline is what Online mode checks against, so the game folder must be clean: mods moved out and files verified in your launcher. Capture now?"))
            return;
        Capturing = true;
        try
        {
            var progress = new Progress<(int Done, int Total, string Path)>(p => Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total);
            await Host.Run(ws => ws.CaptureBaseline(force, progress), "Hashing game files…");
            Progress = 1;
        }
        finally { Capturing = false; }
        await Shell.RefreshAllAsync();
    });

    [RelayCommand]
    private Task AddAllowedAsync() => AddRule(StateStore.RuleAllow, "Allow an extra file", "Game-relative path, e.g. x64/data/startup.meta. It will show amber in Online mode every time.");

    [RelayCommand]
    private Task AddExpectedAsync() => AddRule(StateStore.RuleExpectChange, "Expected to change", "Game-relative path of a vanilla file the launcher rewrites, e.g. a DLSS DLL.");

    private Task AddRule(string rule, string title, string message) => Guard(async () =>
    {
        if (await Dialogs.PromptAsync(title, message) is not { Length: > 0 } path) return;
        await Host.Run(ws => ws.Store.SetPathRule(Core.Util.RelPath.Normalize(path), rule));
        await RefreshAsync();
    });

    [RelayCommand]
    private Task RemoveRuleAsync(PathRuleRow row) => Guard(async () =>
    {
        await Host.Run(ws => ws.Store.SetPathRule(row.Path, null));
        await RefreshAsync();
    });
}
