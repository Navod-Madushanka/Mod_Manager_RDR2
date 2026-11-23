using System.Windows;
using System.Windows.Threading;
using Saddlebag.App.ViewModels;

namespace Saddlebag.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MainWindow()
    {
        InitializeComponent();
        // Drives the live parts: log follow and the build-folder watcher.
        _timer.Tick += async (_, _) =>
        {
            if (DataContext is not ShellViewModel shell) return;
            shell.Logs.Poll();
            await shell.Watch.TickAsync();
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }
}
