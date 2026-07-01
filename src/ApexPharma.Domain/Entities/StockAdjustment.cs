using System.ComponentModel.DataAnnotations.Schema;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// An audited change to a batch's on-hand quantity outside of sale/purchase
/// (plan.md §7.2, §6.1): expiry write-off, breakage/wastage, or physical-count
/// correction. <see cref="QtyDelta"/> is signed (negative removes stock, positive
/// adds) so a single field covers every adjustment direction.
/// </summary>
public class StockAdjustment
{
    public int AdjustmentId { get; set; }

    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    /// <summary>
    /// Denormalized product FK for fast product-level adjustment reporting; the
    /// ER model links StockAdjustment to Product (plan.md §7.1).
    /// </summary>
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public AdjustmentType Type { get; set; }

    /// <summary>Signed change to <see cref="Batch.QtyOnHand"/> (negative = removed).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal QtyDelta { get; set; }

    public string? Reason { get; set; }

    public DateTime Date { get; set; }

    /// <summary>FK to the <see cref="User"/> who made the adjustment (audit — plan.md §4).</summary>
    public int CreatedBy { get; set; }
    public User? CreatedByUser { get; set; }
}
