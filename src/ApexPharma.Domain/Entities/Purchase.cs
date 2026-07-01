using System.ComponentModel.DataAnnotations.Schema;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A supplier purchase invoice / GRN header (plan.md §7.2). Saving a purchase
/// creates or updates <see cref="Batch"/> rows and increases batch-level stock
/// (plan.md §6.1). Line detail lives in <see cref="PurchaseItem"/>.
/// </summary>
public class Purchase
{
    public int PurchaseId { get; set; }

    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>The supplier's own invoice number (their reference, for reconciliation).</summary>
    public string? SupplierInvoiceNo { get; set; }

    public DateTime InvoiceDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Subtotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GstAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; }

    /// <summary>FK to the <see cref="User"/> who recorded the purchase (audit).</summary>
    public int CreatedBy { get; set; }
    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; }

    public ICollection<PurchaseItem> Items { get; set; } = new List<PurchaseItem>();
    public ICollection<PurchaseReturn> PurchaseReturns { get; set; } = new List<PurchaseReturn>();
}
