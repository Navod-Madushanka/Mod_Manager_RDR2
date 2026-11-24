using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Saddlebag.Core;
using Saddlebag.Core.Safety;

namespace Saddlebag.App.ViewModels;

/// <summary>
/// The main window: setup when there's no Saddlebag folder yet, otherwise a header (mode, profile, banners) over the
/// pages. Every page talks to the workspace through <see cref="Host"/>.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    public WorkspaceHost Host { get; } = new();
    public IDialogs Dialogs { get; }
    internal IGameGuard? Guard { get; }
    /// <summary>Records the Saddlebag folder for next start. Replaced in tests so they don't touch AppData.</summary>
    public Action<string> RememberHome { get; init; } = HomeLocator.Remember;
    /// <summary>Whether Saddlebag may write to the game folder. A seam because tests run with rights a normal user lacks.</summary>
    public Func<string, bool> CanWriteGame { get; init; } = Core.Setup.GameLocator.CanWrite;
    /// <summary>Where setup looks for the game. Replaced in tests with a fake Steam/Epic/Rockstar layout.</summary>
    public Core.Setup.IInstallProbe? InstallProbe { get; init; }
    /// <summary>Test seam: a stand-in Documents folder for the game's settings files.</summary>
    public string? DocumentsDir { get; init; }
    /// <summary>Test seam for the Nexus client's HTTP layer.</summary>
    public HttpMessageHandler? NexusHandler { get; init; }

    public SetupWizardViewModel Wizard { get; }
    public HomeViewModel Home { get; }
    public ModeViewModel Mode { get; }
    public ModsViewModel Mods { get; }
    public PlanViewModel Plan { get; }
    public ProfilesViewModel Profiles { get; }
    public BisectViewModel Bisect { get; }
    public LogsViewModel Logs { get; }
    public UpdatesViewModel Updates { get; }
    public WatchViewModel Watch { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private bool _needsSetup;
    /// <summary>Shows every page (profiles, routing, bisect, logs…). Off by default: the Home page covers everyday use.</summary>
    [ObservableProperty] private bool _advancedMode;
    private bool _loadingPrefs;

    /// <summary>The last save of the Advanced switch; awaitable so nothing races it.</summary>
    public Task PrefsSaving { get; private set; } = Task.CompletedTask;

    partial void OnAdvancedModeChanged(bool value)
    {
        if (!_loadingPrefs && Host.IsOpen) PrefsSaving = GuardAsync(() => Host.Run(ws => ws.Store.SetSetting("gui_advanced", value ? "1" : "0")));
    }
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string _modeText = "not set";
    [ObservableProperty] private string? _profileName;
    [ObservableProperty] private string? _gameUpdatedBanner;
    [ObservableProperty] private string? _bisectBanner;
    [ObservableProperty] private string? _baselineBanner;
    [ObservableProperty] private int _pendingChanges;

    public ShellViewModel(IDialogs dialogs, IGameGuard? guard = null)
    {
        Dialogs = dialogs;
        Guard = guard;
        Wizard = new SetupWizardViewModel(this);
        Home = new HomeViewModel(this);
        Mode = new ModeViewModel(this);
        Mods = new ModsViewModel(this);
        Plan = new PlanViewModel(this);
        Profiles = new ProfilesViewModel(this);
        Bisect = new BisectViewModel(this);
        Logs = new LogsViewModel(this);
        Updates = new UpdatesViewModel(this);
        Watch = new WatchViewModel(this);
        Settings = new SettingsViewModel(this);
    }

    private IEnumerable<PageViewModel> Pages => [Home, Mode, Mods, Plan, Profiles, Bisect, Logs, Updates, Watch, Settings];

    /// <summary>Opens the Saddlebag folder (explicit, $SADDLEBAG_HOME, or the last one set up), or shows setup.</summary>
    public async Task StartAsync(string? explicitHome = null)
    {
        var home = HomeLocator.TryResolve(explicitHome);
        if (home is null || !File.Exists(Workspace.DbPath(home)) || !await Task.Run(() => SetupFinished(home)))
        {
            NeedsSetup = true;
            if (home is not null && File.Exists(Workspace.DbPath(home))) Wizard.FixedHome = home;
            await Wizard.FindAsync();
            return;
        }
        await GuardAsync(async () =>
        {
            Host.Attach(await Task.Run(() => Workspace.Open(home, Guard)));
            NeedsSetup = false;
            var recovered = await Host.Run(ws => ws.Store.GetPendingOps().Count > 0 ? ws.Prepare().Count : 0, "Finishing an interrupted operation…");
            if (recovered > 0) StatusText = "Finished an operation that was interrupted last time.";
        });
        await RefreshAllAsync();
    }

    /// <summary>Setup is finished once a baseline exists; until then, starting Saddlebag resumes the wizard.</summary>
    private static bool SetupFinished(string home)
    {
        using var store = new Core.State.StateStore(Workspace.DbPath(home));
        return store.BaselineCount() > 0;
    }

    /// <summary>Called by the wizard once the workspace exists, before setup is finished.</summary>
    internal async Task AttachAsync(Workspace ws, bool refresh = true)
    {
        Host.Attach(ws);
        if (refresh) await RefreshAllAsync();
    }

    internal async Task FinishSetupAsync()
    {
        NeedsSetup = false;
        await RefreshAllAsync();
    }

    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        if (!Host.IsOpen) return;
        _loadingPrefs = true;
        try { AdvancedMode = await Host.Run(ws => ws.Store.GetSetting("gui_advanced") == "1"); }
        finally { _loadingPrefs = false; }
        await GuardAsync(async () =>
        {
            var h = await Host.Run(ws =>
            {
                var plan = ws.Deployer.Plan();
                var bisect = ws.LoadBisect();
                return new
                {
                    Mode = ws.Store.GetSetting("mode") ?? "not set",
                    Profile = ws.Store.FindProfile(ws.Store.ActiveProfile)?.Name ?? ws.Store.ActiveProfile,
                    Baseline = ws.Store.BaselineCount(),
                    Updated = ws.Store.BaselineCount() > 0 && ws.Baseline.GameUpdatedSinceBaseline(),
                    Bisect = bisect is null ? null : bisect.IsDone ? "Crash bisect finished: see the Bisect page, then reset." : $"Crash bisect running: {bisect.Candidates.Count} suspect(s) left.",
                    Pending = ws.Store.GetSetting("mode") == "online" ? 0 : plan.Steps.Count,
                };
            });
            ModeText = h.Mode;
            ProfileName = h.Profile;
            BaselineBanner = h.Baseline == 0 ? "No baseline yet: Online mode can't verify anything. Capture one on the Settings page from a clean install." : null;
            GameUpdatedBanner = h.Updated ? "RDR2.exe changed since the baseline: the game updated. Hook-based mods may crash. Go Online, verify files in your launcher, then re-capture the baseline." : null;
            BisectBanner = h.Bisect;
            PendingChanges = h.Pending;
        });
        foreach (var p in Pages) await GuardAsync(p.RefreshAsync);
    }

    /// <summary>Runs a command and turns any failure into one visible error, instead of a crash.</summary>
    public async Task GuardAsync(Func<Task> work)
    {
        try
        {
            LastError = null;
            await work();
        }
        catch (Exception e)
        {
            var msg = e is AggregateException { InnerException: { } inner } ? inner.Message : e.Message;
            LastError = msg;
            Dialogs.ShowError(e is GameRunningException ? "Close the game first" : "Saddlebag", msg);
        }
    }

    public void Dispose()
    {
        Watch.Dispose();
        Logs.StopFollowing();
        Host.Dispose();
    }
}
