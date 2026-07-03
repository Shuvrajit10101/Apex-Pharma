using System;
using System.Windows.Controls;
using ApexPharma.Desktop.ViewModels.Billing;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// The POS Billing view (plan.md §6.1, §9, §10) — the flagship screen, embedded in the
/// single-window shell. Product search/scan → add line (with FEFO batch + expiry + live GST) →
/// live subtotal/CGST/SGST/total, bill discount, payment mode, customer picker (+ quick-add,
/// required for Credit), Schedule-H doctor+Rx prompt, and Complete Sale showing the saved bill.
/// Its <see cref="ViewModels.Billing.BillingViewModel"/> is supplied as the DataContext by the
/// shell's DataTemplate; all money/stock logic lives in the services (plan.md §8).
/// <para>
/// The only code-behind here is view concern, not logic: after a successful barcode scan the
/// view-model raises <see cref="BillingViewModel.BarcodeAccepted"/> and we refocus/select the
/// barcode box so a keyboard-wedge scanner can stream consecutive scans without the mouse.
/// </para>
/// </summary>
public partial class BillingView : UserControl
{
    private BillingViewModel? _boundViewModel;

    public BillingView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.BarcodeAccepted -= OnBarcodeAccepted;
        }

        _boundViewModel = e.NewValue as BillingViewModel;
        if (_boundViewModel is not null)
        {
            _boundViewModel.BarcodeAccepted += OnBarcodeAccepted;
        }
    }

    /// <summary>Refocus the (now-cleared) barcode box so the next scan streams straight in.</summary>
    private void OnBarcodeAccepted(object? sender, EventArgs e)
    {
        BarcodeBox.Focus();
        BarcodeBox.SelectAll();
    }
}
