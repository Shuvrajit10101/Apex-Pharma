using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.Reporting;

/// <summary>
/// Read-only reporting for the owner and accountant (plan.md §6.1, §11): sales + profit, the
/// low-stock / reorder list, near-expiry &amp; expired stock, the legal Schedule H/H1/X register,
/// and the GST/HSN summary for GSTR-1. Every method is a pure query — it mutates nothing
/// (plan.md §6.1) — and returns flat DTO rows (never EF entities) so the presentation layer
/// runs no stock/money logic (plan.md §8 layering). Access is gated on
/// <see cref="Permission.ViewReports"/> (Owner + Pharmacist, plan.md §4).
/// </summary>
public interface IReportService
{
    /// <summary>
    /// Sales within the inclusive date range with per-bill money figures and profit, plus
    /// footing totals (plan.md §11 — daily/periodic sales + profit). The range is applied on
    /// <c>BillDate</c>; <paramref name="toDate"/> is treated inclusively to the end of that day.
    /// </summary>
    Task<SalesReport> GetSalesReportAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Products whose total on-hand across all batches is at or below their reorder level
    /// (plan.md §11 — low-stock / reorder). Reuses <see cref="IInventoryService.GetLowStockAsync"/>.
    /// </summary>
    Task<IReadOnlyList<LowStockRow>> GetLowStockReportAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Batches within <paramref name="withinDays"/> of expiry (default 90) OR already expired,
    /// with product/batch/qty/expiry (plan.md §11). Reuses <see cref="IInventoryService"/>'s
    /// near-expiry and expired queries; expired rows are flagged so the UI can separate them.
    /// </summary>
    Task<IReadOnlyList<ExpiryRow>> GetExpiryReportAsync(int withinDays = IInventoryService.DefaultNearExpiryDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// The Schedule H/H1/X register (plan.md §14) — one row per dispensed scheduled-drug sale
    /// line in the range, with date, bill, drug, batch, qty, patient, doctor, and Rx reference.
    /// Non-scheduled lines are excluded. The range is applied on the sale's <c>BillDate</c>.
    /// </summary>
    Task<IReadOnlyList<ScheduleRegisterRow>> GetScheduleRegisterAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// The GST/HSN summary for GSTR-1 (plan.md §11): sale lines in the range grouped by HSN
    /// code + GST rate into taxable value, CGST, SGST, and total, with grand totals. The range
    /// is applied on the sale's <c>BillDate</c>.
    /// </summary>
    Task<HsnSummaryReport> GetHsnSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// The GSTR-1 / GST-return export for one calendar month (plan.md §11): retail sales bucketed
    /// into the B2CS (by rate + place-of-supply) and HSN (by HSN + rate, with UQC and quantity)
    /// outward sections, the credit-notes (returns) section aggregated by rate, the
    /// documents-issued summary (first/last bill + count), and the gross outward totals. The month
    /// window is derived the same way as every other report (whole days on <c>BillDate</c>).
    /// Payment-mode-agnostic (cash + credit both count); the credit-notes section is kept separate
    /// and does NOT net into B2CS/HSN. <paramref name="placeOfSupply"/> is the pharmacy's state.
    /// </summary>
    Task<Gstr1Report> GetGstr1Async(int year, int month, string placeOfSupply, CancellationToken cancellationToken = default);
}
