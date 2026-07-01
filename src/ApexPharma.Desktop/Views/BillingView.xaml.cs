using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// The POS Billing view (plan.md §6.1, §9, §10) — the flagship screen, embedded in the
/// single-window shell. Product search/scan → add line (with FEFO batch + expiry + live GST) →
/// live subtotal/CGST/SGST/total, bill discount, payment mode, customer picker (+ quick-add,
/// required for Credit), Schedule-H doctor+Rx prompt, and Complete Sale showing the saved bill.
/// Its <see cref="ViewModels.Billing.BillingViewModel"/> is supplied as the DataContext by the
/// shell's DataTemplate; all money/stock logic lives in the services (plan.md §8).
/// </summary>
public partial class BillingView : UserControl
{
    public BillingView() => InitializeComponent();
}
