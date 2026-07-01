namespace ApexPharma.Application.Services;

/// <summary>
/// GST calculation for billing (plan.md §12 — correctness is non-negotiable and
/// unit-tested). Pure and deterministic so the QA suite can assert it exactly.
/// </summary>
public interface IGstService
{
    /// <summary>
    /// Computes the CGST/SGST split and gross for one line of an <b>intra-state</b>
    /// sale.
    /// </summary>
    /// <param name="taxableAmount">The taxable base (after any discount).</param>
    /// <param name="gstRatePercent">The full GST rate percent, e.g. 5, 12, 18.</param>
    /// <returns>The CGST, SGST, total GST, and gross amount.</returns>
    GstResult CalculateLineGst(decimal taxableAmount, decimal gstRatePercent);
}
