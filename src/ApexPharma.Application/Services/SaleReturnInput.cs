namespace ApexPharma.Application.Services;

/// <summary>
/// A request to return items from a completed sale (plan.md §6.1 returns). The sale is
/// located by <see cref="BillNo"/>; each requested line names the <c>SaleItemId</c>
/// being reversed and the quantity to return. A line quantity must be ≤ the sold qty minus
/// what has already been returned (the service blocks over-return).
/// </summary>
public sealed class SaleReturnInput
{
    /// <summary>The human-facing bill number of the sale to return against (e.g. "INV-000007").</summary>
    public string BillNo { get; set; } = string.Empty;

    /// <summary>Free-text reason recorded on every return row (optional).</summary>
    public string? Reason { get; set; }

    /// <summary>The per-line return requests. At least one with a positive qty is required.</summary>
    public List<SaleReturnLineInput> Lines { get; set; } = new();
}

/// <summary>One line of a sales return: which sold line, and how many units to return.</summary>
public sealed class SaleReturnLineInput
{
    /// <summary>The <see cref="Domain.Entities.SaleItem"/> primary key being reversed.</summary>
    public int SaleItemId { get; set; }

    /// <summary>Units to return for this line (must be &gt; 0 and within the remaining returnable qty).</summary>
    public decimal Qty { get; set; }
}
