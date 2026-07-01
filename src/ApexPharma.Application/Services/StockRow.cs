namespace ApexPharma.Application.Services;

/// <summary>
/// A flattened stock row for the read-only inventory view (plan.md §6.1 "view current
/// stock by product/batch"): one row per batch, carrying just what the grid shows plus
/// the flags the UI colour-codes on — near-expiry and low-stock (plan.md §10). Projected
/// server-side so the presentation layer never sees EF entities or runs stock logic.
/// </summary>
public sealed class StockRow
{
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;

    public int BatchId { get; init; }
    public string BatchNo { get; init; } = string.Empty;
    public DateTime ExpiryDate { get; init; }
    public decimal QtyOnHand { get; init; }
    public decimal Mrp { get; init; }

    /// <summary>True when this batch is already expired (expiry on/before today).</summary>
    public bool IsExpired { get; init; }

    /// <summary>True when this batch expires within the near-expiry window (and isn't yet expired).</summary>
    public bool IsNearExpiry { get; init; }

    /// <summary>
    /// True when the product's TOTAL on-hand (across all its batches) is at or below its
    /// reorder level — a low-stock/reorder signal (plan.md §6.1). Set per product, so every
    /// row of a low-stock product carries it.
    /// </summary>
    public bool IsLowStock { get; init; }
}
