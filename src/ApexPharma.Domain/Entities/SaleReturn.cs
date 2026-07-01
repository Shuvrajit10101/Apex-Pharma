using System.ComponentModel.DataAnnotations.Schema;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A customer sales return (plan.md §7.2). Restocks the exact <see cref="Batch"/>
/// the goods came from and reverses the amount, in a transaction (plan.md §12
/// returns). Kept as its own entity (mirroring <see cref="PurchaseReturn"/>) so the
/// two return directions never get confused.
/// </summary>
public class SaleReturn
{
    public int ReturnId { get; set; }

    /// <summary>The original bill this return reverses.</summary>
    public int SaleId { get; set; }
    public Sale? Sale { get; set; }

    /// <summary>The exact lot being restocked — restock must hit the same batch.</summary>
    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Qty { get; set; }

    /// <summary>Amount refunded/reversed to the customer.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public string? Reason { get; set; }

    public DateTime Date { get; set; }

    /// <summary>FK to the <see cref="User"/> who processed the return (audit).</summary>
    public int CreatedBy { get; set; }
    public User? CreatedByUser { get; set; }
}
