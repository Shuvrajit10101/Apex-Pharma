using System;
using System.Collections.Generic;
using ApexPharma.Application.Services.Ledger;
using ApexPharma.Application.Services.Reporting;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// LedgerExporter tests (plan.md §3, §11). Assert the party-statement CSV shape (header + a row per
/// statement row including the opening line + a trailing closing-balance row, RFC-4180 quoting) and
/// that the A4 PDF renders non-empty bytes. Pure formatting — no database.
/// </summary>
public class LedgerExporterTests
{
    private readonly LedgerExporter _sut = new();

    private static PartyStatement SampleStatement() => new(
        PartyName: "Ravi, Kumar", // comma forces RFC-4180 quoting
        OpeningBalance: 100m,
        Rows: new List<PartyStatementRow>
        {
            new(new DateTime(2026, 6, 1), "Opening balance", string.Empty, 0m, 0m, 100m),
            new(new DateTime(2026, 6, 5), "Credit sale", "INV-000007", 224m, 0m, 324m),
            new(new DateTime(2026, 6, 10), "Receipt", "RCPT-3", 0m, 100m, 224m),
        },
        ClosingBalance: 224m,
        FromDate: new DateTime(2026, 6, 1),
        ToDate: new DateTime(2026, 6, 30));

    [Fact]
    public void PartyStatementCsv_HasHeaderRowsOpeningAndClosing()
    {
        string csv = _sut.PartyStatementCsv(SampleStatement());
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Header
        Assert.Equal("Date,Type,Reference,Debit,Credit,Balance", lines[0]);
        // Opening row present
        Assert.Contains("Opening balance", lines[1]);
        Assert.Contains("100.00", lines[1]);
        // A transaction row with its money formatted to 2dp
        Assert.Contains("Credit sale", lines[2]);
        Assert.Contains("224.00", lines[2]);
        Assert.Contains("INV-000007", lines[2]);
        // Trailing closing-balance row foots the statement
        Assert.StartsWith("2026-06-30,Closing balance", lines[^1]);
        Assert.EndsWith("224.00", lines[^1]);
    }

    [Fact]
    public void PartyStatementCsv_QuotesFieldsWithCommas()
    {
        // The party name has no column of its own, but a DocType/RefNo with a comma must be quoted.
        var stmt = new PartyStatement(
            "Acme", 0m,
            new List<PartyStatementRow>
            {
                new(new DateTime(2026, 6, 1), "Opening balance", string.Empty, 0m, 0m, 0m),
                new(new DateTime(2026, 6, 2), "Credit sale", "INV-1, revised", 50m, 0m, 50m),
            },
            50m, new DateTime(2026, 6, 1), new DateTime(2026, 6, 30));

        string csv = _sut.PartyStatementCsv(stmt);

        Assert.Contains("\"INV-1, revised\"", csv); // RFC-4180: comma field is wrapped in quotes
    }

    [Fact]
    public void PartyStatementPdf_RendersNonEmptyBytes()
    {
        var header = new ReportHeader { PharmacyName = "Apex-Pharma", SubHeader = "GSTIN: 22AAAAA0000A1Z5" };

        byte[] pdf = _sut.PartyStatementPdf(header, SampleStatement());

        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 0);
        // PDF magic header "%PDF".
        Assert.Equal(0x25, pdf[0]);
        Assert.Equal(0x50, pdf[1]);
        Assert.Equal(0x44, pdf[2]);
        Assert.Equal(0x46, pdf[3]);
    }
}
