using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Saddlebag.App.ViewModels;

namespace Saddlebag.App;

/// <summary>Real dialogs for the view models: Windows file pickers, message boxes, and a small prompt window.</summary>
public sealed class WpfDialogs(Window owner) : IDialogs
{
    public Task<bool> ConfirmAsync(string title, string message) =>
        Task.FromResult(MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK);

    public Task<string?> PromptAsync(string title, string message, string? initial = null, bool secret = false) =>
        Task.FromResult(PromptWindow.Ask(owner, title, message, initial, secret));

    public Task<string?> PickFolderAsync(string title)
    {
        var d = new OpenFolderDialog { Title = title };
        return Task.FromResult(d.ShowDialog(owner) == true ? d.FolderName : null);
    }

    public Task<string[]?> PickFilesAsync(string title)
    {
        var d = new OpenFileDialog
        {
            Title = title,
            Multiselect = true,
            Filter = "Mod archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|All files (*.*)|*.*",
        };
        return Task.FromResult(d.ShowDialog(owner) == true ? d.FileNames : null);
    }

    public void ShowError(string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options, int recommended = 0) =>
        Task.FromResult(ChooseWindow.Ask(owner, title, message, options, recommended));

    public void Open(string uriOrPath)
    {
        try { OpenUrl(uriOrPath); }
        catch (Exception e) { ShowError("Couldn't open it", $"{uriOrPath}\n\n{e.Message}"); }
    }

    /// <summary>Starts a second copy elevated (Windows shows its permission prompt), then closes this one.</summary>
    public bool RestartAsAdministrator()
    {
        try
        {
            var args = Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = string.Join(' ', args) });
            Application.Current.Shutdown();
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // you clicked No
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

/// <summary>A question with a few answers as radio buttons; the recommended one is preselected.</summary>
public sealed class ChooseWindow : Window
{
    private readonly List<RadioButton> _options = [];

    private ChooseWindow(string title, string message, IReadOnlyList<string> options, int recommended)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 480 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), FontSize = 13 });
        for (var i = 0; i < options.Count; i++)
        {
            var rb = new RadioButton { Content = options[i], IsChecked = i == recommended, Margin = new Thickness(0, 4, 0, 4), FontSize = 13, GroupName = "choice" };
            _options.Add(rb);
            panel.Children.Add(rb);
        }
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 5, 16, 5) };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public static int? Ask(Window owner, string title, string message, IReadOnlyList<string> options, int recommended)
    {
        var w = new ChooseWindow(title, message, options, recommended) { Owner = owner };
        if (w.ShowDialog() != true) return null;
        var picked = w._options.FindIndex(o => o.IsChecked == true);
        return picked >= 0 ? picked : null;
    }
}

/// <summary>Visible when an enum value's name equals the parameter (one wizard step at a time).</summary>
public sealed class StepToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        string.Equals(value?.ToString(), p as string, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>One-line text prompt; a PasswordBox when the value is secret (API keys).</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _text = new() { Margin = new Thickness(0, 8, 0, 12), MinWidth = 380 };
    private readonly PasswordBox _secret = new() { Margin = new Thickness(0, 8, 0, 12), MinWidth = 380 };
    private readonly bool _isSecret;

    private PromptWindow(string title, string message, string? initial, bool secret)
    {
        _isSecret = secret;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        ok.Click += (_, _) => DialogResult = true;
        _text.Text = initial ?? "";
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        panel.Children.Add(secret ? _secret : _text);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { if (secret) _secret.Focus(); else { _text.Focus(); _text.SelectAll(); } };
    }

    public static string? Ask(Window owner, string title, string message, string? initial, bool secret)
    {
        var w = new PromptWindow(title, message, initial, secret) { Owner = owner };
        return w.ShowDialog() == true ? (w._isSecret ? w._secret.Password : w._text.Text) : null;
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Collapsed for null or empty text; with parameter "invert", visible only when null.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var empty = value is null || value is string { Length: 0 };
        if (p as string == "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when a count is above zero; "invert" for the empty state.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var any = value is int n && n > 0;
        if (p as string == "invert") any = !any;
        return any ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>One colour language for verify status, check severity, log level, confidence and update status.</summary>
public sealed class LevelBrushConverter : IValueConverter
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x1A, 0x7F, 0x37));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0x9A, 0x67, 0x00));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E));
    private static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(0x59, 0x63, 0x6E));
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28));

    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        Core.Baseline.VerifyStatus.Green or Core.Routing.Confidence.High or Core.Nexus.UpdateStatus.UpToDate => Good,
        Core.Baseline.VerifyStatus.Amber or Core.Deploy.Severity.Warn or Core.Logs.LogLevel.Warn or Core.Routing.Confidence.Medium
            or Core.Nexus.UpdateStatus.UpdateAvailable or Core.Nexus.UpdateStatus.OldFile => Warn,
        Core.Baseline.VerifyStatus.Red or Core.Deploy.Severity.Block or Core.Logs.LogLevel.Error or Core.Routing.Confidence.Low
            or Core.Nexus.UpdateStatus.Error or Core.Nexus.UpdateStatus.Unavailable => Bad,
        "red" or "bad" => Bad,
        "amber" or "warn" => Warn,
        "good" => Good,
        "info" or "neutral" => Neutral,
        Core.Logs.LogLevel.Info => Normal,
        _ => Neutral,
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
