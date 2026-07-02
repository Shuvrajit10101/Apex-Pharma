using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ApexPharma.Application.Services.Reporting;

/// <summary>
/// A QuestPDF A4 document that renders the GSTR-1 / GST-return export (plan.md §11) the owner
/// hands to the accountant for filing: pharmacy header (with GSTIN), the return period, then the
/// B2CS (by rate), HSN (by HSN+rate, with UQC + qty), and credit-notes (returns, by rate) tables,
/// and a documents-issued line. Cloned from <see cref="HsnSummaryDocument"/> so the header/table
/// styling stays byte-consistent with the other legal/accounting exports.
/// </summary>
internal sealed class Gstr1Document : IDocument
{
    private readonly ReportHeader _header;
    private readonly int _year;
    private readonly int _month;
    private readonly Gstr1Report _report;

    public Gstr1Document(ReportHeader header, int year, int month, Gstr1Report report)
    {
        _header = header;
        _year = year;
        _month = month;
        _report = report;
    }

    public DocumentMetadata GetMetadata() => new() { Title = "GSTR-1 / GST Return", Author = _header.PharmacyName };

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

                col.Item().PaddingTop(4).AlignCenter().Text("GSTR-1 / GST RETURN").FontSize(11).Bold();
                var period = new DateTime(_year, _month, 1);
                col.Item().AlignCenter().Text(
                    $"Period: {period:MMM-yyyy}   ·   Place of supply: {PlaceOfSupply()}");
                col.Item().PaddingVertical(4).LineHorizontal(1);
            });

            page.Content().PaddingTop(4).Column(col =>
            {
                col.Spacing(10);

                // --- B2CS (business-to-consumer, by rate) ---
                col.Item().Text("B2CS — outward supplies to consumers (by rate)").SemiBold();
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(3f);  // Place of supply
                        c.RelativeColumn(2f);  // Rate
                        c.RelativeColumn(3f);  // Taxable
                        c.RelativeColumn(3f);  // CGST
                        c.RelativeColumn(3f);  // SGST
                    });

                    table.Header(h =>
                    {
                        HeaderCell(h, "Place of Supply", left: true);
                        HeaderCell(h, "Rate");
                        HeaderCell(h, "Taxable");
                        HeaderCell(h, "CGST");
                        HeaderCell(h, "SGST");
                    });

                    foreach (Gstr1B2csRow r in _report.B2cs)
                    {
                        LeftCell(table, r.PlaceOfSupply);
                        RightCell(table, Rate(r.GstRate));
                        RightCell(table, Money(r.Taxable));
                        RightCell(table, Money(r.Cgst));
                        RightCell(table, Money(r.Sgst));
                    }

                    TotalCell(table, "TOTAL", left: true);
                    TotalCell(table, string.Empty);
                    TotalCell(table, Money(_report.Totals.Taxable));
                    TotalCell(table, Money(_report.Totals.Cgst));
                    TotalCell(table, Money(_report.Totals.Sgst));
                });

                // --- HSN summary ---
                col.Item().Text("HSN summary (by HSN + rate)").SemiBold();
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(3f);  // HSN
                        c.RelativeColumn(1.5f); // UQC
                        c.RelativeColumn(2f);  // Qty
                        c.RelativeColumn(2f);  // Rate
                        c.RelativeColumn(3f);  // Taxable
                        c.RelativeColumn(3f);  // CGST
                        c.RelativeColumn(3f);  // SGST
                        c.RelativeColumn(3f);  // Total
                    });

                    table.Header(h =>
                    {
                        HeaderCell(h, "HSN", left: true);
                        HeaderCell(h, "UQC");
                        HeaderCell(h, "Qty");
                        HeaderCell(h, "Rate");
                        HeaderCell(h, "Taxable");
                        HeaderCell(h, "CGST");
                        HeaderCell(h, "SGST");
                        HeaderCell(h, "Total");
                    });

                    foreach (Gstr1HsnRow r in _report.Hsn)
                    {
                        LeftCell(table, r.HsnCode);
                        LeftCell(table, r.Uqc);
                        RightCell(table, Qty(r.TotalQty));
                        RightCell(table, Rate(r.GstRate));
                        RightCell(table, Money(r.Taxable));
                        RightCell(table, Money(r.Cgst));
                        RightCell(table, Money(r.Sgst));
                        RightCell(table, Money(r.Total));
                    }

                    TotalCell(table, "TOTAL", left: true);
                    TotalCell(table, string.Empty);
                    TotalCell(table, Qty(_report.Hsn.Sum(r => r.TotalQty)));
                    TotalCell(table, string.Empty);
                    TotalCell(table, Money(_report.Hsn.Sum(r => r.Taxable)));
                    TotalCell(table, Money(_report.Hsn.Sum(r => r.Cgst)));
                    TotalCell(table, Money(_report.Hsn.Sum(r => r.Sgst)));
                    TotalCell(table, Money(_report.Hsn.Sum(r => r.Total)));
                });

                // --- Credit notes (returns) ---
                col.Item().Text("Credit notes — sales returns (by rate)").SemiBold();
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2f);  // Rate
                        c.RelativeColumn(3f);  // Taxable
                        c.RelativeColumn(3f);  // CGST
                        c.RelativeColumn(3f);  // SGST
                        c.RelativeColumn(3f);  // Total
                    });

                    table.Header(h =>
                    {
                        HeaderCell(h, "Rate", left: true);
                        HeaderCell(h, "Taxable");
                        HeaderCell(h, "CGST");
                        HeaderCell(h, "SGST");
                        HeaderCell(h, "Total");
                    });

                    foreach (Gstr1CreditNoteRow r in _report.CreditNotes)
                    {
                        LeftCell(table, Rate(r.GstRate));
                        RightCell(table, Money(r.Taxable));
                        RightCell(table, Money(r.Cgst));
                        RightCell(table, Money(r.Sgst));
                        RightCell(table, Money(r.Total));
                    }

                    TotalCell(table, "TOTAL", left: true);
                    TotalCell(table, Money(_report.CreditNotes.Sum(r => r.Taxable)));
                    TotalCell(table, Money(_report.CreditNotes.Sum(r => r.Cgst)));
                    TotalCell(table, Money(_report.CreditNotes.Sum(r => r.Sgst)));
                    TotalCell(table, Money(_report.CreditNotes.Sum(r => r.Total)));
                });

                // --- Documents issued ---
                col.Item().Text("Documents issued").SemiBold();
                col.Item().Text(
                    $"Invoices for outward supply — From {DocOrDash(_report.Docs.FromBillNo)} " +
                    $"To {DocOrDash(_report.Docs.ToBillNo)}   ·   Total: {_report.Docs.Count}   ·   " +
                    $"Cancelled: {_report.Docs.Cancelled}");
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

    private string PlaceOfSupply() =>
        string.IsNullOrWhiteSpace(_report.PlaceOfSupply) ? "—" : _report.PlaceOfSupply;

    private static string DocOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Qty(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

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
