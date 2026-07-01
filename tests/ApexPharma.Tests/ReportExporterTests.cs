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
}
