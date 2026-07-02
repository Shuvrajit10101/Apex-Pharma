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
/// <c>scopedToUserId</c> to the acting user server-side (never trusting the UI). When
/// <c>scopedToUserId</c> is set, cash SALES and the own-sales grid are filtered by
/// <see cref="Sale.CreatedBy"/>; cash receipts, supplier payments, and refunds are always store-level
/// (they are not attributed to a till user). Closing a whole day is Owner/Pharmacist-only — a Cashier
/// is rejected.
/// </para>
/// </summary>
public interface IDayEndService
{
    /// <summary>
    /// Builds the cash-reconciliation summary for <paramref name="businessDate"/>. If a close already
    /// exists for that date, returns the STORED snapshot with <c>IsClosed=true</c>; otherwise computes
    /// live. A Cashier is coerced to their own scope regardless of <paramref name="scopedToUserId"/>.
    /// Gated on <see cref="Permission.DayEnd"/>.
    /// </summary>
    Task<MasterResult<DayEndSummary>> GetDaySummaryAsync(
        DateTime businessDate, int actingUserId, UserRole actingRole, int? scopedToUserId, CancellationToken ct = default);

    /// <summary>
    /// Finalizes (closes) a business-day. Recomputes the cash breakdown + expected cash server-side
    /// inside one ACID transaction (any UI-supplied expected is ignored), sets
    /// <c>Variance = CountedCash − ExpectedCash</c>, requires a note when the variance is non-zero,
    /// enforces one close per day, snapshots the breakdown, and commits. Gated on
    /// <see cref="Permission.DayEnd"/> AND rejects a Cashier (whole-store close is Owner/Pharmacist-only).
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
