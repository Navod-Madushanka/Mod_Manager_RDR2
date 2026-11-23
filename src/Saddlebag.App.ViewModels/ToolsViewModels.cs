using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Saddlebag.Core.Bisect;
using Saddlebag.Core.Logs;
using Saddlebag.Core.Nexus;
using Saddlebag.Core.Watch;

namespace Saddlebag.App.ViewModels;

// ---------- crash bisect ----------

public sealed partial class BisectChoice(string id, string name, bool isHook) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public bool IsHook { get; } = isHook;
    /// <summary>On in every launch. Hooks are kept by default; untick to suspect one.</summary>
    [ObservableProperty] private bool _keep = isHook;
}

/// <summary>Find the crashing mod: launch, say whether it crashed, repeat. About log2(n) + 1 launches.</summary>
public sealed partial class BisectViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<BisectChoice> Choices { get; } = [];
    public ObservableCollection<string> Testing { get; } = [];
    public ObservableCollection<string> Kept { get; } = [];
    public ObservableCollection<string> CrashSet { get; } = [];
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string? _stepText;
    [ObservableProperty] private string? _culprit;
    [ObservableProperty] private string? _resultText;

    public override async Task RefreshAsync()
    {
        var d = await Host.Run(ws =>
        {
            var names = ws.Store.GetMods().ToDictionary(m => m.Id, m => m.Name);
            string N(string id) => names.GetValueOrDefault(id, id);
            var s = ws.LoadBisect();
            var choices = s is not null ? [] : ws.Store.GetProfileMods().Where(p => p.Enabled)
                .Select(p => new BisectChoice(p.ModId, N(p.ModId), ws.IsHookMod(p.ModId))).ToList();
            return (s, choices, Testing: s?.Testing.Select(N).ToList() ?? [], Kept: s?.Kept.Select(N).ToList() ?? [],
                Crash: s?.CrashSet.Select(N).ToList() ?? [], Culprit: s?.Culprit is { } c ? N(c) : null);
        });
        IsActive = d.s is not null;
        IsDone = d.s?.IsDone == true;
        Choices.Clear();
        foreach (var c in d.choices) Choices.Add(c);
        Fill(Testing, d.Testing);
        Fill(Kept, d.Kept);
        Fill(CrashSet, d.Crash);
        Culprit = d.Culprit;
        StepText = d.s switch
        {
            null => null,
            { Phase: BisectPhase.Found } s => $"Culprit found in {s.Launches} launch(es).",
            { Phase: BisectPhase.Interaction } => "No single mod crashes on its own; the crash needs two or more of the mods below together.",
            { Phase: BisectPhase.NotFound } => "None of the suspects crashed the game. The cause is probably a kept mod, the game itself, or a combination.",
            { Phase: BisectPhase.Confirm } => "Confirm launch: the last suspect on its own.",
            var s => $"Launch {s.Launches + 1}: {s.Candidates.Count} suspect(s) left, about {s.RemainingEstimate} launch(es) to go.",
        };
        ResultText = d.s is { IsDone: false } ? "Launch RDR2 in Story Mode and do what made it crash. Close the game, then say what happened." : null;
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private Task Act(Func<Core.Workspace, object> work, string busy) => Guard(async () =>
    {
        await Host.Run(ws => { work(ws); }, busy);
        await Shell.RefreshAllAsync();
    });

    [RelayCommand]
    private Task StartAsync() => Act(ws =>
    {
        var keep = Choices.Where(c => c.Keep && !c.IsHook).Select(c => c.Id).ToList();
        var suspect = Choices.Where(c => !c.Keep && c.IsHook).Select(c => c.Id).ToList();
        return ws.BisectStart(keep, suspect);
    }, "Deploying the first test set…");

    [RelayCommand]
    private Task CrashedAsync() => Act(ws => ws.BisectAnswer(crashed: true), "Deploying the next test set…");

    [RelayCommand]
    private Task RanFineAsync() => Act(ws => ws.BisectAnswer(crashed: false), "Deploying the next test set…");

    [RelayCommand]
    private Task UndoAsync() => Act(ws => ws.BisectUndo(), "Taking back the last answer…");

    [RelayCommand]
    private Task ResetAsync() => Act(ws => ws.BisectReset(), "Restoring your profile…");

    [RelayCommand]
    private Task ResetDisableCulpritAsync() => Act(ws => ws.BisectReset(disableCulprit: true), "Restoring your profile without the culprit…");
}

// ---------- logs ----------

public sealed record LogRow(LogFile File, string Owner, int Errors, int Warnings, string Mentions)
{
    public string Display => File.Display;
    public bool Archived => File.Archived;
    public string Size => File.Size < 1024 ? $"{File.Size} B" : File.Size < 1 << 20 ? $"{File.Size / 1024.0:F1} KB" : $"{File.Size / 1048576.0:F1} MB";
    public DateTime Modified => File.ModifiedUtc.ToLocalTime();
}

public sealed record LineRow(int? Number, string Text, LogLevel Level, string Mentions);

/// <summary>Hook and mod logs: which mod wrote them, errors, and a live view while the game runs.</summary>
public sealed partial class LogsViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<LogRow> Logs { get; } = [];
    public ObservableCollection<LineRow> Lines { get; } = [];
    [ObservableProperty] private LogRow? _selected;
    [ObservableProperty] private bool _errorsOnly;
    [ObservableProperty] private string? _filter;
    [ObservableProperty] private int _tail = 500;
    [ObservableProperty] private bool _isFollowing;
    private LogFollower? _follower;
    private Func<string, IReadOnlyList<string>>? _mentions;
    public const int MaxLines = 5000;

    /// <summary>The line load started by the last selection or filter change; tests (and the view) can await it.</summary>
    public Task LinesLoading { get; private set; } = Task.CompletedTask;

    partial void OnSelectedChanged(LogRow? value) { StopFollowing(); Reload(); }
    partial void OnErrorsOnlyChanged(bool value) => Reload();
    partial void OnFilterChanged(string? value) => Reload();
    private void Reload() => LinesLoading = Guard(LoadLinesAsync);

    public override async Task RefreshAsync()
    {
        var keep = Selected?.Display;
        var rows = await Host.Run(ws => ws.Logs.Discover().Select(l =>
        {
            var s = ws.Logs.Summarize(l);
            return new LogRow(l, l.Vanilla ? "the game" : l.OwnerName ?? "?", s.Errors, s.Warnings,
                string.Join(", ", s.ModMentions.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value})")));
        }).ToList());
        Logs.Clear();
        foreach (var r in rows) Logs.Add(r);
        if (keep is not null) Selected = Logs.FirstOrDefault(l => l.Display == keep);
    }

    private async Task LoadLinesAsync()
    {
        Lines.Clear();
        if (Selected is not { } log) return;
        var lines = await Host.Run(ws => ws.Logs.Read(log.File, Math.Clamp(Tail, 1, MaxLines), ErrorsOnly, string.IsNullOrWhiteSpace(Filter) ? null : Filter));
        foreach (var l in lines) Lines.Add(new LineRow(l.Number, l.Text, l.Level, string.Join(", ", l.MentionedMods)));
    }

    [RelayCommand]
    private Task ToggleFollowAsync() => Guard(async () =>
    {
        if (IsFollowing) { StopFollowing(); return; }
        if (Selected is not { Archived: false } log) { Shell.StatusText = "Pick a log in the game folder to follow."; return; }
        _mentions = await Host.Run(ws => ws.Logs.MentionMatcher());
        _follower = new LogFollower(log.File.FullPath);
        _follower.Poll(); // start at the current end
        IsFollowing = true;
    });

    public void StopFollowing()
    {
        _follower = null;
        IsFollowing = false;
    }

    /// <summary>Called by the view's timer while following. Reads only the file, so it doesn't need the workspace.</summary>
    public void Poll()
    {
        if (_follower is null) return;
        var chunk = _follower.Poll();
        if (chunk.Restarted) Lines.Add(new LineRow(null, "── log restarted ──", LogLevel.Info, ""));
        foreach (var text in chunk.Lines)
        {
            var level = LogService.Classify(text);
            if (ErrorsOnly && level != LogLevel.Error) continue;
            if (!string.IsNullOrWhiteSpace(Filter) && !text.Contains(Filter, StringComparison.OrdinalIgnoreCase)) continue;
            Lines.Add(new LineRow(null, text, level, string.Join(", ", _mentions?.Invoke(text) ?? [])));
        }
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }

    [RelayCommand]
    private Task ArchiveAsync() => Guard(async () =>
    {
        var moved = await Host.Run(ws => { ws.Prepare(); return ws.Logs.ArchiveModLogs(); });
        Shell.StatusText = moved.Count == 0 ? "No mod logs to archive." : $"Moved {moved.Count} log(s) out of the game folder.";
        await RefreshAsync();
    });
}

// ---------- Nexus updates ----------

/// <summary>Checks linked mods for newer files on Nexus. Read-only.</summary>
public sealed partial class UpdatesViewModel(ShellViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<UpdateResult> Results { get; } = [];
    [ObservableProperty] private string? _keyStatus;
    [ObservableProperty] private string? _lastRun;
    public bool CanStoreKey => NexusKeyStore.CanStore;

    public override async Task RefreshAsync()
    {
        var (key, source) = await Host.Run(ws => new NexusKeyStore(ws.Home).Load());
        KeyStatus = key is null
            ? "No API key. Copy your personal key from nexusmods.com (Site preferences → API Keys) and set it here."
            : $"API key set (from {source}).";
    }

    [RelayCommand]
    private Task CheckAsync() => Check(refresh: false);

    [RelayCommand]
    private Task CheckFreshAsync() => Check(refresh: true);

    private Task Check(bool refresh) => Guard(async () =>
    {
        var (results, client) = await Host.Run(ws =>
        {
            var (key, _) = new NexusKeyStore(ws.Home).Load();
            if (key is null) throw new InvalidOperationException("Set your Nexus API key first.");
            var c = new NexusClient(key, typeof(UpdatesViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", Shell.NexusHandler);
            return (new UpdateChecker(ws.Store, ws.Library, c).CheckAsync(refresh: refresh).GetAwaiter().GetResult(), c);
        }, "Asking Nexus…");
        Results.Clear();
        foreach (var r in results.OrderBy(r => r.Status)) Results.Add(r);
        var updates = results.Count(r => r.Status is UpdateStatus.UpdateAvailable or UpdateStatus.OldFile);
        LastRun = $"{DateTime.Now:HH:mm}: {updates} update(s). {client.RequestsMade} request(s) to Nexus; {client.DailyRemaining?.ToString() ?? "?"} left today. " +
                  "Download from each mod's files page; free accounts must use the website.";
    });

    [RelayCommand]
    private Task SetKeyAsync() => Guard(async () =>
    {
        if (await Dialogs.PromptAsync("Nexus API key", "Paste your personal API key. It's saved encrypted for your Windows account.", secret: true) is not { Length: > 0 } key) return;
        await Host.Run(ws => new NexusKeyStore(ws.Home).Save(key));
        await RefreshAsync();
    });

    [RelayCommand]
    private Task TestKeyAsync() => Guard(async () =>
    {
        var who = await Host.Run(ws =>
        {
            var (key, _) = new NexusKeyStore(ws.Home).Load();
            if (key is null) throw new InvalidOperationException("Set your Nexus API key first.");
            var c = new NexusClient(key, typeof(UpdatesViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", Shell.NexusHandler);
            var user = c.ValidateKeyAsync().GetAwaiter().GetResult();
            return $"Key works: signed in as {user.Name ?? "?"}{(user.IsPremium ? " (premium)" : "")}. {c.DailyRemaining?.ToString() ?? "?"} requests left today.";
        }, "Asking Nexus…");
        KeyStatus = who;
    });

    [RelayCommand]
    private Task ClearKeyAsync() => Guard(async () =>
    {
        await Host.Run(ws => new NexusKeyStore(ws.Home).Clear());
        await RefreshAsync();
    });
}

// ---------- build-folder watcher ----------

/// <summary>Redeploys linked build folders after every build. The view's timer calls <see cref="TickAsync"/>.</summary>
public sealed partial class WatchViewModel(ShellViewModel shell) : PageViewModel(shell), IDisposable
{
    public ObservableCollection<string> Events { get; } = [];
    public ObservableCollection<string> Targets { get; } = [];
    [ObservableProperty] private bool _isWatching;
    [ObservableProperty] private int _quietMs = 1500;
    private BuildWatcher? _watcher;
    private IDisposable? _fs;
    private readonly ConcurrentQueue<WatchEvent> _pending = new();
    private bool _ticking;

    [RelayCommand]
    private Task ToggleAsync() => Guard(async () =>
    {
        if (IsWatching) { Stop(); return; }
        var (watcher, fs) = await Host.Run(ws =>
        {
            var w = new BuildWatcher(ws, [], TimeSpan.FromMilliseconds(Math.Max(0, QuietMs)), _pending.Enqueue);
            return (w, w.StartWatching());
        });
        _watcher = watcher;
        _fs = fs;
        Targets.Clear();
        foreach (var (_, folder) in watcher.Targets) Targets.Add(folder);
        IsWatching = true;
        Add($"Watching {Targets.Count} folder(s). Build as usual; each build deploys once it's finished.");
        await TickAsync();
    });

    public async Task TickAsync()
    {
        if (_watcher is not { } w || _ticking) return;
        _ticking = true;
        try
        {
            await Host.Run(_ => w.Step());
            var deployed = false;
            while (_pending.TryDequeue(out var e))
            {
                Add(e.Message + (e.Paths is { Count: > 0 } p ? $" ({string.Join(", ", p.Take(5))})" : ""));
                deployed |= e.Kind == WatchEventKind.Deployed;
            }
            if (deployed) await Shell.RefreshAllAsync();
        }
        catch (Exception e) { Add("Error: " + e.Message); }
        finally { _ticking = false; }
    }

    private void Add(string text)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (Events.Count > 200) Events.RemoveAt(Events.Count - 1);
    }

    private void Stop()
    {
        _fs?.Dispose();
        _fs = null;
        _watcher = null;
        IsWatching = false;
        Add("Stopped watching.");
    }

    public void Dispose()
    {
        _fs?.Dispose();
        _fs = null;
        _watcher = null;
    }
}
