using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ApexPharma.Application.Services.Reporting;

/// <summary>
/// A QuestPDF A4 document that renders the GST/HSN summary (plan.md §11 — GSTR-1) the owner
/// hands to the accountant: pharmacy header (with GSTIN), date range, one row per HSN + GST
/// rate (taxable, CGST, SGST, total), and a grand-total footer.
/// </summary>
internal sealed class HsnSummaryDocument : IDocument
{
    private readonly ReportHeader _header;
    private readonly DateTime _fromDate;
    private readonly DateTime _toDate;
    private readonly HsnSummaryReport _report;

    public HsnSummaryDocument(ReportHeader header, DateTime fromDate, DateTime toDate, HsnSummaryReport report)
    {
        _header = header;
        _fromDate = fromDate;
        _toDate = toDate;
        _report = report;
    }

    public DocumentMetadata GetMetadata() => new() { Title = "GST / HSN Summary", Author = _header.PharmacyName };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.5f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(9).FontFamily(Fonts.Calibri));

            page.Header().Column(col =>
            {
                col.Item().AlignCenter().Text(_header.PharmacyName).FontSize(14).Bold();
                if (!string.IsNullOrWhiteSpace(_header.SubHeader))
                {
                    col.Item().AlignCenter().Text(_header.SubHeader).FontSize(8);
                }

                col.Item().PaddingTop(4).AlignCenter().Text("GST / HSN SUMMARY (GSTR-1)").FontSize(11).Bold();
                col.Item().AlignCenter().Text($"Period: {_fromDate:dd-MMM-yyyy} to {_toDate:dd-MMM-yyyy}");
                col.Item().PaddingVertical(4).LineHorizontal(1);
            });

            page.Content().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(3f);   // HSN
                    c.RelativeColumn(2f);   // GST%
                    c.RelativeColumn(3f);   // Taxable
                    c.RelativeColumn(3f);   // CGST
                    c.RelativeColumn(3f);   // SGST
                    c.RelativeColumn(3f);   // Total
                });

                table.Header(h =>
                {
                    HeaderCell(h, "HSN", left: true);
                    HeaderCell(h, "GST %");
                    HeaderCell(h, "Taxable");
                    HeaderCell(h, "CGST");
                    HeaderCell(h, "SGST");
                    HeaderCell(h, "Total");
                });

                foreach (HsnSummaryRow r in _report.Rows)
                {
                    LeftCell(table, r.HsnCode);
                    RightCell(table, Rate(r.GstRate));
                    RightCell(table, Money(r.Taxable));
                    RightCell(table, Money(r.Cgst));
                    RightCell(table, Money(r.Sgst));
                    RightCell(table, Money(r.Total));
                }

                // Grand-total footing row.
                TotalCell(table, "TOTAL", left: true);
                TotalCell(table, string.Empty);
                TotalCell(table, Money(_report.Totals.Taxable));
                TotalCell(table, Money(_report.Totals.Cgst));
                TotalCell(table, Money(_report.Totals.Sgst));
                TotalCell(table, Money(_report.Totals.Total));
            });

            page.Footer().AlignRight().Text(t =>
            {
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        });
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Rate(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void HeaderCell(TableCellDescriptor h, string text, bool left = false)
    {
        var cell = h.Cell().Background(Colors.Grey.Lighten2).Padding(3);
        (left ? cell.Text(text) : cell.AlignRight().Text(text)).SemiBold();
    }

    private static void LeftCell(TableDescriptor table, string text) =>
        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(text);

    private static void RightCell(TableDescriptor table, string text) =>
        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).AlignRight().Text(text);

    private static void TotalCell(TableDescriptor table, string text, bool left = false)
    {
        var cell = table.Cell().BorderTop(1f).BorderColor(Colors.Grey.Medium).Padding(3);
        (left ? cell.Text(text) : cell.AlignRight().Text(text)).Bold();
    }
}
