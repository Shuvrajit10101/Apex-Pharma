using System.ComponentModel.DataAnnotations.Schema;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A POS bill header (plan.md §7.2) — the heart of the app. Created in a single DB
/// transaction that also inserts <see cref="SaleItem"/> rows and decrements batch
/// stock (plan.md §6.1, §9).
/// </summary>
public class Sale
{
    public int SaleId { get; set; }

    /// <summary>
    /// Human-facing bill number. Must be <b>unique and sequential with no gaps</b>,
    /// even after a crash mid-sale (plan.md §6.2, §12) — enforced by a UNIQUE index.
    /// </summary>
    public string BillNo { get; set; } = string.Empty;

    public DateTime BillDate { get; set; }

    /// <summary>Optional — walk-in cash sales have no customer.</summary>
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>
    /// Prescribing doctor. Captured (with <see cref="PrescriptionRef"/>) when any line
    /// is a Schedule H/H1 drug — a legal requirement enforced at billing and surfaced
    /// in the H1 register (plan.md §14). Stored on the sale so the register report can
    /// be derived without a separate table.
    /// </summary>
    public string? DoctorName { get; set; }

    /// <summary>Prescription reference for Schedule H/H1 compliance (see <see cref="DoctorName"/>).</summary>
    public string? PrescriptionRef { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Subtotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Discount { get; set; }

    /// <summary>Central GST half of the intra-state split (plan.md §12 GST math).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Cgst { get; set; }

    /// <summary>State GST half of the intra-state split.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Sgst { get; set; }

    /// <summary>Rounding adjustment so the printed total is a clean figure.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal RoundOff { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; }

    public PaymentMode PaymentMode { get; set; }

    /// <summary>FK to the <see cref="User"/> who created the bill (audit).</summary>
    public int CreatedBy { get; set; }
    public User? CreatedByUser { get; set; }

    public ICollection<SaleItem> Items { get; set; } = new List<SaleItem>();
    public ICollection<SaleReturn> Returns { get; set; } = new List<SaleReturn>();
}
