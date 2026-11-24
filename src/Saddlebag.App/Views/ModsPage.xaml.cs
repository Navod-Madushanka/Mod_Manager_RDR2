using System.Windows;
using System.Windows.Controls;
using Saddlebag.App.ViewModels;

namespace Saddlebag.App.Views;

public partial class ModsPage : UserControl
{
    public ModsPage() => InitializeComponent();

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropped archives and folders go into the library; nothing touches the game folder until Deploy.</summary>
    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true; // don't also let the window import it
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && DataContext is ModsViewModel vm)
            await vm.ImportAsync(paths);
    }
}
