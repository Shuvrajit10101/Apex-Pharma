using ApexPharma.Application.Services;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// Encodes plan.md §12's must-pass GST math: the CGST/SGST split and rounding must
/// be correct across the common Indian GST rates (5/12/18%). These are the
/// non-negotiable money tests that guard invoice correctness.
/// </summary>
public class GstServiceTests
{
    private readonly IGstService _sut = new GstService();

    [Theory]
    // taxable, rate, expected CGST, expected SGST, expected total GST, expected gross
    [InlineData(100.00, 5, 2.50, 2.50, 5.00, 105.00)]
    [InlineData(100.00, 12, 6.00, 6.00, 12.00, 112.00)]
    [InlineData(100.00, 18, 9.00, 9.00, 18.00, 118.00)]
    [InlineData(250.00, 5, 6.25, 6.25, 12.50, 262.50)]
    [InlineData(1000.00, 18, 90.00, 90.00, 180.00, 1180.00)]
    public void CalculateLineGst_SplitsAndRoundsCorrectly(
        decimal taxable,
        decimal rate,
        decimal expectedCgst,
        decimal expectedSgst,
        decimal expectedTotalGst,
        decimal expectedGross)
    {
        var result = _sut.CalculateLineGst(taxable, rate);

        Assert.Equal(expectedCgst, result.Cgst);
        Assert.Equal(expectedSgst, result.Sgst);
        Assert.Equal(expectedTotalGst, result.TotalGst);
        Assert.Equal(expectedGross, result.GrossAmount);
    }

    [Fact]
    public void CalculateLineGst_CgstEqualsSgst_ForIntraStateSplit()
    {
        var result = _sut.CalculateLineGst(347.55m, 12m);

        // Intra-state: the two halves must always be equal.
        Assert.Equal(result.Cgst, result.Sgst);
        Assert.Equal(result.Cgst + result.Sgst, result.TotalGst);
    }

    [Theory]
    [InlineData(0, 18)]
    [InlineData(100, 0)]
    public void CalculateLineGst_ZeroInputs_ProduceZeroGst(decimal taxable, decimal rate)
    {
        var result = _sut.CalculateLineGst(taxable, rate);

        Assert.Equal(0m, result.TotalGst);
        Assert.Equal(taxable, result.GrossAmount);
    }

    [Fact]
    public void CalculateLineGst_UsesAwayFromZeroRounding_NotBankers()
    {
        // taxable 33.35 @ 5% -> half = 33.35 * 5 / 2 / 100 = 0.833750 -> rounds to 0.83.
        // Choose a value whose half lands exactly on a .xx5 midpoint to prove
        // AwayFromZero (banker's rounding would round 0.125 -> 0.12).
        // 5.00 @ 5% -> half = 0.125 -> AwayFromZero rounds to 0.13.
        var result = _sut.CalculateLineGst(5.00m, 5m);

        Assert.Equal(0.13m, result.Cgst);
        Assert.Equal(0.13m, result.Sgst);
        Assert.Equal(0.26m, result.TotalGst);
        Assert.Equal(5.26m, result.GrossAmount);
    }
}
