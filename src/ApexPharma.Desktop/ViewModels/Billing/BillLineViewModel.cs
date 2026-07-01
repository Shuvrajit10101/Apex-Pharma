using System;
using System.Collections.Generic;
using System.Linq;
using ApexPharma.Application.Services;
using ApexPharma.Domain.Entities;

namespace ApexPharma.Desktop.ViewModels.Billing;

/// <summary>
/// One product line in the POS bill grid (plan.md §6.1, §10). Shows the FEFO batch + expiry
/// that would be dispensed, the batch sale rate, the quantity, and this line's live CGST/SGST
/// and total — all computed for on-screen feedback only. The authoritative FEFO dispense, GST,
/// and stock decrement happen server-side in <see cref="IBillingService"/>; this view-model
/// never mutates stock (plan.md §8). Raising <see cref="LineChanged"/> lets the parent re-roll
/// the bill totals.
/// </summary>
public sealed class BillLineViewModel : ViewModelBase
{
    private readonly IGstService _gst;

    private Product? _selectedProduct;
    private decimal _qty = 1m;
    private decimal _lineDiscount;

    // FEFO preview (earliest-expiry non-expired lot) of the selected product, for display.
    private string _batchDisplay = string.Empty;
    private DateTime? _expiryPreview;
    private decimal _rate;
    private decimal _availableQty;

    public BillLineViewModel(IGstService gst, IEnumerable<Product> products)
    {
        _gst = gst;
        Products = products;
    }

    /// <summary>Active products offered in the line's picker (shared list).</summary>
    public IEnumerable<Product> Products { get; }

    /// <summary>Raised whenever an amount-affecting field changes so the parent re-rolls totals.</summary>
    public event EventHandler? LineChanged;

    public Product? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (SetProperty(ref _selectedProduct, value))
            {
                OnPropertyChanged(nameof(IsScheduled));
                OnPropertyChanged(nameof(GstRate));
                RaiseLineChanged();
            }
        }
    }

    public decimal Qty
    {
        get => _qty;
        set { if (SetProperty(ref _qty, value)) RaiseLineChanged(); }
    }

    public decimal LineDiscount
    {
        get => _lineDiscount;
        set { if (SetProperty(ref _lineDiscount, value)) RaiseLineChanged(); }
    }

    /// <summary>The FEFO batch number + expiry shown for the picked product (read-only preview).</summary>
    public string BatchDisplay
    {
        get => _batchDisplay;
        private set => SetProperty(ref _batchDisplay, value);
    }

    public DateTime? ExpiryPreview
    {
        get => _expiryPreview;
        private set => SetProperty(ref _expiryPreview, value);
    }

    /// <summary>The dispensing batch's sale price per unit (preview; server uses the live batch).</summary>
    public decimal Rate
    {
        get => _rate;
        private set { if (SetProperty(ref _rate, value)) RaiseLineChanged(); }
    }

    /// <summary>Total non-expired on-hand across all lots for the picked product (preview).</summary>
    public decimal AvailableQty
    {
        get => _availableQty;
        private set => SetProperty(ref _availableQty, value);
    }

    /// <summary>The product's GST rate percent (drives the shown CGST/SGST).</summary>
    public decimal GstRate => SelectedProduct?.GstRate ?? 0m;

    /// <summary>True when the picked product is a Schedule H/H1 drug (drives the Rx prompt).</summary>
    public bool IsScheduled =>
        SelectedProduct?.Schedule is Domain.Enums.DrugSchedule.H or Domain.Enums.DrugSchedule.H1;

    /// <summary>Line taxable base after the line discount (rate × qty − discount, floored at 0).</summary>
    public decimal LineTaxable => Math.Max(0m, Rate * Qty - LineDiscount);

    public decimal LineCgst => _gst.CalculateLineGst(LineTaxable, GstRate).Cgst;
    public decimal LineSgst => _gst.CalculateLineGst(LineTaxable, GstRate).Sgst;

    /// <summary>Line gross = net taxable + its GST — what this line adds to the bill total.</summary>
    public decimal LineTotal => _gst.CalculateLineGst(LineTaxable, GstRate).GrossAmount;

    /// <summary>
    /// Refreshes the FEFO preview (batch / expiry / rate / available qty) from the given
    /// non-expired lots, ordered earliest-expiry first. Called by the parent after loading
    /// stock so the grid shows which lot would be dispensed — display only.
    /// </summary>
    public void SetFefoPreview(IReadOnlyList<Batch> nonExpiredLots)
    {
        Batch? earliest = nonExpiredLots
            .Where(b => b.QtyOnHand > 0)
            .OrderBy(b => b.ExpiryDate)
            .ThenBy(b => b.BatchId)
            .FirstOrDefault();

        AvailableQty = nonExpiredLots.Where(b => b.QtyOnHand > 0).Sum(b => b.QtyOnHand);

        if (earliest is null)
        {
            BatchDisplay = "— no stock —";
            ExpiryPreview = null;
            Rate = 0m;
            return;
        }

        BatchDisplay = earliest.BatchNo;
        ExpiryPreview = earliest.ExpiryDate;
        Rate = earliest.SalePrice;
    }

    private void RaiseLineChanged()
    {
        OnPropertyChanged(nameof(LineTaxable));
        OnPropertyChanged(nameof(LineCgst));
        OnPropertyChanged(nameof(LineSgst));
        OnPropertyChanged(nameof(LineTotal));
        LineChanged?.Invoke(this, EventArgs.Empty);
    }
}
