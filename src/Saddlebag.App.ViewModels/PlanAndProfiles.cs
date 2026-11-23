using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Saddlebag.Core.Deploy;

namespace Saddlebag.App.ViewModels;

public sealed record StepRow(string Action, string Dest, string Mod, string Detail);
public sealed record CheckRow(Severity Severity, string Id, string Message, string? Hint);

/// <summary>A dry run of the next deploy, with every check; Deploy applies exactly this.</summary>
public sealed partial class PlanViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<StepRow> Steps { get; } = [];
    public ObservableCollection<CheckRow> Checks { get; } = [];
    public ObservableCollection<string> Blockers { get; } = [];
    [ObservableProperty] private bool _ignoreDeps;
    [ObservableProperty] private bool _canApply;
    [ObservableProperty] private string? _summary;

    partial void OnIgnoreDepsChanged(bool value) => _ = Guard(RefreshAsync);

    public override async Task RefreshAsync()
    {
        var plan = await Host.Run(ws => ws.Deployer.Plan(ignoreBlockingChecks: IgnoreDeps));
        Steps.Clear();
        foreach (var s in plan.Steps)
            Steps.Add(new StepRow(s.Type.ToString().ToLowerInvariant(), s.Dest, s.ModId ?? "",
                s.Type == StepType.Place ? s.Method! : s.Note ?? (s.Type == StepType.Restore ? "original back from backups" : "")));
        Checks.Clear();
        foreach (var c in plan.Checks.OrderByDescending(c => c.Severity)) Checks.Add(new CheckRow(c.Severity, c.Id, c.Message, c.Hint));
        Blockers.Clear();
        foreach (var b in plan.Blockers.Where(b => !plan.Checks.Any(c => b.StartsWith(c.Message)))) Blockers.Add(b);
        CanApply = plan.CanApply && !plan.IsEmpty;
        Summary = plan.IsEmpty ? "Nothing to do: the game folder already matches."
            : !plan.CanApply ? "Blocked: fix the items above (or tick “ignore dependency checks” if a mod really doesn't need what's missing)."
            : $"{plan.Steps.Count} file operation(s) ready.";
    }

    [RelayCommand]
    private Task DeployAsync() => Guard(async () =>
    {
        var r = await Host.Run(ws =>
        {
            ws.Prepare();
            var res = ws.Deployer.Apply(ws.Deployer.Plan(ignoreBlockingChecks: IgnoreDeps));
            ws.Store.SetSetting("mode", "story");
            return res;
        }, "Deploying…");
        Shell.StatusText = $"Deployed: placed {r.Placed}, removed {r.Removed}, backed up {r.BackedUp}, restored {r.Restored} file(s)." +
                           (r.Notes.Count > 0 ? " " + string.Join(" ", r.Notes) : "");
        await Shell.RefreshAllAsync();
    });

    [RelayCommand]
    private Task PurgeAsync() => Guard(async () =>
    {
        if (!await Dialogs.ConfirmAsync("Purge", "Remove every mod file Saddlebag deployed and restore the originals? Your library is kept.")) return;
        var r = await Host.Run(ws => ws.Purge(), "Purging…");
        Shell.StatusText = $"Purged: removed {r.Removed}, restored {r.Restored} file(s).";
        await Shell.RefreshAllAsync();
    });
}

public sealed record ProfileItem(string Id, string Name, int EnabledMods, bool IsActive);

/// <summary>Named sets of mods with their own order. Switching in Story mode moves only the files that differ.</summary>
public sealed partial class ProfilesViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<ProfileItem> Items { get; } = [];
    [ObservableProperty] private ProfileItem? _active;
    /// <summary>The header's profile picker. Picking another profile switches to it.</summary>
    [ObservableProperty] private ProfileItem? _picked;
    private bool _loading;

    partial void OnPickedChanged(ProfileItem? value)
    {
        if (!_loading && value is { IsActive: false }) _ = SwitchCommand.ExecuteAsync(value);
    }

    public override async Task RefreshAsync()
    {
        _loading = true;
        try { await Load(); }
        finally { _loading = false; }
    }

    private async Task Load()
    {
        var rows = await Host.Run(ws => ws.Store.GetProfiles()
            .Select(p => new ProfileItem(p.Id, p.Name, ws.Store.GetProfileMods(p.Id).Count(m => m.Enabled), p.Id == ws.Store.ActiveProfile))
            .Where(p => !p.Id.StartsWith("bisect-", StringComparison.Ordinal) || p.IsActive)
            .ToList());
        Items.Clear();
        foreach (var r in rows) Items.Add(r);
        Active = Items.FirstOrDefault(i => i.IsActive);
        Picked = Active;
    }

    [RelayCommand]
    private Task SwitchAsync(ProfileItem item) => Guard(async () =>
    {
        if (item.IsActive) return;
        var r = await Host.Run(ws => ws.SwitchProfile(item.Id), $"Switching to {item.Name}…");
        Shell.StatusText = r is null ? $"Switched to {item.Name}. You're in Online mode, so nothing was deployed."
            : $"Switched to {item.Name}: placed {r.Placed}, removed {r.Removed} file(s).";
        await Shell.RefreshAllAsync();
    });

    [RelayCommand]
    private Task CreateAsync() => Guard(async () =>
    {
        if (await Dialogs.PromptAsync("New profile", "Name (it starts as a copy of the active profile):", "Story-Dev") is not { Length: > 0 } name) return;
        await Host.Run(ws => { ws.ThrowIfBisecting(); ws.Store.CreateProfile(Core.Library.ModLibrary.Slug(name), name, ws.Store.ActiveProfile); });
        await RefreshAsync();
    });

    [RelayCommand]
    private Task DeleteAsync(ProfileItem item) => Guard(async () =>
    {
        if (!await Dialogs.ConfirmAsync("Delete profile", $"Delete {item.Name}? Its mods stay in the library.")) return;
        await Host.Run(ws => { ws.ThrowIfBisecting(); ws.Store.DeleteProfile(item.Id); });
        await RefreshAsync();
    });
}
