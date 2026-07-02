using System.Globalization;
using System.Text;
using QuestPDF.Fluent;

namespace ApexPharma.Application.Services.Reporting;

/// <summary>
/// Concrete <see cref="IReportExporter"/>. CSV is built with RFC-4180 quoting (fields with a
/// comma, quote, or newline are wrapped and inner quotes doubled) so the file opens cleanly in
/// Excel/LibreOffice for the accountant. PDF is rendered through QuestPDF documents (A4). All
/// methods are pure formatting over the DTOs — no database access — so the output is
/// deterministic and unit-testable.
/// </summary>
public sealed class ReportExporter : IReportExporter
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm";

    /// <inheritdoc />
    public string SalesReportCsv(SalesReport report)
    {
        var sb = new StringBuilder();
        AppendRow(sb, "Bill No", "Date", "Customer", "Payment", "Subtotal", "Discount", "CGST", "SGST", "Total", "Profit");
        foreach (SalesReportRow r in report.Rows)
        {
            AppendRow(sb,
                r.BillNo,
                r.BillDate.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
                r.CustomerName,
                r.PaymentMode.ToString(),
                Money(r.Subtotal), Money(r.Discount), Money(r.Cgst), Money(r.Sgst), Money(r.Total), Money(r.Profit));
        }

        // Trailing totals row so the CSV foots on its own.
        SalesReportSummary s = report.Summary;
        AppendRow(sb, "TOTAL", string.Empty, $"{s.BillCount} bill(s)", string.Empty,
            Money(s.Net), Money(s.TotalDiscount),
            Money(report.Rows.Sum(r => r.Cgst)), Money(report.Rows.Sum(r => r.Sgst)),
            Money(s.Gross), Money(s.TotalProfit));
        return sb.ToString();
    }

    /// <inheritdoc />
    public string LowStockCsv(IReadOnlyList<LowStockRow> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, "Product", "Generic", "On Hand", "Reorder Level", "Rack");
        foreach (LowStockRow r in rows)
        {
            AppendRow(sb, r.ProductName, r.GenericName ?? string.Empty, Qty(r.TotalOnHand),
                r.ReorderLevel.ToString(CultureInfo.InvariantCulture), r.RackLocation ?? string.Empty);
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public string ExpiryCsv(IReadOnlyList<ExpiryRow> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, "Status", "Product", "Batch", "Expiry", "Qty", "MRP");
        foreach (ExpiryRow r in rows)
        {
            AppendRow(sb, r.IsExpired ? "EXPIRED" : "Near-expiry", r.ProductName, r.BatchNo,
                r.ExpiryDate.ToString(DateFormat, CultureInfo.InvariantCulture), Qty(r.QtyOnHand), Money(r.Mrp));
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public string ScheduleRegisterCsv(IReadOnlyList<ScheduleRegisterRow> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, "Date", "Bill No", "Drug", "Schedule", "Batch", "Expiry", "Qty", "Patient", "Phone", "Doctor", "Rx Ref");
        foreach (ScheduleRegisterRow r in rows)
        {
            AppendRow(sb,
                r.BillDate.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
                r.BillNo, r.ProductName, r.Schedule.ToString(), r.BatchNo,
                r.ExpiryDate.ToString(DateFormat, CultureInfo.InvariantCulture),
                Qty(r.Qty),
                string.IsNullOrWhiteSpace(r.PatientName) ? "Walk-in" : r.PatientName,
                r.PatientPhone ?? string.Empty,
                r.DoctorName ?? string.Empty,
                r.PrescriptionRef ?? string.Empty);
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public string HsnSummaryCsv(HsnSummaryReport report)
    {
        var sb = new StringBuilder();
        AppendRow(sb, "HSN", "GST %", "Taxable", "CGST", "SGST", "Total");
        foreach (HsnSummaryRow r in report.Rows)
        {
            AppendRow(sb, r.HsnCode, Rate(r.GstRate), Money(r.Taxable), Money(r.Cgst), Money(r.Sgst), Money(r.Total));
        }

        HsnSummaryTotals t = report.Totals;
        AppendRow(sb, "TOTAL", string.Empty, Money(t.Taxable), Money(t.Cgst), Money(t.Sgst), Money(t.Total));
        return sb.ToString();
    }

    /// <inheritdoc />
    public byte[] ScheduleRegisterPdf(ReportHeader header, DateTime fromDate, DateTime toDate, IReadOnlyList<ScheduleRegisterRow> rows)
        => new ScheduleRegisterDocument(header, fromDate, toDate, rows).GeneratePdf();

    /// <inheritdoc />
    public byte[] HsnSummaryPdf(ReportHeader header, DateTime fromDate, DateTime toDate, HsnSummaryReport report)
        => new HsnSummaryDocument(header, fromDate, toDate, report).GeneratePdf();

    /// <inheritdoc />
    public string Gstr1Csv(Gstr1Report report)
    {
        var sb = new StringBuilder();

        // Title line naming the period, e.g. "# GSTR-1 — Jun-2026".
        var period = new DateTime(report.Year, report.Month, 1);
        sb.Append("# GSTR-1 — ")
            .Append(period.ToString("MMM-yyyy", CultureInfo.InvariantCulture))
            .Append("\r\n");

        // [b2cs] — one row per rate; Type "OE" (other than e-commerce) per the GSTR-1 offline shape.
        sb.Append("\r\n[b2cs]\r\n");
        AppendRow(sb, "Type", "Place Of Supply", "Rate", "Taxable Value", "CGST", "SGST", "Cess");
        foreach (Gstr1B2csRow r in report.B2cs)
        {
            AppendRow(sb, "OE", r.PlaceOfSupply, Rate(r.GstRate), Money(r.Taxable), Money(r.Cgst), Money(r.Sgst), Money(0m));
        }

        // [hsn] — one row per HSN+rate with UQC + total qty, then a TOTAL footing row.
        sb.Append("\r\n[hsn]\r\n");
        AppendRow(sb, "HSN", "Description", "UQC", "Total Qty", "Rate", "Taxable Value", "CGST", "SGST", "Total");
        foreach (Gstr1HsnRow r in report.Hsn)
        {
            AppendRow(sb, r.HsnCode, r.Description ?? string.Empty, r.Uqc, Qty(r.TotalQty),
                Rate(r.GstRate), Money(r.Taxable), Money(r.Cgst), Money(r.Sgst), Money(r.Total));
        }

        AppendRow(sb, "TOTAL", string.Empty, string.Empty, Qty(report.Hsn.Sum(r => r.TotalQty)), string.Empty,
            Money(report.Hsn.Sum(r => r.Taxable)), Money(report.Hsn.Sum(r => r.Cgst)),
            Money(report.Hsn.Sum(r => r.Sgst)), Money(report.Hsn.Sum(r => r.Total)));

        // [credit-notes] — returns aggregated by rate, then a TOTAL footing row.
        sb.Append("\r\n[credit-notes]\r\n");
        AppendRow(sb, "Rate", "Taxable Value", "CGST", "SGST", "Total");
        foreach (Gstr1CreditNoteRow r in report.CreditNotes)
        {
            AppendRow(sb, Rate(r.GstRate), Money(r.Taxable), Money(r.Cgst), Money(r.Sgst), Money(r.Total));
        }

        AppendRow(sb, "TOTAL",
            Money(report.CreditNotes.Sum(r => r.Taxable)), Money(report.CreditNotes.Sum(r => r.Cgst)),
            Money(report.CreditNotes.Sum(r => r.Sgst)), Money(report.CreditNotes.Sum(r => r.Total)));

        // [docs] — the documents-issued summary line.
        sb.Append("\r\n[docs]\r\n");
        AppendRow(sb, "Nature", "From", "To", "Total Number", "Cancelled");
        AppendRow(sb, "Invoices for outward supply", report.Docs.FromBillNo, report.Docs.ToBillNo,
            report.Docs.Count.ToString(CultureInfo.InvariantCulture),
            report.Docs.Cancelled.ToString(CultureInfo.InvariantCulture));

        return sb.ToString();
    }

    /// <inheritdoc />
    public byte[] Gstr1Pdf(ReportHeader header, int year, int month, Gstr1Report report)
        => new Gstr1Document(header, year, month, report).GeneratePdf();

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Qty(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Rate(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Appends one CSV record, quoting each field per RFC 4180, terminated with CRLF.</summary>
    private static void AppendRow(StringBuilder sb, params string[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Escape(fields[i]));
        }

        sb.Append("\r\n");
    }

    private static string Escape(string field)
    {
        field ??= string.Empty;
        bool mustQuote = field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r');
        if (!mustQuote)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
