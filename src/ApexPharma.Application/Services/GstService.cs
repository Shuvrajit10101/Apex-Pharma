namespace ApexPharma.Application.Services;

/// <summary>
/// Reference GST implementation (plan.md §12). For an <b>intra-state</b> sale the
/// GST rate is split evenly between CGST and SGST. Each half is rounded to 2 decimal
/// places using <see cref="MidpointRounding.AwayFromZero"/> (commercial rounding) —
/// deliberately NOT banker's rounding — so totals match hand-calculated invoices and
/// the accountant's figures.
/// </summary>
/// <remarks>
/// <b>Future case — inter-state (IGST):</b> when the place of supply is a different
/// state, GST is charged as a single IGST line (equal to the full rate) instead of
/// the CGST+SGST split handled here. That path will be added when multi-state supply
/// is in scope (plan.md §6.1 supplier state_code; out of v1 core).
/// </remarks>
public class GstService : IGstService
{
    /// <inheritdoc />
    public GstResult CalculateLineGst(decimal taxableAmount, decimal gstRatePercent)
    {
        // Half the rate applies to CGST and half to SGST (intra-state split).
        // Round each half independently so CGST and SGST are each valid 2-dp money
        // values; TotalGst is their sum (this matches how invoices are printed).
        decimal half = Math.Round(
            taxableAmount * gstRatePercent / 2m / 100m,
            2,
            MidpointRounding.AwayFromZero);

        decimal cgst = half;
        decimal sgst = half;
        decimal totalGst = cgst + sgst;
        decimal gross = taxableAmount + totalGst;

        return new GstResult(cgst, sgst, totalGst, gross);
    }
}
