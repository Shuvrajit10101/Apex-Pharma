using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// Renders the reusable "coming in a later phase" panel for not-yet-built modules
/// (plan.md §10). Bound to a <see cref="ViewModels.PlaceholderViewModel"/> supplied as
/// the DataContext by the shell's DataTemplate, so a single view serves every stub module.
/// </summary>
public partial class PlaceholderView : UserControl
{
    public PlaceholderView() => InitializeComponent();
}
