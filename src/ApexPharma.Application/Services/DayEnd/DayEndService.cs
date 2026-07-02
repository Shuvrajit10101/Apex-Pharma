using ApexPharma.Application.Services.MasterData;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services.DayEnd;

/// <summary>
/// Concrete <see cref="IDayEndService"/> (plan.md §3, §11 — Phase 2e).
/// <para>
/// Expected cash for a business-day is derived over a half-open day window (the same
/// <c>[date, date+1)</c> idiom every report uses) as
/// <c>OpeningFloat + CashSales + CashReceipts − CashRefunds − CashSupplierPayments</c>, where every
/// component is restricted to <see cref="PaymentMode.Cash"/>. Refunds have no payment mode of their
/// own, so a return is treated as cash when its PARENT sale was cash (the same parent-sale-mode proxy
/// the customer ledger uses). <see cref="Sale"/> totals (and the own-sales grid) are scoped by
/// <see cref="Sale.CreatedBy"/> when a <c>scopedToUserId</c> is supplied — a Cashier is coerced to
/// their own id. Receipts / supplier payments / refunds are always store-level (not attributed to a
/// till user). All reads are <c>AsNoTracking</c>; decimals are materialised then summed in memory
/// (SQLite's EF provider is brittle translating grouped decimal SUMs — consistent with the rest of
/// the codebase).
/// </para>
/// <para>
/// Closing a day snapshots the whole breakdown INSIDE one ACID transaction after RECOMPUTING it
/// server-side (any UI-supplied expected is ignored), so a closed day is immutable against later
/// back-dated cash. One close per business-day is enforced by a pre-check plus the UNIQUE index.
/// </para>
/// </summary>
public sealed class DayEndService : IDayEndService
{
    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;

    public DayEndService(ApexPharmaDbContext db, IAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <inheritdoc />
    public async Task<MasterResult<DayEndSummary>> GetDaySummaryAsync(
        DateTime businessDate, int actingUserId, UserRole actingRole, int? scopedToUserId, CancellationToken ct = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.DayEnd))
        {
            return MasterResult<DayEndSummary>.Fail("You do not have permission to view the day-end summary.");
        }

        // Cashier coercion — server-side, never trust the UI. A Cashier only ever sees their own till.
        if (actingRole == UserRole.Cashier)
        {
            scopedToUserId = actingUserId;
        }

        DateTime date = businessDate.Date;

        // If the day is already closed, return the STORED snapshot (immutable against later
        // back-dated cash). The own-sales grid is still derived live for context — it does not feed
        // the reconciliation, which is frozen in the snapshot.
        DayEndClose? existing = await _db.DayEndCloses.AsNoTracking()
            .Include(d => d.CreatedByUser)
            .FirstOrDefaultAsync(d => d.BusinessDate == date, ct);

        (decimal upi, decimal card, decimal credit, int billCount, decimal gross, IReadOnlyList<DayEndSaleRow> ownSales)
            = await NonCashAndGridAsync(date, scopedToUserId, ct);

        if (existing is not null)
        {
            return MasterResult<DayEndSummary>.Ok(new DayEndSummary(
                BusinessDate: existing.BusinessDate,
                OpeningFloat: existing.OpeningFloat,
                CashSales: existing.CashSales,
                CashReceipts: existing.CashReceipts,
                CashRefunds: existing.CashRefunds,
                CashSupplierPayments: existing.CashSupplierPayments,
                ExpectedCash: existing.ExpectedCash,
                UpiTotal: upi,
                CardTotal: card,
                CreditTotal: credit,
                BillCount: billCount,
                GrossSales: gross,
                IsClosed: true,
                CountedCash: existing.CountedCash,
                Variance: existing.Variance,
                ClosingCarryForward: existing.ClosingCarryForward,
                Note: existing.Note,
                ClosedAt: existing.ClosedAt,
                ClosedByName: existing.CreatedByUser?.FullName ?? existing.CreatedByUser?.Username,
                OwnSales: ownSales));
        }

        // Not closed → compute the cash breakdown live.
        CashBreakdown cash = await ComputeCashBreakdownAsync(date, scopedToUserId, ct);

        return MasterResult<DayEndSummary>.Ok(new DayEndSummary(
            BusinessDate: date,
            OpeningFloat: cash.OpeningFloat,
            CashSales: cash.CashSales,
            CashReceipts: cash.CashReceipts,
            CashRefunds: cash.CashRefunds,
            CashSupplierPayments: cash.CashSupplierPayments,
            ExpectedCash: cash.ExpectedCash,
            UpiTotal: upi,
            CardTotal: card,
            CreditTotal: credit,
            BillCount: billCount,
            GrossSales: gross,
            IsClosed: false,
            CountedCash: null,
            Variance: null,
            ClosingCarryForward: null,
            Note: null,
            ClosedAt: null,
            ClosedByName: null,
            OwnSales: ownSales));
    }

    /// <inheritdoc />
    public async Task<MasterResult<DayEndClose>> CloseDayAsync(
        DayEndCloseInput input, int actingUserId, UserRole actingRole, CancellationToken ct = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.DayEnd))
        {
            return MasterResult<DayEndClose>.Fail("You do not have permission to close the day.");
        }

        // A whole-store close is Owner/Pharmacist-only. A Cashier has DayEnd (to view their own till)
        // but must not finalize the store's day (plan.md §4).
        if (actingRole == UserRole.Cashier)
        {
            return MasterResult<DayEndClose>.Fail("A cashier cannot close the store day. Ask the owner or pharmacist.");
        }

        if (input is null)
        {
            return MasterResult<DayEndClose>.Fail("Close details are required.");
        }

        DateTime date = input.BusinessDate.Date;

        // ONE ACID transaction: re-check one-close-per-day, recompute the breakdown server-side, and
        // persist the snapshot — all commit together or roll back (plan.md §12). Recomputing INSIDE
        // the transaction means any UI-supplied expected is ignored and the stored figure is
        // authoritative.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            bool alreadyClosed = await _db.DayEndCloses.AnyAsync(d => d.BusinessDate == date, ct);
            if (alreadyClosed)
            {
                await tx.RollbackAsync(ct);
                return MasterResult<DayEndClose>.Fail($"The day {date:yyyy-MM-dd} is already closed.");
            }

            // Whole-store close: scope is null so cash SALES are store-wide (not per-cashier).
            CashBreakdown cash = await ComputeCashBreakdownAsync(date, scopedToUserId: null, ct);

            decimal counted = input.CountedCash;
            decimal variance = counted - cash.ExpectedCash;

            // A non-zero variance must be explained (plan.md §3 — cash discipline).
            if (variance != 0m && string.IsNullOrWhiteSpace(input.Note))
            {
                await tx.RollbackAsync(ct);
                return MasterResult<DayEndClose>.Fail(
                    $"The counted cash differs from expected by {variance:0.00}. A note is required to explain the variance.");
            }

            var close = new DayEndClose
            {
                BusinessDate = date,
                OpeningFloat = cash.OpeningFloat,
                CashSales = cash.CashSales,
                CashReceipts = cash.CashReceipts,
                CashRefunds = cash.CashRefunds,
                CashSupplierPayments = cash.CashSupplierPayments,
                ExpectedCash = cash.ExpectedCash,
                CountedCash = counted,
                Variance = variance,
                // Carry-forward defaults to the counted cash (what physically stays in the till) when
                // the closer doesn't override it.
                ClosingCarryForward = input.ClosingCarryForward ?? counted,
                Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
                ClosedAt = DateTime.UtcNow,
                CreatedBy = actingUserId,
            };

            await _db.DayEndCloses.AddAsync(close, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return MasterResult<DayEndClose>.Ok(close);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MasterResult<IReadOnlyList<DayEndCloseRow>>> GetCloseHistoryAsync(
        DateTime fromDate, DateTime toDate, int actingUserId, UserRole actingRole, int? scopedToUserId, CancellationToken ct = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.DayEnd))
        {
            return MasterResult<IReadOnlyList<DayEndCloseRow>>.Fail("You do not have permission to view close history.");
        }

        // Cashier coercion (kept symmetric with the summary; closes are whole-store so this does not
        // change the returned rows, but it never trusts the UI's scope either).
        if (actingRole == UserRole.Cashier)
        {
            scopedToUserId = actingUserId;
        }

        DateTime from = fromDate.Date;
        DateTime to = toDate.Date;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        // BusinessDate is date-floored, so an inclusive [from, to] over the DATE column uses <= to.
        List<DayEndClose> closes = await _db.DayEndCloses.AsNoTracking()
            .Include(d => d.CreatedByUser)
            .Where(d => d.BusinessDate >= from && d.BusinessDate <= to)
            .OrderByDescending(d => d.BusinessDate)
            .ToListAsync(ct);

        IReadOnlyList<DayEndCloseRow> rows = closes.Select(d => new DayEndCloseRow(
            d.BusinessDate,
            d.ExpectedCash,
            d.CountedCash,
            d.Variance,
            d.CreatedByUser?.FullName ?? d.CreatedByUser?.Username ?? string.Empty,
            d.ClosedAt)).ToList();

        return MasterResult<IReadOnlyList<DayEndCloseRow>>.Ok(rows);
    }

    /// <summary>
    /// Computes the cash reconciliation components for the day. Cash SALES are scoped by
    /// <see cref="Sale.CreatedBy"/> when <paramref name="scopedToUserId"/> is set; cash receipts,
    /// supplier payments, and refunds are always store-level.
    /// </summary>
    private async Task<CashBreakdown> ComputeCashBreakdownAsync(DateTime date, int? scopedToUserId, CancellationToken ct)
    {
        DateTime from = date;
        DateTime toExclusive = date.AddDays(1);

        // OpeningFloat = the most recent prior close's ClosingCarryForward (BusinessDate < date), else 0.
        List<decimal> priorCarry = await _db.DayEndCloses.AsNoTracking()
            .Where(d => d.BusinessDate < date)
            .OrderByDescending(d => d.BusinessDate)
            .Select(d => d.ClosingCarryForward)
            .Take(1)
            .ToListAsync(ct);
        decimal openingFloat = priorCarry.Count > 0 ? priorCarry[0] : 0m;

        // Cash IN — cash-mode sales (scoped by CreatedBy when a scope is set).
        List<decimal> cashSaleTotals = await _db.Sales.AsNoTracking()
            .Where(s => s.BillDate >= from && s.BillDate < toExclusive
                        && s.PaymentMode == PaymentMode.Cash
                        && (scopedToUserId == null || s.CreatedBy == scopedToUserId))
            .Select(s => s.Total)
            .ToListAsync(ct);

        // Cash IN — cash-mode customer receipts (store-level, not attributed to a till user).
        List<decimal> cashReceiptAmounts = await _db.CustomerReceipts.AsNoTracking()
            .Where(cr => cr.ReceiptDate >= from && cr.ReceiptDate < toExclusive
                         && cr.PaymentMode == PaymentMode.Cash)
            .Select(cr => cr.Amount)
            .ToListAsync(ct);

        // Cash OUT — cash-mode supplier payments (store-level).
        List<decimal> cashSupplierPaymentAmounts = await _db.SupplierPayments.AsNoTracking()
            .Where(sp => sp.PaymentDate >= from && sp.PaymentDate < toExclusive
                         && sp.PaymentMode == PaymentMode.Cash)
            .Select(sp => sp.Amount)
            .ToListAsync(ct);

        // Cash OUT — refunds on cash-mode parent sales (parent-sale-mode proxy; SaleReturn has no
        // PaymentMode of its own). Store-level.
        List<decimal> cashRefundAmounts = await _db.SaleReturns.AsNoTracking()
            .Where(r => r.Date >= from && r.Date < toExclusive
                        && r.Sale!.PaymentMode == PaymentMode.Cash)
            .Select(r => r.Amount)
            .ToListAsync(ct);

        decimal cashSales = cashSaleTotals.Sum();
        decimal cashReceipts = cashReceiptAmounts.Sum();
        decimal cashRefunds = cashRefundAmounts.Sum();
        decimal cashSupplierPayments = cashSupplierPaymentAmounts.Sum();
        decimal expected = openingFloat + cashSales + cashReceipts - cashRefunds - cashSupplierPayments;

        return new CashBreakdown(openingFloat, cashSales, cashReceipts, cashRefunds, cashSupplierPayments, expected);
    }

    /// <summary>
    /// Computes the non-cash sale tiles (UPI / Card / Credit totals), the bill count, the gross sales,
    /// and the scoped own-sales grid rows. All sales figures are scoped by <see cref="Sale.CreatedBy"/>
    /// when <paramref name="scopedToUserId"/> is set (so a Cashier sees only their own sales).
    /// </summary>
    private async Task<(decimal Upi, decimal Card, decimal Credit, int BillCount, decimal Gross, IReadOnlyList<DayEndSaleRow> OwnSales)>
        NonCashAndGridAsync(DateTime date, int? scopedToUserId, CancellationToken ct)
    {
        DateTime from = date;
        DateTime toExclusive = date.AddDays(1);

        List<(string BillNo, DateTime BillDate, PaymentMode Mode, decimal Total)> sales = (await _db.Sales.AsNoTracking()
            .Where(s => s.BillDate >= from && s.BillDate < toExclusive
                        && (scopedToUserId == null || s.CreatedBy == scopedToUserId))
            .OrderBy(s => s.BillDate)
            .ThenBy(s => s.SaleId)
            .Select(s => new { s.BillNo, s.BillDate, s.PaymentMode, s.Total })
            .ToListAsync(ct))
            .Select(x => (x.BillNo, x.BillDate, x.PaymentMode, x.Total))
            .ToList();

        decimal upi = sales.Where(s => s.Mode == PaymentMode.Upi).Sum(s => s.Total);
        decimal card = sales.Where(s => s.Mode == PaymentMode.Card).Sum(s => s.Total);
        decimal credit = sales.Where(s => s.Mode == PaymentMode.Credit).Sum(s => s.Total);
        decimal gross = sales.Sum(s => s.Total);
        int billCount = sales.Count;

        IReadOnlyList<DayEndSaleRow> ownSales = sales
            .Select(s => new DayEndSaleRow(s.BillNo, s.BillDate, s.Mode, s.Total))
            .ToList();

        return (upi, card, credit, billCount, gross, ownSales);
    }

    /// <summary>The five cash components + the derived expected cash for a business-day.</summary>
    private readonly record struct CashBreakdown(
        decimal OpeningFloat,
        decimal CashSales,
        decimal CashReceipts,
        decimal CashRefunds,
        decimal CashSupplierPayments,
        decimal ExpectedCash);
}
