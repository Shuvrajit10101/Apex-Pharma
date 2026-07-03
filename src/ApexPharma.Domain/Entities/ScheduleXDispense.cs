using System.ComponentModel.DataAnnotations.Schema;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// One dispense record for a single Schedule-X (narcotic/psychotropic) sale line — the strict
/// legal register the Drugs and Cosmetics Rules require for Schedule-X drugs (plan.md §14, §15 —
/// Phase 2f). Schedule X carries the tightest controls: full patient and prescriber identity,
/// the prescription number/date, and confirmation that a duplicate prescription copy was retained
/// at the pharmacy. Each row is written inside the sale's ACID transaction, one per Schedule-X
/// <see cref="SaleItem"/>, so a Schedule-X sale can never persist without its register entry.
/// <para>
/// <see cref="ProductId"/> and <see cref="BatchId"/> are denormalised (also reachable through the
/// <see cref="SaleItem"/>) so the running-balance register can join drug + batch directly without
/// walking the sale line. Every FK is delete-restricted (configured in the DbContext) so a legal
/// register row can never be silently orphaned by deleting a parent (plan.md §6.2, §12).
/// </para>
/// </summary>
public class ScheduleXDispense
{
    public int ScheduleXDispenseId { get; set; }

    /// <summary>The bill this dispense belongs to.</summary>
    public int SaleId { get; set; }
    public Sale? Sale { get; set; }

    /// <summary>The exact sold line that dispensed the Schedule-X drug.</summary>
    public int SaleItemId { get; set; }
    public SaleItem? SaleItem { get; set; }

    /// <summary>The Schedule-X product dispensed (denormalised for the register join).</summary>
    public int ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>The exact lot dispensed (denormalised for the register — batch traceability).</summary>
    public int BatchId { get; set; }
    public Batch? Batch { get; set; }

    /// <summary>Units of the Schedule-X drug dispensed on this line.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Qty { get; set; }

    // ---- Patient identity (required for Schedule X) ----

    /// <summary>Patient's full name (required — a walk-in is not permitted for Schedule X).</summary>
    public string PatientName { get; set; } = string.Empty;

    /// <summary>Patient's address (required for the Schedule-X register).</summary>
    public string PatientAddress { get; set; } = string.Empty;

    /// <summary>Patient's phone (optional).</summary>
    public string? PatientPhone { get; set; }

    // ---- Prescriber identity (required for Schedule X) ----

    /// <summary>Prescribing doctor's name (required).</summary>
    public string PrescriberName { get; set; } = string.Empty;

    /// <summary>Prescribing doctor's address (required).</summary>
    public string PrescriberAddress { get; set; } = string.Empty;

    /// <summary>Prescribing doctor's medical registration number (required).</summary>
    public string PrescriberRegNo { get; set; } = string.Empty;

    // ---- Prescription (required for Schedule X) ----

    /// <summary>The prescription number (required).</summary>
    public string PrescriptionNumber { get; set; } = string.Empty;

    /// <summary>The prescription date (required).</summary>
    public DateTime PrescriptionDate { get; set; }

    /// <summary>True when a duplicate copy of the prescription was retained at the pharmacy (legally required).</summary>
    public bool PrescriptionRetained { get; set; }

    /// <summary>When the drug was dispensed (stored UTC; the UI converts to local for display).</summary>
    public DateTime DispensedAt { get; set; }

    /// <summary>FK to the <see cref="User"/> who dispensed (audit — plan.md §4).</summary>
    public int CreatedBy { get; set; }
    public User? CreatedByUser { get; set; }
}
