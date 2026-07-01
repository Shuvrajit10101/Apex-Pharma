using System.ComponentModel.DataAnnotations.Schema;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A distributor/wholesaler we buy stock from (plan.md §7.2). Holds GST and drug-
/// license identifiers because both are legally required on purchase records, and
/// <see cref="StateCode"/> drives GST place-of-supply (intra- vs inter-state).
/// </summary>
public class Supplier
{
    public int SupplierId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Supplier GSTIN — required for input-tax records.</summary>
    public string? Gstin { get; set; }

    /// <summary>Supplier drug-license number (20B/21B) — compliance record.</summary>
    public string? DlNumber { get; set; }

    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }

    /// <summary>GST state code — determines intra-state (CGST/SGST) vs inter-state (IGST) supply.</summary>
    public string? StateCode { get; set; }

    /// <summary>Ledger opening balance carried in for a lightweight supplier ledger (plan.md §3).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal OpeningBalance { get; set; }

    public ICollection<Batch> Batches { get; set; } = new List<Batch>();
    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();
}
