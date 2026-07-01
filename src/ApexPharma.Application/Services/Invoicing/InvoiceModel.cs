using ApexPharma.Application.Services.Settings;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.Invoicing;

/// <summary>
/// The fully-assembled, layout-agnostic data for one GST invoice (plan.md §11, §14). The
/// <see cref="IInvoiceService"/> builds this from a persisted sale + the pharmacy profile, and
/// the QuestPDF renderer turns it into a thermal receipt (or, later, an A4 layout — the model is
/// deliberately independent of paper size). It is exposed so the assembled figures — GSTIN, DL
/// number, bill no, line totals, CGST/SGST breakup, grand total — are unit-testable without
/// rendering pixels (plan.md §12).
/// </summary>
public sealed class InvoiceModel
{
    // ---- Pharmacy header (from SettingsService — legally required, plan.md §14) ----
    public string PharmacyName { get; init; } = string.Empty;
    public string AddressLine { get; init; } = string.Empty;
    public string CityState { get; init; } = string.Empty;
    public string Gstin { get; init; } = string.Empty;
    public string DlNumber { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;

    // ---- Bill header ----
    public string BillNo { get; init; } = string.Empty;
    public DateTime BillDate { get; init; }
    public string CashierName { get; init; } = string.Empty;
    public string? CustomerName { get; init; }
    public string? CustomerPhone { get; init; }
    public PaymentMode PaymentMode { get; init; }

    /// <summary>Prescribing doctor — set when the bill has a scheduled drug (plan.md §14).</summary>
    public string? DoctorName { get; init; }

    /// <summary>Prescription reference — set when the bill has a scheduled drug.</summary>
    public string? PrescriptionRef { get; init; }

    /// <summary>True when any line is a Schedule H/H1/X drug — drives the Schedule-H note (plan.md §14).</summary>
    public bool HasScheduledDrug { get; init; }

    // ---- Lines + tax summary ----
    public IReadOnlyList<InvoiceLine> Lines { get; init; } = new List<InvoiceLine>();

    /// <summary>CGST/SGST breakup grouped by GST rate / HSN (the tax summary block, plan.md §14).</summary>
    public IReadOnlyList<InvoiceTaxSummaryRow> TaxSummary { get; init; } = new List<InvoiceTaxSummaryRow>();

    // ---- Money roll-up (mirrors the persisted Sale header) ----
    public decimal Subtotal { get; init; }
    public decimal Discount { get; init; }
    public decimal Cgst { get; init; }
    public decimal Sgst { get; init; }
    public decimal RoundOff { get; init; }
    public decimal Total { get; init; }

    // ---- Footer ----
    public string Footer { get; init; } = string.Empty;

    /// <summary>Total quantity of items on the bill (for a quick "items sold" line).</summary>
    public decimal TotalQty { get; init; }
}

/// <summary>One printed bill line (plan.md §14 — name, batch, expiry, qty, rate, discount, GST%, amount).</summary>
public sealed class InvoiceLine
{
    public string ProductName { get; init; } = string.Empty;
    public string BatchNo { get; init; } = string.Empty;
    public DateTime Expiry { get; init; }
    public string? HsnCode { get; init; }
    public decimal Qty { get; init; }
    public decimal Rate { get; init; }
    public decimal Discount { get; init; }
    public decimal GstRate { get; init; }

    /// <summary>Line gross = net taxable + CGST + SGST (foots to the header, plan.md §14).</summary>
    public decimal Amount { get; init; }
}

/// <summary>
/// One row of the CGST/SGST tax summary, grouped by GST rate (and HSN) — the tax breakup block a
/// GST invoice must carry (plan.md §14). Taxable is the post-discount base for that rate band.
/// </summary>
public sealed class InvoiceTaxSummaryRow
{
    public decimal GstRate { get; init; }
    public string? HsnCode { get; init; }
    public decimal Taxable { get; init; }
    public decimal Cgst { get; init; }
    public decimal Sgst { get; init; }

    /// <summary>CGST + SGST for this rate band.</summary>
    public decimal TotalGst => Cgst + Sgst;
}
