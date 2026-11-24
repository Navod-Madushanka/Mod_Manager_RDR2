using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Saddlebag.Core.Baseline;
using Saddlebag.Core.State;

namespace Saddlebag.App.ViewModels;

public sealed partial class FindingRow(Finding f) : ObservableObject
{
    public FindingKind Kind { get; } = f.Kind;
    public string Path { get; } = f.Path;
    public string Detail { get; } = f.Detail;
    /// <summary>red for offenders, amber for things you allowed or expect, info for settings files (never affect status).</summary>
    public string Severity => Kind switch
    {
        FindingKind.SettingsChanged => "info",
        FindingKind.AllowedExtra or FindingKind.ExpectedChange => "amber",
        _ => "red",
    };
    public bool CanRestore => Kind == FindingKind.SettingsChanged;
    public bool CanAdopt => Kind is FindingKind.Extra or FindingKind.ModSignature or FindingKind.Changed && !Path.EndsWith('/');
    public bool CanAllow => Kind is FindingKind.Extra or FindingKind.ModSignature or FindingKind.Changed;
    public bool CanExpect => Kind is FindingKind.Changed;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>The Online/Story switch and what the last verify found. Green only when the folder matches the baseline.</summary>
public sealed partial class ModeViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    [ObservableProperty] private VerifyStatus? _status;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private bool _deep;
    [ObservableProperty] private bool _isOnline;
    public ObservableCollection<FindingRow> Findings { get; } = [];

    public string StatusText => Status switch
    {
        VerifyStatus.Green => "GREEN: folder matches your clean baseline",
        VerifyStatus.Amber => "AMBER: matches the baseline except extras you chose to allow",
        VerifyStatus.Red => "RED: folder does not match the baseline",
        _ => "Not verified yet",
    };

    partial void OnStatusChanged(VerifyStatus? value) => OnPropertyChanged(nameof(StatusText));

    public override async Task RefreshAsync() => IsOnline = await Host.Run(ws => ws.Store.GetSetting("mode") == "online");

    private void Show(VerifyReport r, string? extra = null)
    {
        Status = r.Status;
        Findings.Clear();
        foreach (var f in r.Findings.Where(f => f.Kind != FindingKind.ExpectedChange || r.Status != VerifyStatus.Green)) Findings.Add(new FindingRow(f));
        Summary = $"{r.FilesChecked} files checked, {r.FilesHashed} hashed{(r.Deep ? " (deep)" : "")}. This proves file state only; it says nothing about bans." +
                  (extra is null ? "" : "\n" + extra);
    }

    [RelayCommand]
    private Task ToggleModeAsync() => IsOnline ? GoStoryAsync() : GoOnlineAsync();

    [RelayCommand]
    private Task GoOnlineAsync() => Guard(async () =>
    {
        var r = await Host.Run(ws => ws.GoOnline(Deep), "Online mode: purging and verifying…");
        var logs = r.ArchivedLogs is { Count: > 0 } l ? $"Moved {l.Count} mod log(s) out of the game folder (see Logs)." : null;
        Show(r.Report, $"Removed {r.Purge.Removed}, restored {r.Purge.Restored} file(s). {logs}".Trim());
        await Shell.RefreshAllAsync();
    });

    [RelayCommand]
    private Task GoStoryAsync() => Guard(async () =>
    {
        var r = await Host.Run(ws => ws.GoStory(), "Story mode: deploying your mods…");
        Status = null;
        Findings.Clear();
        Summary = $"Story mode: placed {r.Placed}, removed {r.Removed} file(s)." + (r.Notes.Count > 0 ? "\n" + string.Join("\n", r.Notes) : "");
        await Shell.RefreshAllAsync();
    });

    [RelayCommand]
    private Task VerifyAsync() => Guard(async () =>
    {
        var r = await Host.Run(ws => ws.Baseline.Verify(Deep), Deep ? "Hashing every file…" : "Checking files…");
        Show(r);
    });

    /// <summary>Puts a settings file back to its baseline copy; the current one is saved first.</summary>
    [RelayCommand]
    private Task RestoreSettingsAsync(FindingRow row) => Guard(async () =>
    {
        if (!await Dialogs.ConfirmAsync("Restore settings", $"Put {row.Path} back to the copy taken with the baseline? The current file is saved first.")) return;
        var saved = await Host.Run(ws => ws.RestoreSettings(row.Path));
        Shell.StatusText = saved is null ? "Restored." : $"Restored. The previous file is saved at {saved}.";
        await VerifyAsync();
    });

    [RelayCommand]
    private Task AllowAsync(FindingRow row) => Rule(row, StateStore.RuleAllow);

    [RelayCommand]
    private Task ExpectAsync(FindingRow row) => Rule(row, StateStore.RuleExpectChange);

    private Task Rule(FindingRow row, string rule) => Guard(async () =>
    {
        await Host.Run(ws => ws.Store.SetPathRule(row.Path, rule));
        await VerifyAsync();
        await Shell.Settings.RefreshAsync();
    });

    /// <summary>Turns the selected untracked files into one tracked mod.</summary>
    [RelayCommand]
    private Task AdoptSelectedAsync() => Guard(async () =>
    {
        var paths = Findings.Where(f => f.IsSelected && f.CanAdopt).Select(f => f.Path).ToList();
        if (paths.Count == 0) { Shell.StatusText = "Tick the files to adopt first."; return; }
        if (await Dialogs.PromptAsync("Adopt as a mod", $"Name for the mod made from these {paths.Count} file(s):", "Manual install") is not { Length: > 0 } name) return;
        var r = await Host.Run(ws => ws.Adopt(paths, name));
        Shell.StatusText = r.ReplacedVanilla.Count > 0
            ? $"Adopted. {r.ReplacedVanilla.Count} of them replaced vanilla files: verify files in your launcher to get the originals back, then switch to Story."
            : "Adopted and enabled. Switch to Story to put them back as tracked files.";
        await Shell.RefreshAllAsync();
        await VerifyAsync();
    });
}
