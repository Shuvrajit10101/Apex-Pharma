using ApexPharma.Application.Services;

namespace ApexPharma.Desktop.ViewModels.Returns;

/// <summary>
/// One purchased line in the purchase-return picker (plan.md §6.1, §10). Shows the product,
/// batch, purchased / already-returned / remaining quantities and lets the operator enter how
/// many units to send back. Remaining is the min of (purchased − returned) and the batch's
/// on-hand, so the UI never offers to return more than exists. The parent submits; no
/// stock rule lives here (plan.md §8).
/// </summary>
public sealed class PurchaseReturnLineViewModel : ViewModelBase
{
    private decimal _returnQty;

    public PurchaseReturnLineViewModel(PurchaseReturnableLine line)
    {
        PurchaseItemId = line.PurchaseItemId;
        ProductName = line.ProductName;
        BatchNo = line.BatchNo;
        PurchasedQty = line.PurchasedQty;
        ReturnedQty = line.ReturnedQty;
        RemainingQty = line.RemainingQty;
        PurchasePrice = line.PurchasePrice;
    }

    public int PurchaseItemId { get; }
    public string ProductName { get; }
    public string BatchNo { get; }
    public decimal PurchasedQty { get; }
    public decimal ReturnedQty { get; }

    /// <summary>Units still returnable = min(purchased − returned, batch on hand).</summary>
    public decimal RemainingQty { get; }

    public decimal PurchasePrice { get; }

    /// <summary>Units the operator wants to send back on this line (0 = leave alone).</summary>
    public decimal ReturnQty
    {
        get => _returnQty;
        set => SetProperty(ref _returnQty, value);
    }
}
