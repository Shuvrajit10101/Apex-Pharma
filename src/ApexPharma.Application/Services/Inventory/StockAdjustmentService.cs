using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Time;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services.Inventory;

/// <summary>
/// Concrete stock-adjustment service (plan.md §6.1, §12). Breakage/wastage, physical-count
/// correction, and expiry write-off all funnel through the existing
/// <see cref="IInventoryService.AdjustStockAsync"/> path — ONE ACID transaction per adjustment,
/// non-negative-guarded, each writing a <see cref="StockAdjustment"/> audit row (batch, type, signed
/// delta, reason, date, acting user). RBAC (<see cref="Permission.AdjustStock"/>) is re-checked here,
/// not just in the UI (plan.md §4). Reads are <c>AsNoTracking</c> and mutate nothing (plan.md §8).
/// </summary>
public class StockAdjustmentService : IStockAdjustmentService
{
    private readonly ApexPharmaDbContext _db;
    private readonly IInventoryService _inventory;
    private readonly IAuthService _auth;
    private readonly ITimeZoneProvider _tz;

    public StockAdjustmentService(ApexPharmaDbContext db, IInventoryService inventory, IAuthService auth, ITimeZoneProvider tz)
    {
        _db = db;
        _inventory = inventory;
        _auth = auth;
        _tz = tz;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdjustableBatch>> GetAdjustableBatchesAsync(CancellationToken cancellationToken = default)
    {
        // "Today" is the pharmacy's LOCAL (IST) trading day, not the UTC day, so the IsExpired flag
        // agrees with billing/write-off across the IST 00:00–05:30 window (plan.md §11, §14).
        DateTime today = _tz.LocalToday();

        return (await _db.Batches.AsNoTracking()
            .Include(b => b.Product)
            .Where(b => b.QtyOnHand > 0)
            .OrderBy(b => b.Product!.Name)
            .ThenBy(b => b.ExpiryDate)
            .ToListAsync(cancellationToken))
            .Select(b => ToAdjustable(b, today))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdjustableBatch>> GetExpiredBatchesAsync(DateTime? cutoff = null, CancellationToken cancellationToken = default)
    {
        // Default cutoff is the IST trading day; an explicit caller-supplied cutoff is honored as-is.
        DateTime today = _tz.LocalToday();
        DateTime cut = (cutoff ?? today).Date;

        return (await _db.Batches.AsNoTracking()
            .Include(b => b.Product)
            .Where(b => b.QtyOnHand > 0 && b.ExpiryDate <= cut)
            .OrderBy(b => b.ExpiryDate)
            .ToListAsync(cancellationToken))
            .Select(b => ToAdjustable(b, today))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<MasterResult> AdjustByDeltaAsync(int batchId, decimal qtyDelta, string reason, int userId, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.AdjustStock))
        {
            return MasterResult.Fail("You do not have permission to adjust stock.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            // Breakage/wastage MUST carry a reason for the audit trail (plan.md §6.1, §4).
            return MasterResult.Fail("A reason is required for a manual adjustment.");
        }

        if (qtyDelta == 0m)
        {
            return MasterResult.Fail("Enter a non-zero quantity to adjust.");
        }

        try
        {
            // Reuse the existing transactional, non-negative-guarded audit path (plan.md §6.1).
            await _inventory.AdjustStockAsync(batchId, AdjustmentType.Breakage, qtyDelta, reason.Trim(), userId, cancellationToken);
            return MasterResult.Ok();
        }
        catch (InvalidOperationException ex)
        {
            // Below-zero guard and missing-batch are expected failures → clear message, no throw.
            return MasterResult.Fail(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<MasterResult> CorrectCountAsync(int batchId, decimal countedQty, string reason, int userId, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.AdjustStock))
        {
            return MasterResult.Fail("You do not have permission to adjust stock.");
        }

        if (countedQty < 0m)
        {
            return MasterResult.Fail("The counted quantity cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return MasterResult.Fail("A reason is required for a physical-count correction.");
        }

        // Read the current on-hand so we can record the exact delta (counted − current). We read
        // then adjust; AdjustStockAsync re-reads inside its own transaction and applies the delta.
        Batch? batch = await _db.Batches.AsNoTracking().FirstOrDefaultAsync(b => b.BatchId == batchId, cancellationToken);
        if (batch is null)
        {
            return MasterResult.Fail($"Batch {batchId} not found.");
        }

        decimal delta = countedQty - batch.QtyOnHand;
        if (delta == 0m)
        {
            return MasterResult.Fail("The counted quantity matches the recorded on-hand — nothing to correct.");
        }

        try
        {
            await _inventory.AdjustStockAsync(batchId, AdjustmentType.CountCorrection, delta, reason.Trim(), userId, cancellationToken);
            return MasterResult.Ok();
        }
        catch (InvalidOperationException ex)
        {
            return MasterResult.Fail(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<MasterResult<ExpiryWriteOffLine>> WriteOffExpiredBatchAsync(int batchId, int userId, UserRole actingRole, DateTime? cutoff = null, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.AdjustStock))
        {
            return MasterResult<ExpiryWriteOffLine>.Fail("You do not have permission to adjust stock.");
        }

        // Default cutoff is the IST trading day; an explicit caller-supplied cutoff is honored as-is.
        DateTime cut = (cutoff ?? _tz.LocalToday()).Date;

        Batch? batch = await _db.Batches.AsNoTracking()
            .Include(b => b.Product)
            .FirstOrDefaultAsync(b => b.BatchId == batchId, cancellationToken);
        if (batch is null)
        {
            return MasterResult<ExpiryWriteOffLine>.Fail($"Batch {batchId} not found.");
        }

        MasterResult<ExpiryWriteOffLine> outcome = await WriteOffExpiredCoreAsync(batch, cut, userId, cancellationToken);
        return outcome;
    }

    /// <inheritdoc />
    public async Task<MasterResult<ExpiryWriteOffSummary>> WriteOffAllExpiredAsync(int userId, UserRole actingRole, DateTime? cutoff = null, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.AdjustStock))
        {
            return MasterResult<ExpiryWriteOffSummary>.Fail("You do not have permission to adjust stock.");
        }

        // Default cutoff is the IST trading day; an explicit caller-supplied cutoff is honored as-is.
        DateTime cut = (cutoff ?? _tz.LocalToday()).Date;

        List<Batch> expired = await _db.Batches.AsNoTracking()
            .Include(b => b.Product)
            .Where(b => b.QtyOnHand > 0 && b.ExpiryDate <= cut)
            .OrderBy(b => b.ExpiryDate)
            .ToListAsync(cancellationToken);

        var lines = new List<ExpiryWriteOffLine>();
        foreach (Batch batch in expired)
        {
            // Each batch is written off in its OWN atomic adjustment (AdjustStockAsync opens its own
            // transaction) so a single failure does not undo the batches already written off (plan.md §6.1).
            MasterResult<ExpiryWriteOffLine> lineResult = await WriteOffExpiredCoreAsync(batch, cut, userId, cancellationToken);
            if (lineResult.Succeeded)
            {
                lines.Add(lineResult.Value!);
            }
            // A batch that becomes non-expired/empty between the read and the write is silently
            // skipped — it is no longer a write-off candidate.
        }

        var summary = new ExpiryWriteOffSummary(
            lines.Count,
            lines.Sum(l => l.QtyWrittenOff),
            lines.Sum(l => l.ValueAtCost),
            lines.Sum(l => l.ValueAtMrp),
            lines);

        return MasterResult<ExpiryWriteOffSummary>.Ok(summary);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdjustmentHistoryRow>> GetHistoryAsync(DateTime? from = null, DateTime? to = null, int? productId = null, int? batchId = null, CancellationToken cancellationToken = default)
    {
        IQueryable<StockAdjustment> query = _db.StockAdjustments.AsNoTracking()
            .Include(a => a.Product)
            .Include(a => a.Batch)
            .Include(a => a.CreatedByUser);

        // The operator picks LOCAL (IST) calendar dates; adjustment rows are stamped in UTC. Convert
        // each chosen local date to the correct UTC bound via the pharmacy timezone (mirrors how the
        // reports/ledgers/day-end windows are derived through DayWindow, plan.md §11, §14) so an
        // adjustment stamped just after IST midnight buckets into the IST day the operator expects,
        // not the prior UTC day. Each bound is converted independently since either can be omitted.
        TimeZoneInfo tz = _tz.GetPharmacyTimeZone();

        if (from is DateTime f)
        {
            // Inclusive lower bound: the start (00:00 local) of the 'from' local date, in UTC.
            (DateTime fromUtc, _) = DayWindow.ForLocalDate(f, tz);
            query = query.Where(a => a.Date >= fromUtc);
        }

        if (to is DateTime t)
        {
            // Inclusive of the whole 'to' local day: exclusive upper bound = start of the NEXT local
            // day (00:00 local of to+1), in UTC.
            (_, DateTime toUtcExclusive) = DayWindow.ForLocalDate(t, tz);
            query = query.Where(a => a.Date < toUtcExclusive);
        }

        if (productId is int pid)
        {
            query = query.Where(a => a.ProductId == pid);
        }

        if (batchId is int bid)
        {
            query = query.Where(a => a.BatchId == bid);
        }

        return (await query
            .OrderByDescending(a => a.Date)
            .ThenByDescending(a => a.AdjustmentId)
            .ToListAsync(cancellationToken))
            .Select(a => new AdjustmentHistoryRow(
                a.AdjustmentId,
                a.Date,
                a.ProductId,
                a.Product?.Name ?? string.Empty,
                a.BatchId,
                a.Batch?.BatchNo ?? string.Empty,
                a.Type,
                a.QtyDelta,
                a.Reason,
                a.CreatedBy,
                a.CreatedByUser?.FullName is { Length: > 0 } name ? name : (a.CreatedByUser?.Username ?? string.Empty)))
            .ToList();
    }

    /// <summary>
    /// Core single-batch expiry write-off shared by the single and bulk paths. Validates the batch is
    /// expired (as of <paramref name="cut"/>) and non-empty, computes value lost at cost/MRP, then zeroes
    /// on-hand via the audited <see cref="AdjustmentType.Expiry"/> adjustment. Assumes RBAC already checked.
    /// </summary>
    private async Task<MasterResult<ExpiryWriteOffLine>> WriteOffExpiredCoreAsync(Batch batch, DateTime cut, int userId, CancellationToken cancellationToken)
    {
        if (batch.QtyOnHand <= 0m)
        {
            return MasterResult<ExpiryWriteOffLine>.Fail($"Batch '{batch.BatchNo}' has no stock to write off.");
        }

        if (batch.ExpiryDate.Date > cut)
        {
            // Only expired stock is written off as Expiry — a non-expired lot is not a candidate (plan.md §6.1).
            return MasterResult<ExpiryWriteOffLine>.Fail(
                $"Batch '{batch.BatchNo}' expires {batch.ExpiryDate:yyyy-MM-dd} — it is not expired as of {cut:yyyy-MM-dd}.");
        }

        decimal qty = batch.QtyOnHand;
        decimal valueAtCost = Math.Round(qty * batch.PurchasePrice, 2, MidpointRounding.AwayFromZero);
        decimal valueAtMrp = Math.Round(qty * batch.Mrp, 2, MidpointRounding.AwayFromZero);
        string reason = $"Expired {batch.ExpiryDate:yyyy-MM-dd}";

        try
        {
            // Zero the lot: delta = −QtyOnHand, recorded as an Expiry adjustment (plan.md §6.1).
            await _inventory.AdjustStockAsync(batch.BatchId, AdjustmentType.Expiry, -qty, reason, userId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return MasterResult<ExpiryWriteOffLine>.Fail(ex.Message);
        }

        return MasterResult<ExpiryWriteOffLine>.Ok(new ExpiryWriteOffLine(
            batch.BatchId,
            batch.ProductId,
            batch.Product?.Name ?? string.Empty,
            batch.BatchNo,
            batch.ExpiryDate,
            qty,
            valueAtCost,
            valueAtMrp));
    }

    private static AdjustableBatch ToAdjustable(Batch b, DateTime today) => new(
        b.BatchId,
        b.ProductId,
        b.Product?.Name ?? string.Empty,
        b.BatchNo,
        b.ExpiryDate,
        b.QtyOnHand,
        b.PurchasePrice,
        b.Mrp,
        b.ExpiryDate.Date <= today);
}
