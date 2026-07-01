using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// The Master Data view (plan.md §10), embedded in the single-window shell's content
/// region. Hosts a tab per catalog entity, each bound to its list view-model on the
/// shared <see cref="ViewModels.Masters.MastersViewModel"/> (supplied as the DataContext
/// via the shell's DataTemplate). The signed-in role is applied by the navigation
/// service when it activates the view-model, so every tab gates mutations through the
/// services (plan.md §4).
/// </summary>
public partial class MastersView : UserControl
{
    public MastersView() => InitializeComponent();
}
