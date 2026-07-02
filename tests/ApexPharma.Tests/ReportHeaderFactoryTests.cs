using ApexPharma.Application.Services.Reporting;
using ApexPharma.Application.Services.Settings;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// Unit tests for <see cref="ReportHeaderFactory.From"/> (plan.md §14) — the single source of truth
/// for the printed name-fallback and the GSTIN/D.L. sub-header composition shared by the Reports hub
/// and both party ledgers. Guards the exact separator/format so every export stays byte-identical.
/// </summary>
public class ReportHeaderFactoryTests
{
    [Fact]
    public void From_ComposesNameAndSubHeader_WithBothIdentifiers()
    {
        var profile = new PharmacyProfile { PharmacyName = "Ravi Medicals", Gstin = "22AAAAA0000A1Z5", DlNumber = "20B/21B" };

        ReportHeader header = ReportHeaderFactory.From(profile);

        Assert.Equal("Ravi Medicals", header.PharmacyName);
        Assert.Equal("GSTIN: 22AAAAA0000A1Z5  ·  D.L. No: 20B/21B", header.SubHeader);
    }

    [Fact]
    public void From_BlankName_FallsBackToApexPharma()
    {
        var profile = new PharmacyProfile { PharmacyName = "   " };

        ReportHeader header = ReportHeaderFactory.From(profile);

        Assert.Equal("Apex-Pharma", header.PharmacyName);
    }

    [Fact]
    public void From_OnlyGstin_OmitsDlSegment()
    {
        var profile = new PharmacyProfile { PharmacyName = "X", Gstin = "22AAAAA0000A1Z5" };

        ReportHeader header = ReportHeaderFactory.From(profile);

        Assert.Equal("GSTIN: 22AAAAA0000A1Z5", header.SubHeader);
    }

    [Fact]
    public void From_NoIdentifiers_HasNullSubHeader()
    {
        var profile = new PharmacyProfile { PharmacyName = "X" };

        ReportHeader header = ReportHeaderFactory.From(profile);

        Assert.Null(header.SubHeader);
    }
}
