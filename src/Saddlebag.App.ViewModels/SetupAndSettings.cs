using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Saddlebag.Core;
using Saddlebag.Core.State;

namespace Saddlebag.App.ViewModels;

public sealed record PathRuleRow(string Path, string Rule);

/// <summary>Baseline, allowed extras and expected changes, and where things live.</summary>
public sealed partial class SettingsViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    [ObservableProperty] private string? _baselineText;
    [ObservableProperty] private string? _paths;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _capturing;
    [ObservableProperty] private string? _rulesText;
    public ObservableCollection<PathRuleRow> Rules { get; } = [];

    public override async Task RefreshAsync()
    {
        var info = await Host.Run(ws =>
        {
            var (version, at) = ws.Store.GetBaselineInfo();
            return (Count: ws.Store.BaselineCount(), version, at, ws.Home, ws.GameDir, Rules: ws.Store.GetPathRules(),
                RuleSource: ws.Rules.Source, Settings: ws.Baseline.External?.Records().Count ?? 0);
        });
        BaselineText = info.at is null
            ? "No baseline yet."
            : $"{info.Count} files, captured {info.at:yyyy-MM-dd HH:mm} UTC, game {info.version ?? "?"}." +
              (info.Settings > 0 ? $" Plus {info.Settings} game settings file(s) from Documents." : "");
        RulesText = info.RuleSource == "built-in"
            ? "Routing rules: built-in."
            : $"Routing rules: your file {info.RuleSource}.";
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

    /// <summary>Writes the built-in rules to rules.json in the Saddlebag folder for editing. Takes effect next start.</summary>
    [RelayCommand]
    private Task ExportRulesAsync() => Guard(async () =>
    {
        var path = await Host.Run(ws => Core.Workspace.RulesPath(ws.Home));
        if (File.Exists(path) && !await Dialogs.ConfirmAsync("Export rules", $"{path} already exists. Replace it with the built-in rules?")) return;
        await File.WriteAllTextAsync(path, Core.Routing.RuleSet.DefaultJson);
        Shell.StatusText = $"Wrote {path}. Edit it, then restart Saddlebag; mods are re-routed on start.";
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
