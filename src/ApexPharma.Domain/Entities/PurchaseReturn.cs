using System.ComponentModel.DataAnnotations.Schema;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// Goods returned to a supplier (plan.md §7.2). Decrements the exact
/// <see cref="Batch"/> and reverses the amount, in a transaction (plan.md §12
/// returns). Distinct from <see cref="SaleReturn"/> to keep the two directions
/// unambiguous.
/// </summary>
public class PurchaseReturn
{
    public int ReturnId { get; set; }

    /// <summary>The original purchase this return reverses.</summary>
    public int PurchaseId { get; set; }
    public Purchase? Purchase { get; set; }

    /// <summary>The exact lot being sent back — stock decrements from this batch.</summary>
    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Qty { get; set; }

    /// <summary>Amount reversed against the supplier ledger.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public string? Reason { get; set; }

    public DateTime Date { get; set; }

    /// <summary>FK to the <see cref="User"/> who processed the return (audit).</summary>
    public int CreatedBy { get; set; }
    public User? CreatedByUser { get; set; }
}
