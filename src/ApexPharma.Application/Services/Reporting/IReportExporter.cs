namespace ApexPharma.Application.Services.Reporting;

/// <summary>
/// Turns report DTOs into portable artifacts (plan.md §11 — reports the owner/accountant can
/// keep): CSV text for every report, plus a printable PDF for the two legal/accounting reports
/// (the Schedule H/H1/X register and the GST/HSN summary). Pure formatting — no data access —
/// so the CSV shape is unit-testable without a database. PDF bytes are produced via QuestPDF.
/// </summary>
public interface IReportExporter
{
    /// <summary>CSV for the sales/profit report (one row per bill + a trailing totals row).</summary>
    string SalesReportCsv(SalesReport report);

    /// <summary>CSV for the low-stock / reorder report.</summary>
    string LowStockCsv(IReadOnlyList<LowStockRow> rows);

    /// <summary>CSV for the near-expiry &amp; expired report.</summary>
    string ExpiryCsv(IReadOnlyList<ExpiryRow> rows);

    /// <summary>CSV for the Schedule H/H1/X register.</summary>
    string ScheduleRegisterCsv(IReadOnlyList<ScheduleRegisterRow> rows);

    /// <summary>CSV for the GST/HSN summary (one row per HSN+rate + a trailing totals row).</summary>
    string HsnSummaryCsv(HsnSummaryReport report);

    /// <summary>Renders the Schedule H/H1/X register to PDF bytes (A4).</summary>
    byte[] ScheduleRegisterPdf(ReportHeader header, DateTime fromDate, DateTime toDate, IReadOnlyList<ScheduleRegisterRow> rows);

    /// <summary>Renders the GST/HSN summary to PDF bytes (A4).</summary>
    byte[] HsnSummaryPdf(ReportHeader header, DateTime fromDate, DateTime toDate, HsnSummaryReport report);
}
