using System.ComponentModel.DataAnnotations.Schema;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A single line on a POS bill (plan.md §7.2). References the specific
/// <see cref="Batch"/> it was dispensed from (chosen by FEFO) so stock decrements
/// against the right lot and the H1 register can report batch numbers. GST is
/// stored per line as computed at sale time (rates can change later, but the bill
/// must reprint identically).
/// </summary>
public class SaleItem
{
    public int SaleItemId { get; set; }

    public int SaleId { get; set; }
    public Sale? Sale { get; set; }

    /// <summary>The exact lot dispensed (FEFO-selected) — see <see cref="Batch"/>.</summary>
    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Qty { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Mrp { get; set; }

    /// <summary>Selling rate per unit applied on this line.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Rate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Discount { get; set; }

    /// <summary>GST rate percent applied to this line (snapshot at sale time).</summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal GstRate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Cgst { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Sgst { get; set; }

    /// <summary>Line gross = taxable + CGST + SGST (see GstService).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal LineTotal { get; set; }

    /// <summary>Returns booked against this specific sold line (per-line return tracking).</summary>
    public ICollection<SaleReturn> Returns { get; set; } = new List<SaleReturn>();
}
