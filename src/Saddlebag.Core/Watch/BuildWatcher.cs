using Saddlebag.Core.Deploy;
using Saddlebag.Core.Safety;
using Saddlebag.Core.Util;

namespace Saddlebag.Core.Watch;

public enum WatchEventKind { Started, Changed, WaitingForGame, Deployed, UpToDate, Blocked, Retrying, Error, Stopped }

public sealed record WatchEvent(WatchEventKind Kind, string Message, ApplyResult? Result = null, IReadOnlyList<string>? Paths = null);

public enum WatchAction { None, Deploy, WaitForGame }

/// <summary>
/// The timing logic of the watcher, with no I/O: changes arrive in bursts while a build runs, so deploy once the
/// folder has been quiet for a moment, and hold off while the game is open.
/// </summary>
public sealed class WatchState(TimeSpan quiet)
{
    public TimeSpan Quiet { get; } = quiet;
    public bool Pending { get; private set; }
    public bool WaitingForGame { get; private set; }
    private DateTime _lastChange;
    private readonly HashSet<string> _changed = new(RelPath.Comparer);

    public IReadOnlyCollection<string> ChangedPaths => _changed;

    public void Change(DateTime now, string? path = null)
    {
        Pending = true;
        _lastChange = now;
        if (path is not null) _changed.Add(path);
    }

    /// <summary>Decides what to do now. WaitForGame is returned once per wait, so callers can report it once.</summary>
    public WatchAction Tick(DateTime now, bool gameRunning)
    {
        if (!Pending || now - _lastChange < Quiet) return WatchAction.None;
        if (gameRunning)
        {
            if (WaitingForGame) return WatchAction.None;
            WaitingForGame = true;
            return WatchAction.WaitForGame;
        }
        return WatchAction.Deploy;
    }

    /// <summary>Deploy finished (or was blocked until the next change).</summary>
    public void Done()
    {
        Pending = false;
        WaitingForGame = false;
        _changed.Clear();
    }

    /// <summary>Deploy hit a file the build still holds: try again after another quiet period.</summary>
    public void Retry(DateTime now)
    {
        _lastChange = now;
        WaitingForGame = false;
    }
}

/// <summary>
/// Watches the folders of linked (dev) mods and redeploys when a build changes them. Deploys are ordinary journaled
/// diffs, so only the files the build changed move, and the running-game guard still applies.
/// </summary>
public sealed class BuildWatcher
{
    private readonly Workspace _ws;
    private readonly IReadOnlyList<(string ModId, string Folder)> _targets;
    private readonly WatchState _state;
    private readonly Action<WatchEvent> _report;
    private readonly Lock _gate = new();
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public IReadOnlyList<(string ModId, string Folder)> Targets => _targets;

    /// <param name="modIds">Linked mods to watch; empty means every enabled linked mod in the active profile.</param>
    public BuildWatcher(Workspace ws, IEnumerable<string> modIds, TimeSpan quiet, Action<WatchEvent> report)
    {
        _ws = ws;
        _state = new WatchState(quiet);
        _report = report;

        if (ws.Store.GetSetting("mode") == "online")
            throw new InvalidOperationException("You're in Online mode. Run `saddlebag story` first; the watcher would put mods back in.");
        ws.ThrowIfBisecting();

        var enabled = ws.Store.GetProfileMods().Where(p => p.Enabled).Select(p => p.ModId).ToHashSet();
        var requested = modIds.Select(m => ws.Library.Require(m)).ToList();
        var mods = requested.Count > 0 ? requested : ws.Store.GetMods().Where(m => m.IsLinked && enabled.Contains(m.Id)).ToList();
        foreach (var m in mods)
        {
            if (!m.IsLinked) throw new InvalidOperationException($"{m.Name} isn't a linked folder. Register your build output with `saddlebag link <folder>`.");
            if (!enabled.Contains(m.Id)) throw new InvalidOperationException($"{m.Name} isn't enabled in the active profile.");
        }
        if (mods.Count == 0) throw new InvalidOperationException("No linked mods to watch. Register your build output with `saddlebag link <folder> --name <name>`.");
        _targets = mods.Select(m => (m.Id, m.LinkedPath!)).ToList();
    }

    /// <summary>Records a change. Called by the file system watcher, or directly by tests.</summary>
    public void NotifyChange(string? fullPath = null)
    {
        lock (_gate)
        {
            var rel = fullPath is null ? null : _targets.Select(t => t.Folder).Where(f => fullPath.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileName(f) + "/" + RelPath.From(f, fullPath)).FirstOrDefault();
            _state.Change(DateTime.UtcNow, rel);
        }
    }

    /// <summary>Runs until cancelled: an initial sync, then a redeploy after each build.</summary>
    public void Run(CancellationToken ct)
    {
        using var watchers = StartWatching();
        try
        {
            _report(new(WatchEventKind.Started, $"Watching {string.Join(", ", _targets.Select(t => t.Folder))}"));
            while (!ct.IsCancellationRequested)
            {
                Step();
                ct.WaitHandle.WaitOne(PollInterval);
            }
        }
        finally
        {
            _report(new(WatchEventKind.Stopped, "Stopped watching."));
        }
    }

    /// <summary>
    /// Starts the file system watchers and queues an initial sync, without a loop: for hosts (like the GUI) that call
    /// <see cref="Step"/> on their own timer. Dispose the result to stop.
    /// </summary>
    public IDisposable StartWatching()
    {
        var watchers = _targets.Select(t => CreateWatcher(t.Folder)).ToList();
        lock (_gate) _state.Change(DateTime.UtcNow - _state.Quiet);
        return new Disposer(watchers);
    }

    private sealed class Disposer(List<FileSystemWatcher> watchers) : IDisposable
    {
        public void Dispose() { foreach (var w in watchers) w.Dispose(); }
    }

    /// <summary>One pass of the loop: check timing and the game, deploy if it's time.</summary>
    public void Step()
    {
        WatchAction action;
        IReadOnlyList<string> paths;
        lock (_gate)
        {
            var running = _state.Pending ? _ws.Guard.RunningBlockers() : [];
            action = _state.Tick(DateTime.UtcNow, running.Count > 0);
            paths = _state.ChangedPaths.ToList();
            if (action == WatchAction.WaitForGame)
            {
                _report(new(WatchEventKind.WaitingForGame, $"Build changed, but {string.Join(", ", running)} is open. Deploying when it closes."));
                return;
            }
            if (action != WatchAction.Deploy) return;
        }

        if (paths.Count > 0) _report(new(WatchEventKind.Changed, $"{paths.Count} file(s) changed", Paths: paths));
        try
        {
            var r = _ws.Deploy();
            if (r.Skipped > 0)
            {
                // Some files changed mid-deploy: the build is still writing. Go again once it's quiet.
                lock (_gate) { _state.Change(DateTime.UtcNow); }
                _report(new(WatchEventKind.Retrying, $"Placed {r.Placed}; {r.Skipped} file(s) changed while deploying. Retrying once the build is quiet.", r));
                return;
            }
            lock (_gate) _state.Done();
            _report(r.Placed + r.Removed > 0
                ? new(WatchEventKind.Deployed, $"Deployed: placed {r.Placed}, removed {r.Removed} file(s).", r)
                : new(WatchEventKind.UpToDate, "Game folder already matches the build.", r));
        }
        catch (GameRunningException g)
        {
            lock (_gate) _state.Retry(DateTime.UtcNow);
            _report(new(WatchEventKind.WaitingForGame, g.Message));
        }
        catch (IOException e)
        {
            // The build is still writing or holding a file. Try again after another quiet period.
            lock (_gate) _state.Retry(DateTime.UtcNow);
            _report(new(WatchEventKind.Retrying, $"A file is still in use ({e.Message}). Retrying."));
        }
        catch (DeployException d)
        {
            lock (_gate) _state.Done(); // can't deploy as-is; wait for the next build
            _report(new(WatchEventKind.Blocked, d.Message));
        }
        catch (InvalidOperationException e)
        {
            lock (_gate) _state.Done();
            _report(new(WatchEventKind.Error, e.Message));
        }
    }

    private FileSystemWatcher CreateWatcher(string folder)
    {
        var w = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        w.Changed += (_, e) => NotifyChange(e.FullPath);
        w.Created += (_, e) => NotifyChange(e.FullPath);
        w.Deleted += (_, e) => NotifyChange(e.FullPath);
        w.Renamed += (_, e) => NotifyChange(e.FullPath);
        w.Error += (_, _) => NotifyChange(); // buffer overflow: we missed events, so just resync
        w.EnableRaisingEvents = true;
        return w;
    }
}
