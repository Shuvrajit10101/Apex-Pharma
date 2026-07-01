using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// The Purchase / GRN entry view (plan.md §6.1, §9, §10), embedded in the single-window
/// shell. Hosts the supplier picker, an editable line grid (product + batch + expiry + qty
/// + purchase price + MRP + GST), live invoice totals, a recent-purchases list, and a
/// purchase-return action. Its <see cref="ViewModels.Purchases.PurchaseViewModel"/> is
/// supplied as the DataContext by the shell's DataTemplate and gates saves on the acting
/// role's <c>DoPurchases</c> permission (plan.md §4).
/// </summary>
public partial class PurchaseView : UserControl
{
    public PurchaseView() => InitializeComponent();
}
