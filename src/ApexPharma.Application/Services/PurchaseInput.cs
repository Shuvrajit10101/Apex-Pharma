using System.Collections.Generic;

namespace ApexPharma.Application.Services;

/// <summary>
/// Data carried into <see cref="IPurchaseService.RecordPurchaseAsync"/> — a supplier
/// purchase invoice (GRN) header plus its lines (plan.md §6.1, §7.2, §9). A DTO keeps the
/// presentation layer from constructing entities directly and gives the service one place
/// to validate before it touches stock in a transaction.
/// </summary>
public sealed class PurchaseInput
{
    /// <summary>The supplier the goods were received from (must exist).</summary>
    public int SupplierId { get; set; }

    /// <summary>The supplier's own invoice number (their reference, for reconciliation).</summary>
    public string? SupplierInvoiceNo { get; set; }

    /// <summary>Date printed on the supplier's invoice.</summary>
    public DateTime InvoiceDate { get; set; }

    /// <summary>The lines received on this invoice — at least one is required.</summary>
    public List<PurchaseLineInput> Lines { get; set; } = new();
}

/// <summary>
/// A single received line on a purchase (plan.md §6.1, §7.2). Each line stocks in a
/// batch: product + batch number + expiry + quantity + purchase price + MRP + GST rate.
/// </summary>
public sealed class PurchaseLineInput
{
    /// <summary>The product received (must exist and be active).</summary>
    public int ProductId { get; set; }

    /// <summary>Batch/lot number as printed on the pack (required — mandatory on stock-in).</summary>
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>Expiry of the received lot. Must be in the future — expired goods are refused.</summary>
    public DateTime ExpiryDate { get; set; }

    /// <summary>Units received (must be &gt; 0).</summary>
    public decimal Qty { get; set; }

    /// <summary>Cost we paid per unit (must be ≥ 0).</summary>
    public decimal PurchasePrice { get; set; }

    /// <summary>MRP printed on this lot's packs (must be ≥ 0).</summary>
    public decimal Mrp { get; set; }

    /// <summary>GST rate percent for this line (must be an Indian slab: 0/5/12/18/28).</summary>
    public decimal GstRate { get; set; }
}
