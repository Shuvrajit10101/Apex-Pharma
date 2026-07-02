using System.Globalization;
using ApexPharma.Application.Services.Reporting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ApexPharma.Application.Services.Ledger;

/// <summary>
/// A QuestPDF A4 document that renders a party (customer/supplier) statement (plan.md §3, §11) as
/// a printable ledger: pharmacy header, party name + date range, then one table row per statement
/// row (date, type, reference, debit, credit, running balance), with opening and closing balances.
/// A4 portrait because the owner/accountant files and reviews it on paper (mirrors
/// <see cref="ScheduleRegisterDocument"/>).
/// </summary>
internal sealed class PartyStatementDocument : IDocument
{
    private readonly ReportHeader _header;
    private readonly PartyStatement _statement;

    public PartyStatementDocument(ReportHeader header, PartyStatement statement)
    {
        _header = header;
        _statement = statement;
    }

    public DocumentMetadata GetMetadata() => new() { Title = "Party Statement", Author = _header.PharmacyName };

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

                col.Item().PaddingTop(4).AlignCenter().Text("ACCOUNT STATEMENT").FontSize(11).Bold();
                col.Item().AlignCenter().Text(_statement.PartyName).FontSize(10).SemiBold();
                col.Item().AlignCenter().Text(
                    $"Period: {_statement.FromDate:dd-MMM-yyyy} to {_statement.ToDate:dd-MMM-yyyy}");
                col.Item().PaddingVertical(4).LineHorizontal(1);
            });

            page.Content().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(2.0f); // date
                    c.RelativeColumn(2.4f); // type
                    c.RelativeColumn(2.4f); // reference
                    c.RelativeColumn(1.6f); // debit
                    c.RelativeColumn(1.6f); // credit
                    c.RelativeColumn(1.8f); // balance
                });

                table.Header(h =>
                {
                    HeaderCell(h, "Date");
                    HeaderCell(h, "Type");
                    HeaderCell(h, "Reference");
                    HeaderCellRight(h, "Debit");
                    HeaderCellRight(h, "Credit");
                    HeaderCellRight(h, "Balance");
                });

                foreach (PartyStatementRow r in _statement.Rows)
                {
                    Cell(table, r.Date.ToString("dd-MMM-yy", CultureInfo.InvariantCulture));
                    Cell(table, r.DocType);
                    Cell(table, r.RefNo);
                    CellRight(table, r.Debit == 0m ? string.Empty : Money(r.Debit));
                    CellRight(table, r.Credit == 0m ? string.Empty : Money(r.Credit));
                    CellRight(table, Money(r.RunningBalance));
                }
            });

            page.Footer().Column(col =>
            {
                col.Item().PaddingTop(4).AlignRight().Text(
                    $"Closing balance: {Money(_statement.ClosingBalance)}").SemiBold();
                col.Item().AlignRight().Text(t =>
                {
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        });
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static void HeaderCell(TableCellDescriptor h, string text) =>
        h.Cell().Background(Colors.Grey.Lighten2).Padding(3).Text(text).SemiBold();

    private static void HeaderCellRight(TableCellDescriptor h, string text) =>
        h.Cell().Background(Colors.Grey.Lighten2).Padding(3).AlignRight().Text(text).SemiBold();

    private static void Cell(TableDescriptor table, string text) =>
        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(text);

    private static void CellRight(TableDescriptor table, string text) =>
        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).AlignRight().Text(text);
}
