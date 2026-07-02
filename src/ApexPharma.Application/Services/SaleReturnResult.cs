namespace ApexPharma.Application.Services;

/// <summary>
/// Summary of a completed sales return (plan.md §6.1). Carries the total value reversed
/// (a credit note / refund figure) and its GST split, plus the number of lines restocked,
/// so the UI can confirm the outcome without a second query.
/// </summary>
/// <param name="SaleId">The original sale the return reversed.</param>
/// <param name="BillNo">The original bill number.</param>
/// <param name="LinesReturned">How many sold lines were (partially or fully) returned.</param>
/// <param name="TotalQty">Total units returned across all lines.</param>
/// <param name="TaxableReversed">Sum of returned taxable value (net of the sale's discounts).</param>
/// <param name="Cgst">Central GST reversed.</param>
/// <param name="Sgst">State GST reversed.</param>
/// <param name="TotalRefund">Grand total reversed = taxable + CGST + SGST (credit-note amount).</param>
/// <param name="KhataReduced">Amount removed from the customer's khata (0 for a non-credit sale).</param>
public readonly record struct SaleReturnReceipt(
    int SaleId,
    string BillNo,
    int LinesReturned,
    decimal TotalQty,
    decimal TaxableReversed,
    decimal Cgst,
    decimal Sgst,
    decimal TotalRefund,
    decimal KhataReduced);
