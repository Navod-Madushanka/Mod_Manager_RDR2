using System.Windows.Controls;
using System.Windows.Input;
using Saddlebag.Core.Nexus;

namespace Saddlebag.App.Views;

public partial class UpdatesPage : UserControl
{
    public UpdatesPage() => InitializeComponent();

    /// <summary>Double-click a mod to open its Nexus files page in the browser.</summary>
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is UpdateResult { Url: { } url }) WpfDialogs.OpenUrl(url);
    }
}
