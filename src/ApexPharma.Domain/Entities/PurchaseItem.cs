using System.ComponentModel.DataAnnotations.Schema;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A single line on a supplier purchase (plan.md §7.2). Carries the batch number
/// and expiry so that saving the purchase can create the corresponding
/// <see cref="Batch"/> — batch + expiry are mandatory on stock-in (plan.md §14).
/// </summary>
public class PurchaseItem
{
    public int PurchaseItemId { get; set; }

    public int PurchaseId { get; set; }
    public Purchase? Purchase { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Batch/lot number as printed — becomes <see cref="Batch.BatchNo"/>.</summary>
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>Expiry of the received lot — becomes <see cref="Batch.ExpiryDate"/>.</summary>
    public DateTime ExpiryDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Qty { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PurchasePrice { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Mrp { get; set; }

    /// <summary>GST rate percent captured at purchase time (for input-tax records).</summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal GstRate { get; set; }

    /// <summary>Returns booked against this specific purchased line (per-line return tracking).</summary>
    public ICollection<PurchaseReturn> Returns { get; set; } = new List<PurchaseReturn>();
}
