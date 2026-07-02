using ApexPharma.Application.Services.Inventory;

namespace ApexPharma.Desktop.ViewModels.StockAdjustments;

/// <summary>
/// One expired batch in the expiry write-off grid (plan.md §6.1, §10): product, batch, expiry,
/// on-hand, and value at cost/MRP so the operator sees what would be lost. A selectable checkbox
/// drives the (multi-select) single-batch write-offs. No stock rule lives here (plan.md §8).
/// </summary>
public sealed class ExpiredBatchViewModel : ViewModelBase
{
    private bool _isSelected;

    public ExpiredBatchViewModel(AdjustableBatch batch)
    {
        BatchId = batch.BatchId;
        ProductName = batch.ProductName;
        BatchNo = batch.BatchNo;
        ExpiryDate = batch.ExpiryDate;
        QtyOnHand = batch.QtyOnHand;
        ValueAtCost = decimal.Round(batch.QtyOnHand * batch.PurchasePrice, 2);
        ValueAtMrp = decimal.Round(batch.QtyOnHand * batch.Mrp, 2);
    }

    public int BatchId { get; }
    public string ProductName { get; }
    public string BatchNo { get; }
    public System.DateTime ExpiryDate { get; }
    public decimal QtyOnHand { get; }
    public decimal ValueAtCost { get; }
    public decimal ValueAtMrp { get; }

    /// <summary>Whether this batch is ticked for the "write off selected" action.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
