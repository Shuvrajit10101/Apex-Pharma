using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// Code-behind for <see cref="CustomerLedgerView"/> (plan.md §10). View-only: all behaviour lives
/// in the bound <see cref="ViewModels.Ledger.CustomerLedgerViewModel"/> (MVVM — plan.md §8).
/// </summary>
public partial class CustomerLedgerView : UserControl
{
    public CustomerLedgerView() => InitializeComponent();
}
