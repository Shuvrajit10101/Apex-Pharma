using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.Reporting;

/// <summary>
/// Read-only DTO rows and summaries returned by <see cref="IReportService"/> (plan.md §11).
/// Reports never leak EF entities to the UI (plan.md §8 layering) — every figure the owner
/// or accountant needs is projected into these flat, printable shapes.
/// </summary>

/// <summary>
/// One bill on the sales report (plan.md §11 — daily/periodic sales + profit). Carries the
/// header money figures plus the <see cref="Profit"/> for the bill (Σ per line of
/// net-taxable-ex-GST − purchase cost). "Net" is the taxable value after all discounts,
/// excluding GST — the same base the profit margin is measured against.
/// </summary>
public sealed class SalesReportRow
{
    public int SaleId { get; init; }
    public string BillNo { get; init; } = string.Empty;
    public DateTime BillDate { get; init; }

    /// <summary>Customer name, or "Walk-in" for a cash sale with no customer.</summary>
    public string CustomerName { get; init; } = "Walk-in";

    public PaymentMode PaymentMode { get; init; }

    /// <summary>Taxable value after all discounts, excluding GST (Sale.Subtotal).</summary>
    public decimal Subtotal { get; init; }

    public decimal Discount { get; init; }
    public decimal Cgst { get; init; }
    public decimal Sgst { get; init; }

    /// <summary>Grand total actually charged (Sale.Total).</summary>
    public decimal Total { get; init; }

    /// <summary>Bill profit = Σ(line net taxable ex-GST) − Σ(Batch.PurchasePrice × qty).</summary>
    public decimal Profit { get; init; }
}

/// <summary>Footing totals for the whole sales report (plan.md §11).</summary>
public sealed class SalesReportSummary
{
    /// <summary>Number of bills in the range.</summary>
    public int BillCount { get; init; }

    /// <summary>Σ Sale.Total — gross value charged including GST.</summary>
    public decimal Gross { get; init; }

    /// <summary>Σ Sale.Subtotal — net taxable after discounts, excluding GST.</summary>
    public decimal Net { get; init; }

    /// <summary>Σ (Sale.Cgst + Sale.Sgst).</summary>
    public decimal TotalGst { get; init; }

    public decimal TotalDiscount { get; init; }

    /// <summary>Σ per-bill profit.</summary>
    public decimal TotalProfit { get; init; }
}

/// <summary>The sales report: its bill rows and their footing totals.</summary>
public sealed class SalesReport
{
    public IReadOnlyList<SalesReportRow> Rows { get; init; } = Array.Empty<SalesReportRow>();
    public SalesReportSummary Summary { get; init; } = new();
}

/// <summary>
/// One product on the low-stock / reorder report (plan.md §11). Total on-hand across all
/// batches is at or below the reorder level — a "buy more" signal.
/// </summary>
public sealed class LowStockRow
{
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string? GenericName { get; init; }
    public decimal TotalOnHand { get; init; }
    public int ReorderLevel { get; init; }
    public string? RackLocation { get; init; }
}

/// <summary>
/// One batch on the near-expiry / expired report (plan.md §11). Carries the product, batch,
/// quantity, and expiry, plus whether it is already expired (write-off) or merely near expiry.
/// </summary>
public sealed class ExpiryRow
{
    public int ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int BatchId { get; init; }
    public string BatchNo { get; init; } = string.Empty;
    public DateTime ExpiryDate { get; init; }
    public decimal QtyOnHand { get; init; }
    public decimal Mrp { get; init; }

    /// <summary>True = already expired (quarantine/write-off); false = within the near-expiry window.</summary>
    public bool IsExpired { get; init; }
}

/// <summary>
/// One line of the Schedule H/H1/X register (plan.md §14) — the legal register. One row per
/// dispensed scheduled-drug <see cref="Domain.Entities.SaleItem"/>: date, bill, drug, batch,
/// qty, patient (name/phone), prescriber (doctor), and prescription reference.
/// </summary>
public sealed class ScheduleRegisterRow
{
    public DateTime BillDate { get; init; }
    public string BillNo { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;

    /// <summary>The drug's schedule (H, H1, or X) — only scheduled drugs appear on this register.</summary>
    public DrugSchedule Schedule { get; init; }

    public string BatchNo { get; init; } = string.Empty;
    public DateTime ExpiryDate { get; init; }
    public decimal Qty { get; init; }

    /// <summary>Patient/customer name, or empty for a walk-in.</summary>
    public string PatientName { get; init; } = string.Empty;
    public string? PatientPhone { get; init; }

    /// <summary>Prescribing doctor (captured at billing — legally required).</summary>
    public string? DoctorName { get; init; }

    /// <summary>Prescription reference (captured at billing — legally required).</summary>
    public string? PrescriptionRef { get; init; }
}

/// <summary>
/// One HSN + GST-rate group on the GST/HSN summary (plan.md §11 — GSTR-1). Aggregates the
/// taxable value, CGST, SGST, and total across all sale lines sharing the same HSN code and
/// GST rate in the range.
/// </summary>
public sealed class HsnSummaryRow
{
    /// <summary>HSN code (or "(none)" for lines whose product has no HSN recorded).</summary>
    public string HsnCode { get; init; } = string.Empty;
    public decimal GstRate { get; init; }

    /// <summary>Σ net taxable value (ex-GST, after discounts) for this HSN+rate group.</summary>
    public decimal Taxable { get; init; }

    public decimal Cgst { get; init; }
    public decimal Sgst { get; init; }

    /// <summary>Taxable + CGST + SGST for the group.</summary>
    public decimal Total { get; init; }
}

/// <summary>Grand totals for the GST/HSN summary (plan.md §11).</summary>
public sealed class HsnSummaryTotals
{
    public decimal Taxable { get; init; }
    public decimal Cgst { get; init; }
    public decimal Sgst { get; init; }
    public decimal Total { get; init; }
}

/// <summary>The GST/HSN summary: its per-HSN+rate rows and grand totals.</summary>
public sealed class HsnSummaryReport
{
    public IReadOnlyList<HsnSummaryRow> Rows { get; init; } = Array.Empty<HsnSummaryRow>();
    public HsnSummaryTotals Totals { get; init; } = new();
}
