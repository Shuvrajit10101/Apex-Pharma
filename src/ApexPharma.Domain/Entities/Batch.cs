using System.ComponentModel.DataAnnotations.Schema;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A specific lot of a product received from a supplier (plan.md §7, §7.2).
/// <b>Stock lives here, not on <see cref="Product"/></b>: the same medicine arrives
/// in lots with different expiry dates and MRPs, so quantity, expiry and price
/// belong to the batch. This is what enables FEFO dispensing, near-expiry
/// reporting, and correct stock valuation — none of which the source systems could
/// do (plan.md §7).
/// </summary>
public class Batch
{
    public int BatchId { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Manufacturer's batch/lot number (printed on the pack).</summary>
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>
    /// Expiry date of this lot. Indexed because it drives FEFO batch selection,
    /// near-expiry alerts, and the block on selling expired stock (plan.md §6.1).
    /// </summary>
    public DateTime ExpiryDate { get; set; }

    /// <summary>Maximum retail price printed on this lot's packs (per-batch — may differ across lots).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Mrp { get; set; }

    /// <summary>Cost we paid per unit — used for profit (sale − cost) reporting.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal PurchasePrice { get; set; }

    /// <summary>Selling price per unit for this lot (defaults from MRP; can be tuned).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; }

    /// <summary>
    /// Units currently on hand for this lot. Decremented transactionally on sale
    /// (never negative — plan.md §6.2 data integrity) and increased on purchase.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal QtyOnHand { get; set; }

    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    public DateTime ReceivedDate { get; set; }

    public ICollection<SaleItem> SaleItems { get; set; } = new List<SaleItem>();
    public ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();
    public ICollection<SaleReturn> SaleReturns { get; set; } = new List<SaleReturn>();
    public ICollection<PurchaseReturn> PurchaseReturns { get; set; } = new List<PurchaseReturn>();
}
