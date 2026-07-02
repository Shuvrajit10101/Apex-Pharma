namespace ApexPharma.Application.Services;

/// <summary>
/// A read-only view of a sale and its lines with per-line returnable quantities (plan.md §6.1),
/// used to drive the sales-return UI. Each line reports what was sold, what has already been
/// returned, and how much remains returnable.
/// </summary>
/// <param name="SaleId">The located sale's primary key.</param>
/// <param name="BillNo">The bill number.</param>
/// <param name="BillDate">When the sale was billed.</param>
/// <param name="IsCredit">True when the original sale was on credit (khata) — a return reduces the balance.</param>
/// <param name="Lines">The per-line returnable rows.</param>
public sealed record SaleReturnableLines(
    int SaleId,
    string BillNo,
    DateTime BillDate,
    bool IsCredit,
    IReadOnlyList<SaleReturnableLine> Lines);

/// <summary>One sold line with its sold / already-returned / remaining-returnable quantities.</summary>
/// <param name="SaleItemId">The sold line's primary key (targets a return).</param>
/// <param name="ProductId">The product sold on this line.</param>
/// <param name="ProductName">Product display name.</param>
/// <param name="BatchId">The batch the line was dispensed from (restock target).</param>
/// <param name="BatchNo">The batch number.</param>
/// <param name="SoldQty">Quantity originally sold on this line.</param>
/// <param name="ReturnedQty">Quantity already returned against this line.</param>
/// <param name="Rate">Selling rate per unit applied on this line.</param>
public sealed record SaleReturnableLine(
    int SaleItemId,
    int ProductId,
    string ProductName,
    int BatchId,
    string BatchNo,
    decimal SoldQty,
    decimal ReturnedQty,
    decimal Rate)
{
    /// <summary>Units still returnable on this line = sold − already returned (never negative).</summary>
    public decimal RemainingQty => Math.Max(0m, SoldQty - ReturnedQty);
}
