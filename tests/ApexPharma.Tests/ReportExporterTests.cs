using System;
using System.Collections.Generic;
using ApexPharma.Application.Services.Reporting;
using ApexPharma.Domain.Enums;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// ReportExporter tests (plan.md §11). Assert the CSV shape (header + rows + footing totals,
/// RFC-4180 quoting for fields with commas/quotes) and that the PDF exports for the Schedule-H
/// register and GST/HSN summary render non-empty bytes. Pure formatting — no database.
/// </summary>
public class ReportExporterTests
{
    private readonly ReportExporter _sut = new();

    [Fact]
    public void SalesReportCsv_HasHeaderRowsAndTotals()
    {
        var report = new SalesReport
        {
            Rows = new[]
            {
                new SalesReportRow
                {
                    BillNo = "INV-000001", BillDate = new DateTime(2026, 6, 1, 10, 30, 0),
                    CustomerName = "Walk-in", PaymentMode = PaymentMode.Cash,
                    Subtotal = 500m, Discount = 0m, Cgst = 30m, Sgst = 30m, Total = 560m, Profit = 200m,
                },
            },
            Summary = new SalesReportSummary
            {
                BillCount = 1, Gross = 560m, Net = 500m, TotalGst = 60m, TotalDiscount = 0m, TotalProfit = 200m,
            },
        };

        string csv = _sut.SalesReportCsv(report);
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("Bill No,Date,Customer,Payment,Subtotal,Discount,CGST,SGST,Total,Profit", lines[0]);
        Assert.Contains("INV-000001", lines[1]);
        Assert.Contains("200.00", lines[1]); // profit
        Assert.StartsWith("TOTAL", lines[2]);
        Assert.Contains("200.00", lines[2]); // total profit foots
    }

    [Fact]
    public void HsnSummaryCsv_HasGroupsAndGrandTotal()
    {
        var report = new HsnSummaryReport
        {
            Rows = new[]
            {
                new HsnSummaryRow { HsnCode = "3004", GstRate = 12m, Taxable = 400m, Cgst = 24m, Sgst = 24m, Total = 448m },
                new HsnSummaryRow { HsnCode = "3003", GstRate = 5m, Taxable = 200m, Cgst = 5m, Sgst = 5m, Total = 210m },
            },
            Totals = new HsnSummaryTotals { Taxable = 600m, Cgst = 29m, Sgst = 29m, Total = 658m },
        };

        string csv = _sut.HsnSummaryCsv(report);
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("HSN,GST %,Taxable,CGST,SGST,Total", lines[0]);
        Assert.Equal(4, lines.Length); // header + 2 rows + totals
        Assert.StartsWith("TOTAL", lines[3]);
        Assert.Contains("658.00", lines[3]);
    }

    [Fact]
    public void Csv_QuotesFieldsContainingCommas()
    {
        var rows = new List<ScheduleRegisterRow>
        {
            new()
            {
                BillDate = new DateTime(2026, 6, 1, 9, 0, 0), BillNo = "INV-000001",
                ProductName = "Cough Syrup, 100ml", Schedule = DrugSchedule.H1, BatchNo = "B1",
                ExpiryDate = new DateTime(2027, 6, 1), Qty = 1m,
                PatientName = "Rao, A.", DoctorName = "Dr. \"Q\"", PrescriptionRef = "RX-1",
            },
        };

        string csv = _sut.ScheduleRegisterCsv(rows);

        Assert.Contains("\"Cough Syrup, 100ml\"", csv); // comma → quoted
        Assert.Contains("\"Rao, A.\"", csv);
        Assert.Contains("\"Dr. \"\"Q\"\"\"", csv);       // inner quotes doubled
    }

    [Fact]
    public void ScheduleRegisterPdf_ProducesNonEmptyBytes()
    {
        var rows = new List<ScheduleRegisterRow>
        {
            new()
            {
                BillDate = DateTime.Today, BillNo = "INV-000001", ProductName = "Azithromycin",
                Schedule = DrugSchedule.H1, BatchNo = "AZ1", ExpiryDate = DateTime.Today.AddYears(1),
                Qty = 2m, PatientName = "Patient A", PatientPhone = "9876543210",
                DoctorName = "Dr. Rao", PrescriptionRef = "RX-100",
            },
        };

        byte[] pdf = _sut.ScheduleRegisterPdf(new ReportHeader { PharmacyName = "Apex-Pharma", SubHeader = "GSTIN: X" },
            DateTime.Today.AddDays(-7), DateTime.Today, rows);

        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 0);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4)); // PDF magic header
    }

    // ---------------- Schedule-X strict register ----------------

    private static ScheduleXRegisterReport SampleScheduleX() => new()
    {
        FromDate = new DateTime(2026, 6, 1),
        ToDate = new DateTime(2026, 6, 30),
        Balances = new[]
        {
            new ScheduleXBalanceRow { ProductId = 1, ProductName = "Morphine", Opening = 65m, Received = 42m, Issued = 17m, Closing = 90m },
            new ScheduleXBalanceRow { ProductId = 2, ProductName = "Fentanyl", Opening = 10m, Received = 0m, Issued = 4m, Closing = 6m },
        },
        Dispenses = new[]
        {
            new ScheduleXDispenseRow
            {
                DispensedAt = new DateTime(2026, 6, 15, 10, 0, 0), ProductName = "Morphine", BatchNo = "MX1", Qty = 2m,
                PatientName = "Anil Kumar", PatientAddress = "12 MG Road, Kolkata", PatientPhone = "9876543210",
                PrescriberName = "Dr. Sen", PrescriberRegNo = "WBMC-12345",
                PrescriptionNumber = "RX-X-777", PrescriptionDate = new DateTime(2026, 6, 14), PrescriptionRetained = true,
            },
        },
    };

    [Fact]
    public void ScheduleXRegisterCsv_HasBothSectionsHeadersAndBalanceFooting()
    {
        string csv = _sut.ScheduleXRegisterCsv(SampleScheduleX());
        string[] lines = csv.Split("\r\n", StringSplitOptions.None);

        // Title + both section markers.
        Assert.Contains("# SCHEDULE X REGISTER — 2026-06-01 to 2026-06-30", csv);
        Assert.Contains("[balances]", csv);
        Assert.Contains("[dispenses]", csv);

        // Section header rows.
        Assert.Contains("Drug,Opening,Received,Issued,Closing", csv);
        Assert.Contains("Date,Drug,Batch,Qty,Patient,Patient Address,Phone,Prescriber,Reg No,Rx No,Rx Date,Retained", csv);

        // Balance footing: TOTAL sums each column (Opening 75, Received 42, Issued 21, Closing 96).
        Assert.Contains(lines, l => l.StartsWith("TOTAL,") && l.Contains("75") && l.Contains("96"));

        // A dispense-detail row with the strict fields + the retained flag.
        Assert.Contains("Morphine", csv);
        Assert.Contains("WBMC-12345", csv);
        Assert.Contains("RX-X-777", csv);
        Assert.Contains(lines, l => l.Contains("Anil Kumar") && l.EndsWith("Yes"));
    }

    [Fact]
    public void ScheduleXRegisterCsv_QuotesEmbeddedCommas()
    {
        string csv = _sut.ScheduleXRegisterCsv(SampleScheduleX());
        // The patient address has a comma → must be RFC-4180 quoted.
        Assert.Contains("\"12 MG Road, Kolkata\"", csv);
    }

    [Fact]
    public void ScheduleXRegisterPdf_ProducesNonEmptyBytes()
    {
        byte[] pdf = _sut.ScheduleXRegisterPdf(
            new ReportHeader { PharmacyName = "Apex-Pharma", SubHeader = "GSTIN: X" }, SampleScheduleX());

        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 0);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void HsnSummaryPdf_ProducesNonEmptyBytes()
    {
        var report = new HsnSummaryReport
        {
            Rows = new[] { new HsnSummaryRow { HsnCode = "3004", GstRate = 12m, Taxable = 400m, Cgst = 24m, Sgst = 24m, Total = 448m } },
            Totals = new HsnSummaryTotals { Taxable = 400m, Cgst = 24m, Sgst = 24m, Total = 448m },
        };

        byte[] pdf = _sut.HsnSummaryPdf(new ReportHeader { PharmacyName = "Apex-Pharma" },
            DateTime.Today.AddDays(-30), DateTime.Today, report);

        Assert.True(pdf.Length > 0);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    // ---------------- GSTR-1 / GST return ----------------

    private static Gstr1Report SampleGstr1() => new()
    {
        Year = 2026,
        Month = 6,
        PlaceOfSupply = "West Bengal",
        B2cs = new[]
        {
            new Gstr1B2csRow { GstRate = 5m, PlaceOfSupply = "West Bengal", Taxable = 200m, Cgst = 5m, Sgst = 5m, Total = 210m },
            new Gstr1B2csRow { GstRate = 12m, PlaceOfSupply = "West Bengal", Taxable = 400m, Cgst = 24m, Sgst = 24m, Total = 448m },
        },
        Hsn = new[]
        {
            new Gstr1HsnRow { HsnCode = "3003", Uqc = "NOS", TotalQty = 4m, GstRate = 5m, Taxable = 200m, Cgst = 5m, Sgst = 5m, Total = 210m },
            new Gstr1HsnRow { HsnCode = "3004", Uqc = "OTH", TotalQty = 3m, GstRate = 12m, Taxable = 400m, Cgst = 24m, Sgst = 24m, Total = 448m },
        },
        CreditNotes = new[]
        {
            new Gstr1CreditNoteRow { GstRate = 12m, Taxable = 100m, Cgst = 6m, Sgst = 6m, Total = 112m },
        },
        Docs = new Gstr1DocsIssued { FromBillNo = "INV-000001", ToBillNo = "INV-000009", Count = 9, Cancelled = 0 },
        Totals = new Gstr1Totals { Taxable = 600m, Cgst = 29m, Sgst = 29m, Total = 658m, BillCount = 9 },
    };

    [Fact]
    public void Gstr1Csv_HasAllSectionMarkersAndFootings()
    {
        string csv = _sut.Gstr1Csv(SampleGstr1());

        // Title line + all four section markers present.
        Assert.Contains("# GSTR-1 — Jun-2026", csv);
        Assert.Contains("[b2cs]", csv);
        Assert.Contains("[hsn]", csv);
        Assert.Contains("[credit-notes]", csv);
        Assert.Contains("[docs]", csv);

        // Section header rows.
        Assert.Contains("Type,Place Of Supply,Rate,Taxable Value,CGST,SGST,Cess", csv);
        Assert.Contains("HSN,Description,UQC,Total Qty,Rate,Taxable Value,CGST,SGST,Total", csv);
        Assert.Contains("Rate,Taxable Value,CGST,SGST,Total", csv);
        Assert.Contains("Nature,From,To,Total Number,Cancelled", csv);

        // B2CS rows carry Type "OE" and the place of supply.
        Assert.Contains("OE,West Bengal,", csv);

        // HSN + credit-note footings present.
        var lines = csv.Split("\r\n", StringSplitOptions.None);
        // HSN footing: TOTAL with the summed taxable 600.00.
        Assert.Contains(lines, l => l.StartsWith("TOTAL,") && l.Contains("600.00"));
        // Credit-notes footing: TOTAL with 100.00 taxable.
        Assert.Contains(lines, l => l.StartsWith("TOTAL,") && l.Contains("100.00"));

        // B2CS footing: the 7-column TOTAL row (Type,POS,Rate,Taxable,CGST,SGST,Cess) uses the
        // report totals — Taxable 600.00, CGST/SGST 29.00, Cess 0.00 — and reconciles to the HSN
        // totals (B2CS == HSN, two views of the same outward supply). Cess column pins it to b2cs.
        var report = SampleGstr1();
        string b2csTotal = $"TOTAL,,,{report.Totals.Taxable:0.00},{report.Totals.Cgst:0.00},{report.Totals.Sgst:0.00},0.00";
        Assert.Contains(b2csTotal, csv);
        // The B2CS TOTAL's taxable/CGST/SGST equal the HSN TOTAL's (machine-checked reconciliation).
        Assert.Equal(report.Hsn.Sum(r => r.Taxable), report.Totals.Taxable);
        Assert.Equal(report.Hsn.Sum(r => r.Cgst), report.Totals.Cgst);
        Assert.Equal(report.Hsn.Sum(r => r.Sgst), report.Totals.Sgst);

        // Docs line.
        Assert.Contains("Invoices for outward supply,INV-000001,INV-000009,9,0", csv);
    }

    [Fact]
    public void Gstr1Csv_EmptyReport_StillEmitsSectionHeaders()
    {
        var empty = new Gstr1Report { Year = 2026, Month = 1, PlaceOfSupply = "WB" };

        string csv = _sut.Gstr1Csv(empty);

        // No crash, and every section marker + header is still present.
        Assert.Contains("# GSTR-1 — Jan-2026", csv);
        Assert.Contains("[b2cs]", csv);
        Assert.Contains("[hsn]", csv);
        Assert.Contains("[credit-notes]", csv);
        Assert.Contains("[docs]", csv);
        Assert.Contains("Type,Place Of Supply,Rate,Taxable Value,CGST,SGST,Cess", csv);
        // Docs line with empty from/to and a zero count.
        Assert.Contains("Invoices for outward supply,,,0,0", csv);
    }

    [Fact]
    public void Gstr1Csv_IsRfc4180Valid_QuotesEmbeddedCommas()
    {
        Gstr1Report sample = SampleGstr1();
        // Place of supply with a comma must be quoted.
        var report = new Gstr1Report
        {
            Year = sample.Year,
            Month = sample.Month,
            PlaceOfSupply = sample.PlaceOfSupply,
            B2cs = new[] { new Gstr1B2csRow { GstRate = 12m, PlaceOfSupply = "Dadra, Nagar Haveli", Taxable = 100m, Cgst = 6m, Sgst = 6m, Total = 112m } },
            Hsn = sample.Hsn,
            CreditNotes = sample.CreditNotes,
            Docs = sample.Docs,
            Totals = sample.Totals,
        };

        string csv = _sut.Gstr1Csv(report);
        Assert.Contains("\"Dadra, Nagar Haveli\"", csv);
    }

    [Fact]
    public void Gstr1Pdf_ProducesNonEmptyBytes()
    {
        byte[] pdf = _sut.Gstr1Pdf(new ReportHeader { PharmacyName = "Apex-Pharma", SubHeader = "GSTIN: X" },
            2026, 6, SampleGstr1());

        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 0);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }
}
