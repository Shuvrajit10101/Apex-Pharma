using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.DayEnd;

/// <summary>
/// Day-end cash reconciliation + the Cashier's own-day view (plan.md §3, §11 — Phase 2e). Produces a
/// live cash summary for a business-day, finalizes a day in an ACID transaction with a server-side
/// recompute, and lists prior closes. Every method re-checks <see cref="Permission.DayEnd"/> and
/// fails closed (a failed <see cref="MasterResult{T}"/>, never an exception for an expected failure —
/// plan.md §6.2). No money rule lives in the UI (plan.md §8).
/// <para>
/// <b>Cashier scoping.</b> A Cashier only ever sees their own till: the two read methods coerce
/// <c>scopedToUserId</c> to the acting user server-side (never trusting the UI). On the LIVE/open-day
/// path, when <c>scopedToUserId</c> is set, cash SALES and the own-sales grid are filtered by
/// <see cref="Sale.CreatedBy"/>; cash receipts, supplier payments, and refunds are always store-level
/// (they are not attributed to a till user). On an already-CLOSED day the cash breakdown is the
/// immutable WHOLE-STORE snapshot captured at close time — a Cashier sees that store-wide cash
/// breakdown (it is deliberately not re-scoped per cashier), while only the OwnSales grid and the
/// non-cash tiles remain scoped to the acting cashier for that date. Closing a whole day is
/// Owner/Pharmacist-only — a Cashier is rejected.
/// </para>
/// </summary>
public interface IDayEndService
{
    /// <summary>
    /// Builds the cash-reconciliation summary for <paramref name="businessDate"/>. If a close already
    /// exists for that date, returns the STORED whole-store snapshot with <c>IsClosed=true</c> (the
    /// cash breakdown is the immutable close-time figure, not re-scoped per cashier); otherwise computes
    /// live with own-till scoping. A Cashier is coerced to their own scope regardless of
    /// <paramref name="scopedToUserId"/> — this scopes the OwnSales grid and the non-cash tiles; on a
    /// closed day the cash breakdown remains the whole-store snapshot. Gated on
    /// <see cref="Permission.DayEnd"/>.
    /// </summary>
    Task<MasterResult<DayEndSummary>> GetDaySummaryAsync(
        DateTime businessDate, int actingUserId, UserRole actingRole, int? scopedToUserId, CancellationToken ct = default);

    /// <summary>
    /// Finalizes (closes) a business-day. Recomputes the cash DELTAS (sales/receipts/refunds/supplier
    /// payments) server-side inside one ACID transaction — no UI figure is trusted for them — while
    /// honoring the operator's declared <see cref="DayEndCloseInput.OpeningFloat"/>, so
    /// <c>ExpectedCash = OpeningFloat + (server-computed cash deltas)</c> and
    /// <c>Variance = CountedCash − ExpectedCash</c>. Requires a note when the variance is non-zero,
    /// enforces one close per day (pre-check + UNIQUE index + a clean "already closed" message on a
    /// race), snapshots the breakdown, and commits. Gated on <see cref="Permission.DayEnd"/> AND rejects
    /// a Cashier (whole-store close is Owner/Pharmacist-only).
    /// </summary>
    Task<MasterResult<DayEndClose>> CloseDayAsync(
        DayEndCloseInput input, int actingUserId, UserRole actingRole, CancellationToken ct = default);

    /// <summary>
    /// Lists finalized closes over the inclusive [from, to] window, most-recent first. A Cashier is
    /// coerced to their own scope (though closes are whole-store, so scoping does not change the rows —
    /// kept symmetric with the summary). Gated on <see cref="Permission.DayEnd"/>.
    /// </summary>
    Task<MasterResult<IReadOnlyList<DayEndCloseRow>>> GetCloseHistoryAsync(
        DateTime fromDate, DateTime toDate, int actingUserId, UserRole actingRole, int? scopedToUserId, CancellationToken ct = default);
}
