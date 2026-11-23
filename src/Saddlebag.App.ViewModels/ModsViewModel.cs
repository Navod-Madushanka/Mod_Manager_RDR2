using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Saddlebag.Core.Nexus;
using Saddlebag.Core.Routing;

namespace Saddlebag.App.ViewModels;

public sealed record ModItem(string Id, string Name, string? Version, bool Enabled, int? Priority, int DeployedFiles,
    string Source, bool IsLinked, bool LmlPacked, bool NexusLinked, int OpenQuestions);

/// <summary>The library with the winner on top (highest priority first), and the drop zone that imports archives.</summary>
public sealed partial class ModsViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<ModItem> Items { get; } = [];
    public StagingViewModel Staging { get; } = new(shell);
    [ObservableProperty] private ModItem? _selected;

    partial void OnSelectedChanged(ModItem? value)
    {
        if (value is not null) _ = Guard(() => Staging.LoadAsync(value.Id));
    }

    public override async Task RefreshAsync()
    {
        var keep = Selected?.Id;
        var rows = await Host.Run(ws =>
        {
            var profile = ws.Store.GetProfileMods().ToDictionary(p => p.ModId);
            var deployed = ws.Store.GetDeployed().GroupBy(d => d.ModId).ToDictionary(g => g.Key, g => g.Count());
            return ws.Store.GetMods().Select(m =>
            {
                profile.TryGetValue(m.Id, out var p);
                var manifest = ws.Library.LoadManifest(m.Id);
                var open = ws.Store.GetModFiles(m.Id).Count(f => f.DestPath is null && f.Kind is "Data" or "Unknown") + (manifest.PackageType == "variants" ? 1 : 0);
                return new ModItem(m.Id, m.Name, m.Version, p?.Enabled == true, p?.Priority, deployed.GetValueOrDefault(m.Id),
                    m.LinkedPath is not null ? "linked: " + m.LinkedPath : m.SourceUrl ?? "",
                    m.IsLinked, manifest.LmlPack?.Enabled == true, manifest.Nexus is not null, open);
            }).OrderByDescending(i => i.Priority ?? int.MinValue).ThenBy(i => i.Name).ToList();
        });
        Items.Clear();
        foreach (var r in rows) Items.Add(r);
        Selected = keep is null ? null : Items.FirstOrDefault(i => i.Id == keep);
        if (Selected is null) Staging.Clear();
    }

    /// <summary>Imports dropped or picked archives/folders. Mods that route without questions are enabled right away.</summary>
    public Task ImportAsync(IReadOnlyList<string> paths) => Guard(async () =>
    {
        string? last = null;
        var needAnswers = 0;
        foreach (var path in paths)
        {
            var r = await Host.Run(ws =>
            {
                var imported = ws.Library.Import(path);
                if (imported.Plan.IsComplete) ws.Store.SetEnabled(imported.Mod.Id, true);
                return imported;
            }, $"Importing {Path.GetFileName(path)}…");
            last = r.Mod.Id;
            if (!r.Plan.IsComplete) needAnswers++;
        }
        await Shell.RefreshAllAsync();
        if (last is not null) Selected = Items.FirstOrDefault(i => i.Id == last);
        Shell.StatusText = needAnswers > 0
            ? $"Imported {paths.Count}; {needAnswers} need an answer about where files go (see the routing table). Then enable and deploy."
            : $"Imported and enabled {paths.Count}. Deploy on the Deploy page when ready.";
    });

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        if (await Dialogs.PickFilesAsync("Mod archives (.zip, .7z, .rar) or loose files") is { Length: > 0 } files) await ImportAsync(files);
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (await Dialogs.PickFolderAsync("An extracted mod folder") is { } dir) await ImportAsync([dir]);
    }

    /// <summary>Dev mode: a build output folder becomes a mod that's copied in fresh on every deploy.</summary>
    [RelayCommand]
    private Task LinkFolderAsync() => Guard(async () =>
    {
        if (await Dialogs.PickFolderAsync("Your build output folder, e.g. bin\\Release") is not { } dir) return;
        if (await Dialogs.PromptAsync("Link build folder", "Name for this mod:", new DirectoryInfo(dir).Name) is not { Length: > 0 } name) return;
        var id = await Host.Run(ws =>
        {
            var r = ws.Library.Link(dir, name);
            ws.Store.SetEnabled(r.Mod.Id, true);
            return r.Mod.Id;
        });
        await Shell.RefreshAllAsync();
        Selected = Items.FirstOrDefault(i => i.Id == id);
    });

    [RelayCommand]
    private Task ToggleEnabledAsync(ModItem item) => Guard(async () =>
    {
        await Host.Run(ws => { ws.ThrowIfBisecting(); ws.Store.SetEnabled(item.Id, !item.Enabled); });
        await Shell.RefreshAllAsync();
    });

    /// <summary>Higher priority wins when two mods write the same file.</summary>
    [RelayCommand]
    private Task RaisePriorityAsync(ModItem item) => Move(item, +1);

    [RelayCommand]
    private Task LowerPriorityAsync(ModItem item) => Move(item, -1);

    private Task Move(ModItem item, int by) => Guard(async () =>
    {
        if (item.Priority is not { } p) { Shell.StatusText = "Enable the mod first to give it a place in the order."; return; }
        await Host.Run(ws => { ws.ThrowIfBisecting(); ws.Store.MoveMod(item.Id, Math.Max(1, p + by)); });
        await Shell.RefreshAllAsync();
    });

    [RelayCommand]
    private Task RemoveAsync(ModItem item) => Guard(async () =>
    {
        if (!await Dialogs.ConfirmAsync("Remove mod", $"Delete {item.Name} from the library? Its files must not be deployed (disable it and deploy first).")) return;
        await Host.Run(ws => ws.Library.Remove(item.Id));
        await Shell.RefreshAllAsync();
    });
}

public sealed partial class RouteRow(RoutedFile f, string? gamePath) : ObservableObject
{
    public string Source { get; } = f.Source;
    public string? Dest { get; } = f.Dest;
    public string RuleId { get; } = f.RuleId;
    public Confidence Confidence { get; } = f.Confidence;
    public string Reason { get; } = f.Reason;
    public FileKind Kind { get; } = f.Kind;
    public bool NeedsDecision { get; } = f.NeedsDecision;
    public bool IsOverride => RuleId == "override";
    public bool IsGenerated => Source.StartsWith(Core.Library.ModLibrary.GeneratedPrefix, StringComparison.Ordinal);
    public string DestText => Dest ?? (NeedsDecision ? "? needs an answer" : "not deployed");
    /// <summary>Editable destination: type a game path and apply it as this mod's saved answer.</summary>
    [ObservableProperty] private string _editDest = f.Dest ?? "";
    /// <summary>For LML packaging: the game path this file replaces, e.g. update:/x64/data/metadata/cameras.ymt.</summary>
    [ObservableProperty] private string _gamePath = gamePath ?? "";
}

/// <summary>Where each file of the selected mod goes, which rule decided it, and your overrides.</summary>
public sealed partial class StagingViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    [ObservableProperty] private string? _modId;
    [ObservableProperty] private string? _modName;
    [ObservableProperty] private string? _packageType;
    [ObservableProperty] private bool _isLmlPacked;
    /// <summary>Folder under lml/ for the package; empty means the mod's name.</summary>
    [ObservableProperty] private string _lmlFolder = "";
    /// <summary>Author written into the generated install.xml.</summary>
    [ObservableProperty] private string _lmlAuthor = "";
    /// <summary>Nexus page URL or mod id.</summary>
    [ObservableProperty] private string _nexusTarget = "";
    /// <summary>The archive name as downloaded from Nexus, e.g. Trainer-1234-1-2-1700000000.zip: lets the update check match the exact file.</summary>
    [ObservableProperty] private string _nexusFile = "";
    [ObservableProperty] private string _installedVersion = "";
    [ObservableProperty] private bool _isLinked;
    [ObservableProperty] private string? _nexusText;
    [ObservableProperty] private string? _selectedVariant;
    public ObservableCollection<RouteRow> Rows { get; } = [];
    public ObservableCollection<string> Variants { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    public bool HasMod => ModId is not null;
    public bool NeedsVariant => Variants.Count > 0;
    partial void OnModIdChanged(string? value) => OnPropertyChanged(nameof(HasMod));

    public void Clear()
    {
        ModId = null;
        ModName = null;
        Rows.Clear();
        Variants.Clear();
        Notes.Clear();
        OnPropertyChanged(nameof(NeedsVariant));
    }

    public async Task LoadAsync(string id)
    {
        var d = await Host.Run(ws =>
        {
            var mod = ws.Store.GetMod(id)!;
            var manifest = ws.Library.LoadManifest(id);
            return (mod, manifest, Plan: ws.Library.Reroute(id));
        });
        ModId = id;
        ModName = d.mod.Name;
        PackageType = d.Plan.PackageType;
        IsLinked = d.mod.IsLinked;
        IsLmlPacked = d.manifest.LmlPack?.Enabled == true;
        LmlFolder = d.manifest.LmlPack?.Folder ?? "";
        LmlAuthor = d.manifest.LmlPack?.Author ?? "";
        var nx = d.manifest.Nexus;
        NexusText = nx is null ? "Not linked to Nexus"
            : $"Nexus mod {nx.ModId}{(nx.Guessed ? " (guessed from the file name)" : "")}{(nx.FileName is null ? "; no exact file, so updates are judged by version" : "; exact file known")}";
        NexusTarget = nx is null ? "" : $"https://www.nexusmods.com/{nx.Game}/mods/{nx.ModId}";
        NexusFile = nx?.FileName ?? "";
        InstalledVersion = d.mod.Version ?? "";
        Rows.Clear();
        foreach (var f in d.Plan.Files.Where(f => f.Kind != FileKind.Skipped || f.RuleId == "override"))
            Rows.Add(new RouteRow(f, d.manifest.LmlPack?.GamePaths.GetValueOrDefault(f.Source)));
        Variants.Clear();
        foreach (var v in d.Plan.Variants) Variants.Add(v);
        OnPropertyChanged(nameof(NeedsVariant));
        Notes.Clear();
        foreach (var note in d.manifest.Notes) Notes.Add(note);
    }

    private Task Edit(Action<Core.Workspace, string> change) => Guard(async () =>
    {
        if (ModId is not { } id) return;
        await Host.Run(ws => change(ws, id));
        await LoadAsync(id);
        await Shell.Mods.RefreshAsync();
        await Shell.Plan.RefreshAsync();
    });

    [RelayCommand]
    private Task ApplyDestAsync(RouteRow row) =>
        string.IsNullOrWhiteSpace(row.EditDest)
            ? Guard(() => { Shell.StatusText = "Type a game-relative destination first, e.g. lml/replace/x64/data/weather.ymt."; return Task.CompletedTask; })
            : Edit((ws, id) => ws.Library.SetOverride(id, row.Source, row.EditDest.Trim()));

    [RelayCommand]
    private Task SkipAsync(RouteRow row) => Edit((ws, id) => ws.Library.SetOverride(id, row.Source, null));

    [RelayCommand]
    private Task ClearOverrideAsync(RouteRow row) => Edit((ws, id) => ws.Library.ClearOverride(id, row.Source));

    [RelayCommand]
    private Task ChooseVariantAsync() => SelectedVariant is null
        ? Task.CompletedTask
        : Edit((ws, id) =>
        {
            var plan = ws.Library.SetVariant(id, SelectedVariant);
            if (plan.IsComplete) ws.Store.SetEnabled(id, true);
        });

    [RelayCommand]
    private Task EnableAsync() => Edit((ws, id) => ws.Store.SetEnabled(id, true));

    [RelayCommand]
    private Task PackForLmlAsync() => SetLmlPackAsync(true);

    [RelayCommand]
    private Task UnpackAsync() => SetLmlPackAsync(false);

    /// <summary>Package as an LML mod with a generated install.xml, using the game paths typed into the rows.</summary>
    public Task SetLmlPackAsync(bool on) => Edit((ws, id) =>
    {
        if (!on) { ws.Library.SetLmlPack(id, null); return; }
        var opt = new LmlPackOptions
        {
            Enabled = true,
            Folder = string.IsNullOrWhiteSpace(LmlFolder) ? null : LmlFolder.Trim(),
            Author = string.IsNullOrWhiteSpace(LmlAuthor) ? null : LmlAuthor.Trim(),
        };
        foreach (var r in Rows.Where(r => !string.IsNullOrWhiteSpace(r.GamePath) && !r.IsGenerated)) opt.GamePaths[r.Source] = r.GamePath.Trim();
        ws.Library.SetLmlPack(id, opt);
    });

    [RelayCommand]
    private Task UnlinkNexusAsync() => Edit((ws, id) => ws.Library.SetNexusLink(id, null));

    /// <summary>Saves the Nexus fields: page (required), downloaded file name and installed version (optional).</summary>
    [RelayCommand]
    private Task SaveNexusLinkAsync() =>
        string.IsNullOrWhiteSpace(NexusTarget)
            ? Guard(() => { Shell.StatusText = "Paste the mod's Nexus page (or its mod id) first."; return Task.CompletedTask; })
            : Edit((ws, id) => ws.Library.LinkNexus(id, NexusTarget, NexusFile, InstalledVersion, keepKnownFile: false));
}
