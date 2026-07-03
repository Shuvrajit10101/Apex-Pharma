using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.DayEnd;

/// <summary>
/// A read-only cash-reconciliation summary for one business-day (plan.md §3, §11 — Phase 2e). A flat
/// DTO (no EF entity crosses the layer boundary — plan.md §8). When <see cref="IsClosed"/> is true the
/// figures are the STORED snapshot from the <see cref="Domain.Entities.DayEndClose"/> (immutable
/// against later back-dated cash); otherwise they are computed live from the day's transactions.
/// </summary>
/// <param name="BusinessDate">The business-day this summary covers (date-floored).</param>
/// <param name="OpeningFloat">Till float at the start of the day (= the prior close's carry-forward, else 0).</param>
/// <param name="CashSales">Cash IN from cash-mode sales (scoped to the cashier when applicable).</param>
/// <param name="CashReceipts">Cash IN from cash-mode customer receipts (store-level).</param>
/// <param name="CashRefunds">Cash OUT for refunds on cash-mode parent sales (store-level).</param>
/// <param name="CashSupplierPayments">Cash OUT for cash-mode supplier payments (store-level).</param>
/// <param name="ExpectedCash">OpeningFloat + CashSales + CashReceipts − CashRefunds − CashSupplierPayments.</param>
/// <param name="UpiTotal">Non-cash tile: total of UPI-mode sales over the day.</param>
/// <param name="CardTotal">Non-cash tile: total of Card-mode sales over the day.</param>
/// <param name="CreditTotal">Non-cash tile: total of Credit-mode (khata) sales over the day.</param>
/// <param name="BillCount">Number of bills over the day (scoped like <see cref="CashSales"/>).</param>
/// <param name="GrossSales">Gross of ALL sales over the day (every payment mode; scoped like bills).</param>
/// <param name="IsClosed">True when a <see cref="Domain.Entities.DayEndClose"/> already exists for the date.</param>
/// <param name="CountedCash">Counted cash from the close (null when not closed).</param>
/// <param name="Variance">Counted − Expected from the close (null when not closed).</param>
/// <param name="ClosingCarryForward">Float carried to the next day from the close (null when not closed).</param>
/// <param name="Note">The close note (null when not closed or none entered).</param>
/// <param name="OpeningFloatReason">The reason the opening float was overridden (null when not closed, or when the float was not overridden).</param>
/// <param name="ClosedAt">When the day was closed, UTC (null when not closed).</param>
/// <param name="ClosedByName">Name of the user who closed the day (null when not closed).</param>
/// <param name="OwnSales">The scoped per-bill rows for the on-screen grid ("my sales" for a Cashier / whole-store otherwise).</param>
public sealed record DayEndSummary(
    DateTime BusinessDate,
    decimal OpeningFloat,
    decimal CashSales,
    decimal CashReceipts,
    decimal CashRefunds,
    decimal CashSupplierPayments,
    decimal ExpectedCash,
    decimal UpiTotal,
    decimal CardTotal,
    decimal CreditTotal,
    int BillCount,
    decimal GrossSales,
    bool IsClosed,
    decimal? CountedCash,
    decimal? Variance,
    decimal? ClosingCarryForward,
    string? Note,
    string? OpeningFloatReason,
    DateTime? ClosedAt,
    string? ClosedByName,
    IReadOnlyList<DayEndSaleRow> OwnSales);

/// <summary>
/// One bill row for the day-end sales grid. Scoped to the acting cashier's own sales when a Cashier
/// is closing, or the whole store for Owner/Pharmacist.
/// </summary>
/// <param name="BillNo">The bill number.</param>
/// <param name="Time">The bill timestamp (UTC; the UI converts to local for display).</param>
/// <param name="Mode">How the bill was settled.</param>
/// <param name="Total">The bill total (gross).</param>
public readonly record struct DayEndSaleRow(
    string BillNo,
    DateTime Time,
    PaymentMode Mode,
    decimal Total);

/// <summary>
/// A request to close (reconcile and finalize) a business-day (plan.md §3 — Phase 2e). The service
/// RECOMPUTES the cash DELTAS (sales/receipts/refunds/supplier payments) server-side inside the
/// transaction — there is no cash-delta or expected field to inject — but HONORS the operator's
/// declared <see cref="OpeningFloat"/> (design decision #4 — an override, not ignored), so
/// <c>ExpectedCash = OpeningFloat + (server-computed cash deltas)</c>. The caller supplies the opening
/// float and the counted cash (plus an optional carry-forward override + note). One close per
/// business-day is enforced.
/// </summary>
/// <param name="BusinessDate">The business-day to close (date-floored).</param>
/// <param name="OpeningFloat">The operator's declared opening float. HONORED and persisted as the snapshot value; it drives ExpectedCash = OpeningFloat + server-computed cash deltas (the cash deltas are NOT taken from the UI).</param>
/// <param name="CountedCash">The physically counted cash.</param>
/// <param name="ClosingCarryForward">Optional override for the next-day float; defaults to <paramref name="CountedCash"/> when null.</param>
/// <param name="Note">Required when the resulting variance is non-zero; optional otherwise.</param>
/// <param name="OpeningFloatReason">Required (non-blank) when <paramref name="OpeningFloat"/> differs from the carried-forward amount (the prior close's carry-forward, else 0); optional otherwise. Persisted on the close (owner-approved day-end control).</param>
public sealed record DayEndCloseInput(
    DateTime BusinessDate,
    decimal OpeningFloat,
    decimal CountedCash,
    decimal? ClosingCarryForward = null,
    string? Note = null,
    string? OpeningFloatReason = null);

/// <summary>
/// One row of the close-history grid (plan.md §11 — Phase 2e): a finalized day-end close, most-recent
/// first. A flat DTO so no EF entity leaks to the UI.
/// </summary>
/// <param name="BusinessDate">The closed business-day.</param>
/// <param name="ExpectedCash">The snapshot expected cash.</param>
/// <param name="CountedCash">The counted cash.</param>
/// <param name="Variance">Counted − Expected.</param>
/// <param name="OpeningFloatReason">The reason the opening float was overridden (null/empty when it was not).</param>
/// <param name="ClosedByName">Who closed it.</param>
/// <param name="ClosedAt">When it was closed (UTC).</param>
public readonly record struct DayEndCloseRow(
    DateTime BusinessDate,
    decimal ExpectedCash,
    decimal CountedCash,
    decimal Variance,
    string? OpeningFloatReason,
    string ClosedByName,
    DateTime ClosedAt);
