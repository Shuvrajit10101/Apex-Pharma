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

    /// <summary>The lines to sell — at least one is required.</summary>
    public List<SaleLineInput> Lines { get; set; } = new();
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
