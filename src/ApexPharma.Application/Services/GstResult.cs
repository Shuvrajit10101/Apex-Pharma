namespace ApexPharma.Application.Services;

/// <summary>
/// The GST breakdown for a single billing line (plan.md §12). For an intra-state
/// sale the total GST is split evenly into CGST (central) and SGST (state).
/// </summary>
/// <param name="Cgst">Central GST — half of the total GST.</param>
/// <param name="Sgst">State GST — half of the total GST (equal to <see cref="Cgst"/> intra-state).</param>
/// <param name="TotalGst">CGST + SGST.</param>
/// <param name="GrossAmount">Taxable amount + total GST — what the customer pays for the line.</param>
public readonly record struct GstResult(
    decimal Cgst,
    decimal Sgst,
    decimal TotalGst,
    decimal GrossAmount);
