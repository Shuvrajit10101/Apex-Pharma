namespace ApexPharma.Application.Services;

/// <summary>
/// The saved-bill summary returned from <see cref="IBillingService.CreateSaleAsync"/> on
/// success (plan.md §6.1, §9). Carries the assigned bill number and the money roll-up so the
/// UI can show and print the completed sale without a second query.
/// </summary>
/// <param name="SaleId">The persisted sale's primary key.</param>
/// <param name="BillNo">The unique, sequential bill number assigned in the transaction.</param>
/// <param name="Subtotal">Sum of line taxable bases after per-line and bill discounts.</param>
/// <param name="Discount">Total discount applied (line discounts + bill discount).</param>
/// <param name="Cgst">Central GST total across all lines.</param>
/// <param name="Sgst">State GST total across all lines.</param>
/// <param name="RoundOff">Rounding adjustment so the printed total is a clean rupee figure.</param>
/// <param name="Total">Grand total the customer pays (or is billed to khata on credit).</param>
public readonly record struct SaleReceipt(
    int SaleId,
    string BillNo,
    decimal Subtotal,
    decimal Discount,
    decimal Cgst,
    decimal Sgst,
    decimal RoundOff,
    decimal Total);
