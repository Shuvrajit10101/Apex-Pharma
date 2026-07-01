using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ApexPharma.Application.Services.Reporting;

/// <summary>
/// A QuestPDF A4 document that renders the Schedule H/H1/X register (plan.md §14) as a
/// printable legal register: pharmacy header, date range, then one table row per dispensed
/// scheduled-drug sale line (date, bill, drug, schedule, batch, qty, patient, doctor, Rx).
/// A4 portrait because a register is filed/inspected on paper, unlike the 80mm receipt.
/// </summary>
internal sealed class ScheduleRegisterDocument : IDocument
{
    private readonly ReportHeader _header;
    private readonly DateTime _fromDate;
    private readonly DateTime _toDate;
    private readonly IReadOnlyList<ScheduleRegisterRow> _rows;

    public ScheduleRegisterDocument(ReportHeader header, DateTime fromDate, DateTime toDate, IReadOnlyList<ScheduleRegisterRow> rows)
    {
        _header = header;
        _fromDate = fromDate;
        _toDate = toDate;
        _rows = rows;
    }

    public DocumentMetadata GetMetadata() => new() { Title = "Schedule H/H1/X Register", Author = _header.PharmacyName };

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

                col.Item().PaddingTop(4).AlignCenter().Text("SCHEDULE H / H1 / X REGISTER").FontSize(11).Bold();
                col.Item().AlignCenter().Text(
                    $"Period: {_fromDate:dd-MMM-yyyy} to {_toDate:dd-MMM-yyyy}   ·   {_rows.Count} entr{(_rows.Count == 1 ? "y" : "ies")}");
                col.Item().PaddingVertical(4).LineHorizontal(1);
            });

            page.Content().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(2.2f); // date
                    c.RelativeColumn(1.8f); // bill
                    c.RelativeColumn(3.2f); // drug
                    c.RelativeColumn(1.2f); // schedule
                    c.RelativeColumn(1.8f); // batch
                    c.RelativeColumn(1.0f); // qty
                    c.RelativeColumn(2.8f); // patient
                    c.RelativeColumn(2.6f); // doctor
                    c.RelativeColumn(2.4f); // rx
                });

                table.Header(h =>
                {
                    HeaderCell(h, "Date");
                    HeaderCell(h, "Bill No");
                    HeaderCell(h, "Drug");
                    HeaderCell(h, "Sched");
                    HeaderCell(h, "Batch");
                    HeaderCell(h, "Qty");
                    HeaderCell(h, "Patient");
                    HeaderCell(h, "Doctor");
                    HeaderCell(h, "Rx Ref");
                });

                foreach (ScheduleRegisterRow r in _rows)
                {
                    Cell(table, r.BillDate.ToString("dd-MMM-yy HH:mm", CultureInfo.InvariantCulture));
                    Cell(table, r.BillNo);
                    Cell(table, r.ProductName);
                    Cell(table, r.Schedule.ToString());
                    Cell(table, r.BatchNo);
                    Cell(table, r.Qty.ToString("0.##", CultureInfo.InvariantCulture));
                    Cell(table, PatientText(r));
                    Cell(table, r.DoctorName ?? string.Empty);
                    Cell(table, r.PrescriptionRef ?? string.Empty);
                }
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

    private static string PatientText(ScheduleRegisterRow r)
    {
        if (string.IsNullOrWhiteSpace(r.PatientName))
        {
            return "Walk-in";
        }

        return string.IsNullOrWhiteSpace(r.PatientPhone) ? r.PatientName : $"{r.PatientName} ({r.PatientPhone})";
    }

    private static void HeaderCell(TableCellDescriptor h, string text) =>
        h.Cell().Background(Colors.Grey.Lighten2).Padding(3).Text(text).SemiBold();

    private static void Cell(TableDescriptor table, string text) =>
        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3).Text(text);
}
