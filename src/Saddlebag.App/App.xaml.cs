using System.Windows;
using Saddlebag.App.ViewModels;

namespace Saddlebag.App;

public partial class App : Application
{
    private ShellViewModel? _shell;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        _shell = new ShellViewModel(new WpfDialogs(window));
        window.DataContext = _shell;
        MainWindow = window;
        window.Show();
        // `Saddlebag.exe --home D:\Saddlebag` opens a specific folder; otherwise the one the CLI or GUI set up last.
        var home = e.Args.Length >= 2 && e.Args[0] == "--home" ? e.Args[1] : null;
        await _shell.StartAsync(home);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.Dispose();
        base.OnExit(e);
    }
}
