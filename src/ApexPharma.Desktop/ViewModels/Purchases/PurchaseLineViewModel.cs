using System;
using System.Collections.Generic;
using ApexPharma.Application.Services;
using ApexPharma.Domain.Entities;

namespace ApexPharma.Desktop.ViewModels.Purchases;

/// <summary>
/// One editable line in the purchase-entry grid (plan.md §6.1, §10): product + batch no +
/// expiry + qty + purchase price + MRP + GST rate. Recomputes its own line taxable / GST /
/// total whenever an amount-affecting field changes so the form shows live CGST/SGST and a
/// running total. All money maths runs through <see cref="IGstService"/> — no tax rule is
/// duplicated in the UI (plan.md §8). Raising <see cref="LineChanged"/> lets the parent
/// roll up the invoice totals.
/// </summary>
public sealed class PurchaseLineViewModel : ViewModelBase
{
    private readonly IGstService _gst;

    private Product? _selectedProduct;
    private string _batchNo = string.Empty;
    private DateTime? _expiryDate;
    private decimal _qty;
    private decimal _purchasePrice;
    private decimal _mrp;
    private decimal _gstRate;

    public PurchaseLineViewModel(IGstService gst, IEnumerable<Product> products)
    {
        _gst = gst;
        Products = products;
    }

    /// <summary>Products offered in the line's picker (shared active-product list).</summary>
    public IEnumerable<Product> Products { get; }

    /// <summary>The GST slabs the picker offers (plan.md §6.1 India slabs).</summary>
    public decimal[] GstRates { get; } = { 0m, 5m, 12m, 18m, 28m };

    /// <summary>Raised whenever an amount-affecting field changes so the parent re-rolls totals.</summary>
    public event EventHandler? LineChanged;

    public Product? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (SetProperty(ref _selectedProduct, value))
            {
                // Default the line's GST rate to the product's configured rate for convenience.
                if (value is not null)
                {
                    GstRate = value.GstRate;
                }

                RaiseLineChanged();
            }
        }
    }

    public string BatchNo
    {
        get => _batchNo;
        set => SetProperty(ref _batchNo, value);
    }

    public DateTime? ExpiryDate
    {
        get => _expiryDate;
        set => SetProperty(ref _expiryDate, value);
    }

    public decimal Qty
    {
        get => _qty;
        set { if (SetProperty(ref _qty, value)) RaiseLineChanged(); }
    }

    public decimal PurchasePrice
    {
        get => _purchasePrice;
        set { if (SetProperty(ref _purchasePrice, value)) RaiseLineChanged(); }
    }

    public decimal Mrp
    {
        get => _mrp;
        set => SetProperty(ref _mrp, value);
    }

    public decimal GstRate
    {
        get => _gstRate;
        set { if (SetProperty(ref _gstRate, value)) RaiseLineChanged(); }
    }

    /// <summary>Line taxable base = purchase price × quantity.</summary>
    public decimal LineTaxable => PurchasePrice * Qty;

    /// <summary>Central GST for this line (half the total, intra-state).</summary>
    public decimal LineCgst => _gst.CalculateLineGst(LineTaxable, GstRate).Cgst;

    /// <summary>State GST for this line (half the total, intra-state).</summary>
    public decimal LineSgst => _gst.CalculateLineGst(LineTaxable, GstRate).Sgst;

    /// <summary>Line gross = taxable + total GST — what this line adds to the invoice total.</summary>
    public decimal LineTotal => _gst.CalculateLineGst(LineTaxable, GstRate).GrossAmount;

    private void RaiseLineChanged()
    {
        OnPropertyChanged(nameof(LineTaxable));
        OnPropertyChanged(nameof(LineCgst));
        OnPropertyChanged(nameof(LineSgst));
        OnPropertyChanged(nameof(LineTotal));
        LineChanged?.Invoke(this, EventArgs.Empty);
    }
}
