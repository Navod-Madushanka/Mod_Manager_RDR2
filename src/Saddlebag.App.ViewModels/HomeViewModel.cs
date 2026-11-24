using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Saddlebag.Core;
using Saddlebag.Core.Baseline;
using Saddlebag.Core.Deploy;

namespace Saddlebag.App.ViewModels;

public sealed record SimpleModRow(string Id, string Name, string? Version, bool Enabled, bool NeedsChoice, string Detail);

public enum ProblemAction { None, OpenLink, ChooseForMod, TakeOverStrays, RepairGame, Advanced }

/// <summary>Something to fix, in plain words, with at most one button that fixes it.</summary>
public sealed record ProblemRow(string Text, string Level, string? ActionText = null, ProblemAction Action = ProblemAction.None,
    string? Link = null, string? ModId = null);

/// <summary>Where to get the mods other mods depend on. The pages are the official download pages.</summary>
public static class Downloads
{
    public const string ScriptHook = "http://www.dev-c.com/rdr2/scripthookrdr2/";
    public const string Lml = "https://www.rdr2mods.com/downloads/rdr2/tools/76-lennys-mod-loader-rdr/";
    public const string DotNetHook = "https://www.nexusmods.com/reddeadredemption2/mods/70";
}

/// <summary>
/// The everyday screen: are mods on or off, one button to switch, the mod list with switches that apply right away,
/// and anything that needs fixing in plain words. Everything here uses the same engine as the advanced pages.
/// </summary>
public sealed partial class HomeViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<SimpleModRow> Mods { get; } = [];
    public ObservableCollection<ProblemRow> Problems { get; } = [];

    [ObservableProperty] private bool _modsOn = true;
    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusDetail = "";
    /// <summary>good, warn, bad or neutral: the colour of the status card.</summary>
    [ObservableProperty] private string _statusLevel = "neutral";
    [ObservableProperty] private string? _message;

    public string PrimaryActionText => ModsOn ? "Get ready for Online" : "Turn mods back on";
    public string PrimaryActionHint => ModsOn
        ? "Takes every mod out of the game and checks it's back to the clean original."
        : "Puts your mods back for Story Mode.";
    partial void OnModsOnChanged(bool value)
    {
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryActionHint));
    }

    public override async Task RefreshAsync()
    {
        var d = await Host.Run(ws =>
        {
            var online = ws.Store.GetSetting("mode") == "online";
            var plan = ws.Deployer.Plan(leaveOutUnfinished: true);
            var mods = ws.Store.GetMods();
            var profile = ws.Store.GetProfileMods().ToDictionary(p => p.ModId);
            var rows = mods.Select(m =>
            {
                profile.TryGetValue(m.Id, out var p);
                var manifest = ws.Library.LoadManifest(m.Id);
                var open = manifest.PackageType == "variants" || ws.Store.GetModFiles(m.Id).Any(f => f.DestPath is null && f.Kind is "Data" or "Unknown");
                var detail = open ? "needs a choice" : m.IsLinked ? "your dev build" : ws.IsHookMod(m.Id) ? "needed by other mods" : m.Version is { } v ? "version " + v : "";
                return (Row: new SimpleModRow(m.Id, m.Name, m.Version, p?.Enabled == true, open, detail), Priority: p?.Priority ?? 0);
            }).OrderByDescending(x => x.Priority).ThenBy(x => x.Row.Name).Select(x => x.Row).ToList();
            var report = online && ws.Store.BaselineCount() > 0 ? ws.Baseline.Verify() : null;
            var enabled = rows.Count(r => r.Enabled);
            return (online, plan, rows, report, enabled, Updated: ws.Store.BaselineCount() > 0 && ws.Baseline.GameUpdatedSinceBaseline());
        });

        ModsOn = !d.online;
        Mods.Clear();
        foreach (var r in d.rows) Mods.Add(r);

        Problems.Clear();
        if (d.Updated)
            Problems.Add(new("The game was updated. Mods that hook into the game may crash until their authors release updates.", "warn",
                "What now?", ProblemAction.Advanced));
        foreach (var r in d.rows.Where(r => r.Enabled && r.NeedsChoice))
            Problems.Add(new($"{r.Name} needs a choice before it can be used.", "warn", "Choose…", ProblemAction.ChooseForMod, ModId: r.Id));

        if (d.online)
            ShowOnline(d.report);
        else
        {
            foreach (var p in Explain(d.plan)) Problems.Add(p);
            var waiting = d.plan.Steps.Count > 0 && !d.plan.CanApply;
            (StatusTitle, StatusDetail, StatusLevel) = d.enabled == 0
                ? ("Mods are on, but none are added yet", "Add mods with \"Add mods\" or drop them onto this window.", "neutral")
                : waiting
                    ? ("Mods are waiting", "Some mods can't be turned on yet. See below for what to fix.", "warn")
                    : ($"Mods are on: {d.enabled} mod(s)", "Play Story Mode with mods. Before playing Online, press \"Get ready for Online\".", "good");
        }
    }

    /// <summary>Plan checks in plain words, with a download link where one helps.</summary>
    private static IEnumerable<ProblemRow> Explain(DeployPlan plan)
    {
        var ids = plan.Checks.Select(c => c.Id).ToHashSet();
        if (ids.Contains(DeployChecks.MissingScriptHook) || ids.Contains(DeployChecks.MissingAsiLoader))
            yield return new("Some of your mods need Script Hook RDR2. Download it, then add the zip here.", "bad",
                "Get Script Hook RDR2", ProblemAction.OpenLink, Downloads.ScriptHook);
        if (ids.Contains(DeployChecks.MissingLml))
            yield return new("Some of your mods need Lenny's Mod Loader (LML). Download it, then add the zip here.", "bad",
                "Get Lenny's Mod Loader", ProblemAction.OpenLink, Downloads.Lml);
        if (ids.Contains(DeployChecks.MissingDotNetHook))
            yield return new("Some of your mods need ScriptHookRDR2 .NET. Download it, then add the zip here.", "bad",
                "Get ScriptHookRDR2 .NET", ProblemAction.OpenLink, Downloads.DotNetHook);
        foreach (var c in plan.Checks.Where(c => c.Id == DeployChecks.SameDest || c.Id == DeployChecks.StreamName || c.Id == DeployChecks.ReplaceTarget).Take(3))
            yield return new($"Two mods change the same thing. {Plain(c)}", "info");
        if (ids.Contains(DeployChecks.TwoAsiLoaders))
            yield return new("Two ASI loaders are installed (dinput8.dll and an old version.dll). Keeping both can crash the game.", "warn",
                "Show me", ProblemAction.Advanced);
        if (ids.Contains(DeployChecks.DotNetMismatch))
            yield return new("The ScriptHookRDR2 .NET files come from two different releases. Reinstall it from one download.", "warn",
                "Get ScriptHookRDR2 .NET", ProblemAction.OpenLink, Downloads.DotNetHook);
        foreach (var c in plan.Checks.Where(c => c.Id == DeployChecks.BrokenInstallXml).Take(2))
            yield return new($"A mod looks damaged and may not load: {c.Message}", "warn");
        // Blockers that aren't checks (e.g. a missing library file): show as-is.
        foreach (var b in plan.Blockers.Where(b => !plan.Checks.Any(c => b.StartsWith(c.Message))).Take(3))
            yield return new(b, "bad", "Show me", ProblemAction.Advanced);
    }

    private static string Plain(CheckFinding c) => c.Id switch
    {
        DeployChecks.SameDest => c.Message.Replace(" wins over ", " is used instead of ") + " The mod higher in the list wins.",
        DeployChecks.StreamName => c.Message,
        _ => c.Message,
    };

    private void ShowOnline(VerifyReport? report)
    {
        if (report is null)
        {
            (StatusTitle, StatusDetail, StatusLevel) = ("Mods are off", "Press \"Get ready for Online\" to check the game is clean.", "neutral");
            return;
        }
        var strays = report.Findings.Count(f => f.Kind is FindingKind.Extra or FindingKind.ModSignature);
        var vanilla = report.Findings.Count(f => f.Kind is FindingKind.Missing or FindingKind.Changed);
        switch (report.Status)
        {
            case VerifyStatus.Green:
                (StatusTitle, StatusDetail, StatusLevel) = ("Ready for Online",
                    "Mods are off and the game matches your clean snapshot. Start the game and choose Red Dead Online. (Saddlebag checks files; it can't promise anything about bans.)", "good");
                break;
            case VerifyStatus.Amber:
                (StatusTitle, StatusDetail, StatusLevel) = ("Ready for Online, with files you allowed",
                    "Mods are off. A few files you chose to keep are still there (see Advanced → Online / Story).", "warn");
                break;
            default:
                (StatusTitle, StatusDetail, StatusLevel) = ("Not ready for Online yet",
                    "The game folder still has files that aren't part of the original game. Fix them below, then check again.", "bad");
                if (strays > 0)
                    Problems.Add(new($"{strays} file(s) that aren't part of the game are still in the game folder (left by other tools or installed by hand).",
                        "bad", "Clean up (keeps them as a mod)", ProblemAction.TakeOverStrays));
                if (vanilla > 0)
                    Problems.Add(new($"{vanilla} original game file(s) are missing or changed. Your launcher can repair them.",
                        "bad", "Repair game files", ProblemAction.RepairGame));
                break;
        }
        foreach (var f in report.Findings.Where(f => f.Kind == FindingKind.SettingsChanged))
            Problems.Add(new("Your game settings file changed since the snapshot (a VR or graphics mod, or new settings). This doesn't affect Online.", "info",
                "Restore original settings", ProblemAction.Advanced));
    }

    // ---------- the big button ----------

    [RelayCommand]
    private Task PrimaryAsync() => ModsOn ? GetReadyForOnlineAsync() : TurnModsOnAsync();

    private Task GetReadyForOnlineAsync() => Guard(async () =>
    {
        await Host.Run(ws => ws.GoOnline(), "Taking mods out and checking the game…");
        Message = null;
        await Shell.RefreshAllAsync();
    });

    private Task TurnModsOnAsync() => Guard(async () =>
    {
        var r = await Host.Run(ws =>
        {
            ws.Prepare();
            var plan = ws.Deployer.Plan(leaveOutUnfinished: true);
            ws.Store.SetSetting("mode", "story");
            return plan.CanApply ? ws.Deployer.Apply(plan) : null;
        }, "Putting your mods back…");
        Message = r is null ? "Mods are on, but some can't be used yet: see below." : null;
        await Shell.RefreshAllAsync();
    });

    // ---------- play ----------

    [RelayCommand]
    private Task PlayAsync() => Guard(async () =>
    {
        var install = await Host.Run(ws => ws.Install);
        if (install.LaunchUri is { } uri) Dialogs.Open(uri);
        else if (install.LaunchExe is { } exe) Dialogs.Open(exe);
        else Message = $"Start Red Dead Redemption 2 from {install.StoreName}.";
    });

    // ---------- mods ----------

    [RelayCommand]
    private async Task AddModsAsync()
    {
        if (await Dialogs.PickFilesAsync("Pick mod downloads (.zip, .7z, .rar)") is { Length: > 0 } files) await AddAsync(files);
    }

    /// <summary>
    /// Adds dropped or picked mods with plain questions instead of a routing table: which version (if several), and whether
    /// to skip files Saddlebag can't place. Then applies, if mods are on.
    /// </summary>
    public Task AddAsync(IReadOnlyList<string> paths) => Guard(async () =>
    {
        var added = new List<string>();
        var already = 0;
        foreach (var path in paths)
        {
            var r = await Host.Run(ws => ws.Library.Import(path), $"Adding {Path.GetFileName(path)}…");
            if (r.AlreadyImported) already++;
            if (await ResolveAsync(r.Mod.Id, r.Mod.Name, r.Plan)) added.Add(r.Mod.Name);
        }
        await ApplyAsync();
        Message = (added.Count, already) switch
        {
            (0, > 0) => "That mod is already added.",
            (0, _) => "Nothing was added.",
            _ => $"Added {string.Join(", ", added)}." + (ModsOn ? "" : " It goes in when you turn mods back on."),
        };
        await Shell.RefreshAllAsync();
    });

    /// <summary>Asks the plain questions a mod needs; enables it when it's ready. False if you cancelled.</summary>
    private async Task<bool> ResolveAsync(string id, string name, Core.Routing.RoutePlan plan)
    {
        if (plan.NeedsVariant)
        {
            var labels = plan.Variants.Select(v => Core.Util.RelPath.FileName(v)).ToList();
            var pick = await Dialogs.ChooseAsync("Pick a version", $"{name} comes in several versions. Which one do you want?", labels);
            if (pick is not { } i) return false;
            plan = await Host.Run(ws => ws.Library.SetVariant(id, plan.Variants[i]));
        }
        var unresolved = plan.Unresolved.ToList();
        if (unresolved.Count > 0)
        {
            var list = string.Join(", ", unresolved.Take(4).Select(u => Core.Util.RelPath.FileName(u.Source))) + (unresolved.Count > 4 ? ", …" : "");
            var pick = await Dialogs.ChooseAsync("Some files need a decision",
                $"Saddlebag isn't sure where {unresolved.Count} file(s) from {name} go ({list}). Most mods work fine without them; " +
                "if the mod's instructions say where they go, you can place them yourself.",
                ["Skip those files (recommended)", "I'll place them myself (advanced)"]);
            if (pick is null) return false;
            if (pick == 1)
            {
                Shell.AdvancedMode = true;
                await Shell.Mods.RefreshAsync();
                Shell.Mods.Selected = Shell.Mods.Items.FirstOrDefault(m => m.Id == id);
                Message = $"Use the Mods page to say where {name}'s files go, then enable it.";
                return false;
            }
            await Host.Run(ws => { foreach (var u in unresolved) ws.Library.SetOverride(id, u.Source, null); });
        }
        await Host.Run(ws => ws.Store.SetEnabled(id, true));
        return true;
    }

    [RelayCommand]
    private Task ToggleModAsync(SimpleModRow row) => Guard(async () =>
    {
        if (!row.Enabled && row.NeedsChoice)
        {
            var plan = await Host.Run(ws => ws.Library.Reroute(row.Id));
            if (!await ResolveAsync(row.Id, row.Name, plan)) { await RefreshAsync(); return; }
        }
        else await Host.Run(ws => { ws.ThrowIfBisecting(); ws.Store.SetEnabled(row.Id, !row.Enabled); });
        await ApplyAsync();
        await Shell.RefreshAllAsync();
    });

    [RelayCommand]
    private Task RemoveModAsync(SimpleModRow row) => Guard(async () =>
    {
        if (!await Dialogs.ConfirmAsync("Remove mod", $"Remove {row.Name}? It's taken out of the game and deleted from Saddlebag.")) return;
        await Host.Run(ws => ws.Store.SetEnabled(row.Id, false));
        await ApplyAsync();
        var deployed = await Host.Run(ws => ws.Store.GetDeployed().Any(d => d.ModId == row.Id));
        if (deployed)
        {
            Message = $"{row.Name} is turned off, but couldn't be taken out of the game yet (see below). Remove it again once that's fixed.";
            await Shell.RefreshAllAsync();
            return;
        }
        await Host.Run(ws => ws.Library.Remove(row.Id));
        Message = $"Removed {row.Name}.";
        await Shell.RefreshAllAsync();
    });

    /// <summary>When mods are on, makes the game folder match the switches right away. Mods needing a choice are left out.</summary>
    private async Task ApplyAsync()
    {
        if (!ModsOn) return;
        await Host.Run(ws =>
        {
            ws.Prepare();
            var plan = ws.Deployer.Plan(leaveOutUnfinished: true);
            if (plan.CanApply && !plan.IsEmpty) ws.Deployer.Apply(plan);
            if (plan.CanApply) ws.Store.SetSetting("mode", "story");
        }, "Applying…");
    }

    // ---------- problem buttons ----------

    [RelayCommand]
    private Task FixAsync(ProblemRow p) => Guard(async () =>
    {
        switch (p.Action)
        {
            case ProblemAction.OpenLink when p.Link is not null:
                Dialogs.Open(p.Link);
                break;
            case ProblemAction.ChooseForMod when p.ModId is not null:
                var mod = Mods.FirstOrDefault(m => m.Id == p.ModId);
                var plan = await Host.Run(ws => ws.Library.Reroute(p.ModId));
                if (await ResolveAsync(p.ModId, mod?.Name ?? p.ModId, plan)) await ApplyAsync();
                await Shell.RefreshAllAsync();
                break;
            case ProblemAction.TakeOverStrays:
                var r = await Host.Run(ws => ws.TakeOverStrays(), "Cleaning up the game folder…");
                Message = r.Adopted > 0
                    ? $"Moved {r.Adopted} file(s) out of the game folder into a mod called \"Found in game folder\". They come back when you turn mods on."
                    : "Nothing to clean up.";
                await Shell.RefreshAllAsync();
                break;
            case ProblemAction.RepairGame:
                await RepairGameAsync();
                break;
            case ProblemAction.Advanced:
                Shell.AdvancedMode = true;
                break;
        }
    });

    [RelayCommand]
    private Task RepairGameAsync() => Guard(async () =>
    {
        var install = await Host.Run(ws => ws.Install);
        if (install.VerifyUri is { } uri) Dialogs.Open(uri);
        Message = install.VerifyHelp + " Then press \"Check again\".";
    });

    [RelayCommand]
    private Task CheckAgainAsync() => Guard(async () =>
    {
        Message = null;
        await Shell.RefreshAllAsync(); // re-verifies while mods are off
    });
}
