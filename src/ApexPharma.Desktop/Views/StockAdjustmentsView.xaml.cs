using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// Code-behind for <see cref="StockAdjustmentsView"/> (plan.md §10). View-only: all behaviour lives
/// in the bound <see cref="ViewModels.StockAdjustments.StockAdjustmentsViewModel"/> (MVVM — plan.md §8).
/// </summary>
public partial class StockAdjustmentsView : UserControl
{
    public StockAdjustmentsView() => InitializeComponent();
}
