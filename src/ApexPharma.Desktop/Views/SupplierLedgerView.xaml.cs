using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// Code-behind for <see cref="SupplierLedgerView"/> (plan.md §10). View-only: all behaviour lives
/// in the bound <see cref="ViewModels.Ledger.SupplierLedgerViewModel"/> (MVVM — plan.md §8).
/// </summary>
public partial class SupplierLedgerView : UserControl
{
    public SupplierLedgerView() => InitializeComponent();
}
