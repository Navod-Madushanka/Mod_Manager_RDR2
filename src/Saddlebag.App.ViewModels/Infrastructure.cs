using CommunityToolkit.Mvvm.ComponentModel;
using Saddlebag.Core;

namespace Saddlebag.App.ViewModels;

/// <summary>Everything that needs a window: pickers, prompts, confirmations. WPF implements it; tests script it.</summary>
public interface IDialogs
{
    Task<bool> ConfirmAsync(string title, string message);
    /// <summary>Asks for one line of text. Null when cancelled. <paramref name="secret"/> hides the input (API keys).</summary>
    Task<string?> PromptAsync(string title, string message, string? initial = null, bool secret = false);
    Task<string?> PickFolderAsync(string title);
    Task<string[]?> PickFilesAsync(string title);
    void ShowError(string title, string message);
    /// <summary>Asks to pick one of several options (radio buttons). Returns the index, or null when cancelled.</summary>
    Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options, int recommended = 0);
    /// <summary>Opens a web page, a launcher link (steam://…) or a program.</summary>
    void Open(string uriOrPath);
    /// <summary>Restarts Saddlebag with administrator rights (Windows asks first). False if you said no.</summary>
    bool RestartAsAdministrator();
}

/// <summary>
/// Owns the open <see cref="Workspace"/> and runs every operation on it one at a time, off the UI thread.
/// The workspace's SQLite connection isn't safe for concurrent use, so nothing else may touch it directly.
/// </summary>
public sealed partial class WorkspaceHost : ObservableObject, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Workspace? _workspace;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;

    public bool IsOpen => _workspace is not null;
    public string? Home => _workspace?.Home;
    public string? GameDir => _workspace?.GameDir;

    /// <summary>Hands the host a workspace (from setup or on start). The host disposes it.</summary>
    public void Attach(Workspace ws)
    {
        _workspace?.Dispose();
        _workspace = ws;
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(Home));
        OnPropertyChanged(nameof(GameDir));
    }

    public async Task<T> Run<T>(Func<Workspace, T> work, string? busyText = null)
    {
        var ws = _workspace ?? throw new InvalidOperationException("Saddlebag isn't set up yet.");
        await _gate.WaitAsync();
        IsBusy = true;
        BusyText = busyText;
        try { return await Task.Run(() => work(ws)); }
        finally
        {
            IsBusy = false;
            BusyText = null;
            _gate.Release();
        }
    }

    public Task Run(Action<Workspace> work, string? busyText = null) => Run(ws => { work(ws); return true; }, busyText);

    public void Dispose()
    {
        _workspace?.Dispose();
        _workspace = null;
    }
}

/// <summary>A page of the main window. Errors from any command surface the same way, through the shell.</summary>
public abstract class PageViewModel(ShellViewModel shell) : ObservableObject
{
    protected ShellViewModel Shell { get; } = shell;
    protected WorkspaceHost Host => Shell.Host;
    protected IDialogs Dialogs => Shell.Dialogs;

    /// <summary>Reload what this page shows from the workspace.</summary>
    public virtual Task RefreshAsync() => Task.CompletedTask;

    private CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? _refresh;
    /// <summary>Every page's Refresh button.</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand RefreshCommand =>
        _refresh ??= new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Guard(RefreshAsync));

    protected Task Guard(Func<Task> work) => Shell.GuardAsync(work);
}
