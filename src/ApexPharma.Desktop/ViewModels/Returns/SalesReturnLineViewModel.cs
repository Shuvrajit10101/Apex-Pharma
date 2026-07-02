using ApexPharma.Application.Services;

namespace ApexPharma.Desktop.ViewModels.Returns;

/// <summary>
/// One sold line in the sales-return picker (plan.md §6.1, §10). Shows the product, batch,
/// sold / already-returned / remaining quantities and lets the operator enter how many units
/// to return on this line. The parent view-model validates and submits; no money/stock rule
/// lives here (plan.md §8).
/// </summary>
public sealed class SalesReturnLineViewModel : ViewModelBase
{
    private decimal _returnQty;

    public SalesReturnLineViewModel(SaleReturnableLine line)
    {
        SaleItemId = line.SaleItemId;
        ProductName = line.ProductName;
        BatchNo = line.BatchNo;
        SoldQty = line.SoldQty;
        ReturnedQty = line.ReturnedQty;
        RemainingQty = line.RemainingQty;
        Rate = line.Rate;
    }

    public int SaleItemId { get; }
    public string ProductName { get; }
    public string BatchNo { get; }
    public decimal SoldQty { get; }
    public decimal ReturnedQty { get; }

    /// <summary>Units still returnable on this line (sold − already returned).</summary>
    public decimal RemainingQty { get; }

    public decimal Rate { get; }

    /// <summary>Units the operator wants to return on this line (0 = leave alone).</summary>
    public decimal ReturnQty
    {
        get => _returnQty;
        set => SetProperty(ref _returnQty, value);
    }
}
