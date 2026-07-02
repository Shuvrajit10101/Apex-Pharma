namespace ApexPharma.Application.Services;

/// <summary>
/// A read-only view of a purchase and its lines with per-line returnable quantities (plan.md §6.1),
/// used to drive the purchase-return UI. Each line reports what was purchased, what has already
/// been returned, and how much remains returnable (also capped by the batch's on-hand stock).
/// </summary>
/// <param name="PurchaseId">The purchase's primary key.</param>
/// <param name="SupplierName">Supplier the goods came from.</param>
/// <param name="InvoiceDate">The purchase invoice date.</param>
/// <param name="Lines">The per-line returnable rows.</param>
public sealed record PurchaseReturnableLines(
    int PurchaseId,
    string SupplierName,
    DateTime InvoiceDate,
    IReadOnlyList<PurchaseReturnableLine> Lines);

/// <summary>One purchased line with its purchased / already-returned / remaining quantities.</summary>
/// <param name="PurchaseItemId">The purchased line's primary key (targets a return).</param>
/// <param name="ProductId">The product purchased on this line.</param>
/// <param name="ProductName">Product display name.</param>
/// <param name="BatchId">The batch created/updated by this line (decrement target); 0 if not resolvable.</param>
/// <param name="BatchNo">The batch number.</param>
/// <param name="PurchasedQty">Quantity originally purchased on this line.</param>
/// <param name="ReturnedQty">Quantity already returned against this line.</param>
/// <param name="BatchOnHand">Current on-hand for the line's batch (the hard stock cap).</param>
/// <param name="PurchasePrice">Cost per unit on this line (return amount = qty × this).</param>
public sealed record PurchaseReturnableLine(
    int PurchaseItemId,
    int ProductId,
    string ProductName,
    int BatchId,
    string BatchNo,
    decimal PurchasedQty,
    decimal ReturnedQty,
    decimal BatchOnHand,
    decimal PurchasePrice)
{
    /// <summary>
    /// Units still returnable = min(purchased − already returned, batch on hand), never negative.
    /// The stock cap matters because the lot may have been partly sold since purchase.
    /// </summary>
    public decimal RemainingQty => Math.Max(0m, Math.Min(PurchasedQty - ReturnedQty, BatchOnHand));
}
