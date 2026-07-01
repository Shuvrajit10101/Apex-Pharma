using System.Globalization;
using ApexPharma.Domain.Enums;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ApexPharma.Application.Services.Invoicing;

/// <summary>
/// A QuestPDF document that renders an <see cref="InvoiceModel"/> as an 80mm (3-inch) thermal
/// GST receipt (plan.md §11, §14 — client is thermal-receipt-first). Continuous-roll width, no
/// fixed page height, small fonts. The header carries the pharmacy name/address/GSTIN/DL/phone;
/// the body lists each item (name, batch, expiry, qty×rate, amount); a CGST/SGST tax summary
/// groups by GST rate; then the money roll-up, payment mode, the Schedule-H doctor/Rx note when
/// applicable, and the footer/terms. The A4/A5 layout the client deferred can be added as a
/// sibling document over the same model without touching this one.
/// </summary>
internal sealed class ThermalReceiptDocument : IDocument
{
    // 80mm thermal roll. QuestPDF uses points (1/72 inch); 80mm ≈ 226.77pt. We leave a small
    // margin so nothing is clipped by the print head's non-printable edge.
    private const float RollWidthMm = 80f;

    private readonly InvoiceModel _model;

    public ThermalReceiptDocument(InvoiceModel model) => _model = model;

    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"Invoice {_model.BillNo}",
        Author = _model.PharmacyName,
    };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            // Continuous roll: fixed width, height grows with content.
            page.ContinuousSize(RollWidthMm, Unit.Millimetre);
            page.Margin(4, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontSize(7.5f).FontFamily(Fonts.Calibri));

            page.Content().Column(col =>
            {
                col.Spacing(4);
                ComposeHeader(col);
                ComposeBillMeta(col);
                ComposeLines(col);
                ComposeTaxSummary(col);
                ComposeTotals(col);
                ComposeSchedule(col);
                ComposeFooter(col);
            });
        });
    }

    private void ComposeHeader(ColumnDescriptor col)
    {
        col.Item().AlignCenter().Text(_model.PharmacyName)
            .FontSize(11).Bold();

        if (!string.IsNullOrWhiteSpace(_model.AddressLine))
        {
            col.Item().AlignCenter().Text(_model.AddressLine);
        }

        if (!string.IsNullOrWhiteSpace(_model.CityState))
        {
            col.Item().AlignCenter().Text(_model.CityState);
        }

        if (!string.IsNullOrWhiteSpace(_model.Phone))
        {
            col.Item().AlignCenter().Text($"Ph: {_model.Phone}");
        }

        if (!string.IsNullOrWhiteSpace(_model.Gstin))
        {
            col.Item().AlignCenter().Text($"GSTIN: {_model.Gstin}").Bold();
        }

        if (!string.IsNullOrWhiteSpace(_model.DlNumber))
        {
            col.Item().AlignCenter().Text($"D.L. No: {_model.DlNumber}");
        }

        col.Item().PaddingVertical(2).LineHorizontal(0.5f);
        col.Item().AlignCenter().Text("GST INVOICE / TAX INVOICE").Bold().FontSize(8.5f);
    }

    private void ComposeBillMeta(ColumnDescriptor col)
    {
        col.Item().Row(row =>
        {
            row.RelativeItem().Text($"Bill: {_model.BillNo}").Bold();
            row.RelativeItem().AlignRight().Text(_model.BillDate.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture));
        });

        if (!string.IsNullOrWhiteSpace(_model.CashierName))
        {
            col.Item().Text($"Cashier: {_model.CashierName}");
        }

        if (!string.IsNullOrWhiteSpace(_model.CustomerName))
        {
            string customer = _model.CustomerName!;
            if (!string.IsNullOrWhiteSpace(_model.CustomerPhone))
            {
                customer += $" ({_model.CustomerPhone})";
            }

            col.Item().Text($"Customer: {customer}");
        }

        col.Item().LineHorizontal(0.5f);
    }

    private void ComposeLines(ColumnDescriptor col)
    {
        // Column header for the item grid.
        col.Item().Row(row =>
        {
            row.RelativeItem(5).Text("Item").SemiBold();
            row.RelativeItem(2).AlignRight().Text("Qty").SemiBold();
            row.RelativeItem(3).AlignRight().Text("Rate").SemiBold();
            row.RelativeItem(3).AlignRight().Text("Amount").SemiBold();
        });
        col.Item().LineHorizontal(0.5f);

        foreach (InvoiceLine line in _model.Lines)
        {
            col.Item().Row(row =>
            {
                row.RelativeItem(5).Text(line.ProductName);
                row.RelativeItem(2).AlignRight().Text(Qty(line.Qty));
                row.RelativeItem(3).AlignRight().Text(Money(line.Rate));
                row.RelativeItem(3).AlignRight().Text(Money(line.Amount));
            });

            // Batch / expiry / GST% sub-line in a lighter style (compliance + traceability).
            string sub = $"  Batch {line.BatchNo}  Exp {line.Expiry:MM/yy}  GST {Rate(line.GstRate)}%";
            if (line.Discount > 0)
            {
                sub += $"  Disc {Money(line.Discount)}";
            }

            col.Item().Text(sub).FontSize(6.5f).FontColor(Colors.Grey.Darken1);
        }

        col.Item().LineHorizontal(0.5f);
    }

    private void ComposeTaxSummary(ColumnDescriptor col)
    {
        if (_model.TaxSummary.Count == 0)
        {
            return;
        }

        col.Item().Text("Tax Summary (CGST / SGST)").SemiBold();
        col.Item().Row(row =>
        {
            row.RelativeItem(3).Text("GST%").FontSize(6.5f);
            row.RelativeItem(4).AlignRight().Text("Taxable").FontSize(6.5f);
            row.RelativeItem(4).AlignRight().Text("CGST").FontSize(6.5f);
            row.RelativeItem(4).AlignRight().Text("SGST").FontSize(6.5f);
        });

        foreach (InvoiceTaxSummaryRow tax in _model.TaxSummary)
        {
            col.Item().Row(row =>
            {
                row.RelativeItem(3).Text(Rate(tax.GstRate));
                row.RelativeItem(4).AlignRight().Text(Money(tax.Taxable));
                row.RelativeItem(4).AlignRight().Text(Money(tax.Cgst));
                row.RelativeItem(4).AlignRight().Text(Money(tax.Sgst));
            });
        }

        col.Item().LineHorizontal(0.5f);
    }

    private void ComposeTotals(ColumnDescriptor col)
    {
        AmountRow(col, "Subtotal", _model.Subtotal);
        if (_model.Discount > 0)
        {
            AmountRow(col, "Discount", _model.Discount);
        }

        AmountRow(col, "CGST", _model.Cgst);
        AmountRow(col, "SGST", _model.Sgst);
        if (_model.RoundOff != 0)
        {
            AmountRow(col, "Round Off", _model.RoundOff);
        }

        col.Item().LineHorizontal(1f);
        col.Item().Row(row =>
        {
            row.RelativeItem().Text("TOTAL").Bold().FontSize(10);
            row.RelativeItem().AlignRight().Text($"Rs {Money(_model.Total)}").Bold().FontSize(10);
        });
        col.Item().Text($"Payment: {_model.PaymentMode}").FontSize(7);
        col.Item().Text($"Items: {Qty(_model.TotalQty)}").FontSize(7);
    }

    private void ComposeSchedule(ColumnDescriptor col)
    {
        if (!_model.HasScheduledDrug)
        {
            return;
        }

        col.Item().PaddingTop(2).LineHorizontal(0.5f);
        col.Item().Text("Schedule H/H1 drug dispensed on prescription").SemiBold().FontSize(7);
        if (!string.IsNullOrWhiteSpace(_model.DoctorName))
        {
            col.Item().Text($"Doctor: {_model.DoctorName}").FontSize(7);
        }

        if (!string.IsNullOrWhiteSpace(_model.PrescriptionRef))
        {
            col.Item().Text($"Rx Ref: {_model.PrescriptionRef}").FontSize(7);
        }
    }

    private void ComposeFooter(ColumnDescriptor col)
    {
        col.Item().PaddingTop(3).LineHorizontal(0.5f);
        if (!string.IsNullOrWhiteSpace(_model.Footer))
        {
            col.Item().AlignCenter().Text(_model.Footer).FontSize(7).Italic();
        }

        col.Item().AlignCenter().Text("*** Thank you ***").FontSize(7);
    }

    private static void AmountRow(ColumnDescriptor col, string label, decimal value)
    {
        col.Item().Row(row =>
        {
            row.RelativeItem().Text(label);
            row.RelativeItem().AlignRight().Text(Money(value));
        });
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // Quantities and rates are typically whole/2-dp; trim trailing zeros for compactness.
    private static string Qty(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Rate(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
