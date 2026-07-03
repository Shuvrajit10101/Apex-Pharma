using System.Collections.Generic;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services;

/// <summary>
/// Data carried into <see cref="IBillingService.CreateSaleAsync"/> — a POS bill header plus
/// its lines (plan.md §6.1, §7.2, §9). A DTO keeps the presentation layer from constructing
/// entities directly and gives the service one place to validate before it touches stock,
/// bill numbering, and the customer khata inside a single ACID transaction.
/// </summary>
public sealed class SaleInput
{
    /// <summary>Optional customer — required only for a <see cref="PaymentMode.Credit"/> (khata) sale.</summary>
    public int? CustomerId { get; set; }

    /// <summary>Prescribing doctor — required when any line is a Schedule H/H1 drug (plan.md §14).</summary>
    public string? DoctorName { get; set; }

    /// <summary>Prescription reference — required when any line is a Schedule H/H1 drug (plan.md §14).</summary>
    public string? PrescriptionRef { get; set; }

    /// <summary>How the customer settled (Cash/Upi/Card/Credit). Credit adds the total to the khata.</summary>
    public PaymentMode PaymentMode { get; set; } = PaymentMode.Cash;

    /// <summary>Optional whole-bill discount applied to the pre-tax subtotal (must be ≥ 0).</summary>
    public decimal BillDiscount { get; set; }

    /// <summary>
    /// Strict Schedule-X capture (patient + prescriber + prescription + retained-copy flag),
    /// REQUIRED when any line is a <see cref="DrugSchedule.X"/> drug (plan.md §14 — Phase 2f).
    /// Ignored when no line is Schedule X. The service is the real boundary: a Schedule-X sale
    /// with this missing or incomplete is rejected and NOTHING persists.
    /// </summary>
    public ScheduleXCapture? ScheduleX { get; set; }

    /// <summary>The lines to sell — at least one is required.</summary>
    public List<SaleLineInput> Lines { get; set; } = new();
}

/// <summary>
/// The strict dual-Rx details captured for a Schedule-X (narcotic/psychotropic) sale (plan.md §14,
/// §15 — Phase 2f). Schedule X requires full patient and prescriber identity, the prescription
/// number and date, and confirmation that a duplicate copy of the prescription was retained at the
/// pharmacy. All string fields except <see cref="PatientPhone"/> are required and must be non-blank,
/// and <see cref="PrescriptionRetained"/> must be true — the service enforces this and one
/// <see cref="Domain.Entities.ScheduleXDispense"/> row is written per Schedule-X line.
/// </summary>
public sealed class ScheduleXCapture
{
    /// <summary>Patient's full name (required).</summary>
    public string? PatientName { get; set; }

    /// <summary>Patient's address (required).</summary>
    public string? PatientAddress { get; set; }

    /// <summary>Patient's phone (optional).</summary>
    public string? PatientPhone { get; set; }

    /// <summary>Prescribing doctor's name (required).</summary>
    public string? PrescriberName { get; set; }

    /// <summary>Prescribing doctor's address (required).</summary>
    public string? PrescriberAddress { get; set; }

    /// <summary>Prescribing doctor's medical registration number (required).</summary>
    public string? PrescriberRegNo { get; set; }

    /// <summary>The prescription number (required).</summary>
    public string? PrescriptionNumber { get; set; }

    /// <summary>The prescription date (required).</summary>
    public DateTime PrescriptionDate { get; set; }

    /// <summary>Must be true: a duplicate copy of the prescription was retained at the pharmacy.</summary>
    public bool PrescriptionRetained { get; set; }
}

/// <summary>
/// A single line to sell (plan.md §6.1, §7.2). The service resolves the actual batch(es) by
/// FEFO — the caller only names the product, quantity, and an optional per-line discount; the
/// rate comes from the dispensing batch's <see cref="Domain.Entities.Batch.SalePrice"/> and the
/// GST from the product's rate (never trusted from the client).
/// </summary>
public sealed class SaleLineInput
{
    /// <summary>The product to sell (must exist and be active).</summary>
    public int ProductId { get; set; }

    /// <summary>Units to sell (must be &gt; 0). May be dispensed across multiple FEFO batches.</summary>
    public decimal Qty { get; set; }

    /// <summary>Optional discount applied to this line's pre-tax value (must be ≥ 0).</summary>
    public decimal LineDiscount { get; set; }
}
