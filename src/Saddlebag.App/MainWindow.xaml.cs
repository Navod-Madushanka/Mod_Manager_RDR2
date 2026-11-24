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

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop) && DataContext is ShellViewModel { NeedsSetup: false };
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropping mod downloads anywhere adds them the friendly way (plain questions, applied straight away).</summary>
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && DataContext is ShellViewModel { NeedsSetup: false } shell)
            await shell.Home.AddAsync(paths);
    }
}
