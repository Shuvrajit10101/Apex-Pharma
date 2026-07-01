using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// The read-only Inventory view (plan.md §6.1, §10), embedded in the single-window shell.
/// Renders the current-stock grid with near-expiry / expired / low-stock colour-coding;
/// its <see cref="ViewModels.Inventory.InventoryViewModel"/> is supplied as the DataContext
/// by the shell's DataTemplate and performs no mutations.
/// </summary>
public partial class InventoryView : UserControl
{
    public InventoryView() => InitializeComponent();
}
