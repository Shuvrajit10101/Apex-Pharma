using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ApexPharma.Application.Services.Reporting;

/// <summary>
/// A QuestPDF A4 document that renders the strict Schedule-X register (plan.md §14, §15 — Phase 2f)
/// as a printable legal narcotic ledger: pharmacy header, date window, a per-drug running-balance
/// table (opening / received / issued / closing, all derived), then the dispense-detail table (one
/// row per dispense: date, drug, batch, qty, patient name+address, prescriber name+reg-no, Rx
/// number+date, retained-copy flag). A4 portrait because a register is filed/inspected on paper.
/// Modeled on <see cref="ScheduleRegisterDocument"/>.
/// </summary>
internal sealed class ScheduleXRegisterDocument : IDocument
{
    private readonly ReportHeader _header;
    private readonly ScheduleXRegisterReport _report;

    public ScheduleXRegisterDocument(ReportHeader header, ScheduleXRegisterReport report)
    {
        _header = header;
        _report = report;
    }

    public DocumentMetadata GetMetadata() => new() { Title = "Schedule X Register", Author = _header.PharmacyName };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.5f, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(8).FontFamily(Fonts.Calibri));

            page.Header().Column(col =>
            {
                col.Item().AlignCenter().Text(_header.PharmacyName).FontSize(14).Bold();
                if (!string.IsNullOrWhiteSpace(_header.SubHeader))
                {
                    col.Item().AlignCenter().Text(_header.SubHeader).FontSize(8);
                }

                col.Item().PaddingTop(4).AlignCenter().Text("SCHEDULE X REGISTER (STRICT)").FontSize(11).Bold();
                col.Item().AlignCenter().Text(
                    $"Period: {_report.FromDate:dd-MMM-yyyy} to {_report.ToDate:dd-MMM-yyyy}   ·   " +
                    $"{_report.Dispenses.Count} dispense{(_report.Dispenses.Count == 1 ? string.Empty : "s")}");
                col.Item().PaddingVertical(4).LineHorizontal(1);
            });

            page.Content().Column(col =>
            {
                col.Item().PaddingBottom(2).Text("Running balance").SemiBold().FontSize(9);
                col.Item().Table(BalancesTable);

                col.Item().PaddingTop(10).PaddingBottom(2).Text("Dispense detail").SemiBold().FontSize(9);
                col.Item().Table(DispensesTable);
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

    private void BalancesTable(TableDescriptor table)
    {
        table.ColumnsDefinition(c =>
        {
            c.RelativeColumn(3.0f); // drug
            c.RelativeColumn(1.2f); // opening
            c.RelativeColumn(1.2f); // received
            c.RelativeColumn(1.2f); // issued
            c.RelativeColumn(1.2f); // closing
        });

        table.Header(h =>
        {
            HeaderCell(h, "Drug");
            HeaderCell(h, "Opening");
            HeaderCell(h, "Received");
            HeaderCell(h, "Issued");
            HeaderCell(h, "Closing");
        });

        foreach (ScheduleXBalanceRow r in _report.Balances)
        {
            Cell(table, r.ProductName);
            Cell(table, Qty(r.Opening));
            Cell(table, Qty(r.Received));
            Cell(table, Qty(r.Issued));
            Cell(table, Qty(r.Closing));
        }

        // TOTAL footing so the balances foot on their own.
        FootCell(table, "TOTAL");
        FootCell(table, Qty(_report.Balances.Sum(r => r.Opening)));
        FootCell(table, Qty(_report.Balances.Sum(r => r.Received)));
        FootCell(table, Qty(_report.Balances.Sum(r => r.Issued)));
        FootCell(table, Qty(_report.Balances.Sum(r => r.Closing)));
    }

    private void DispensesTable(TableDescriptor table)
    {
        table.ColumnsDefinition(c =>
        {
            c.RelativeColumn(1.9f); // date
            c.RelativeColumn(2.0f); // drug
            c.RelativeColumn(1.3f); // batch
            c.RelativeColumn(0.8f); // qty
            c.RelativeColumn(2.6f); // patient (name + address)
            c.RelativeColumn(2.4f); // prescriber (name + reg)
            c.RelativeColumn(1.9f); // rx (no + date)
            c.RelativeColumn(1.0f); // retained
        });

        table.Header(h =>
        {
            HeaderCell(h, "Date");
            HeaderCell(h, "Drug");
            HeaderCell(h, "Batch");
            HeaderCell(h, "Qty");
            HeaderCell(h, "Patient");
            HeaderCell(h, "Prescriber");
            HeaderCell(h, "Rx");
            HeaderCell(h, "Retained");
        });

        foreach (ScheduleXDispenseRow r in _report.Dispenses)
        {
            Cell(table, r.DispensedAt.ToString("dd-MMM-yy HH:mm", CultureInfo.InvariantCulture));
            Cell(table, r.ProductName);
            Cell(table, r.BatchNo);
            Cell(table, Qty(r.Qty));
            Cell(table, PatientText(r));
            Cell(table, PrescriberText(r));
            Cell(table, RxText(r));
            Cell(table, r.PrescriptionRetained ? "Yes" : "No");
        }
    }

    private static string PatientText(ScheduleXDispenseRow r)
    {
        string phone = string.IsNullOrWhiteSpace(r.PatientPhone) ? string.Empty : $" · {r.PatientPhone}";
        return $"{r.PatientName}\n{r.PatientAddress}{phone}";
    }

    private static string PrescriberText(ScheduleXDispenseRow r)
        => $"{r.PrescriberName}\nReg: {r.PrescriberRegNo}";

    private static string RxText(ScheduleXDispenseRow r)
        => $"{r.PrescriptionNumber}\n{r.PrescriptionDate:dd-MMM-yy}";

    private static string Qty(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void HeaderCell(TableCellDescriptor h, string text) =>
        h.Cell().Background(Colors.Grey.Lighten2).Padding(3).Text(text).SemiBold();

    private static void Cell(TableDescriptor table, string text) =>
        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(text);

    private static void FootCell(TableDescriptor table, string text) =>
        table.Cell().BorderTop(1f).BorderColor(Colors.Grey.Medium).Padding(3).Text(text).SemiBold();
}
