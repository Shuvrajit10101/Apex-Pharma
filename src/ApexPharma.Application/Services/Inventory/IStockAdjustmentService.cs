using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.Inventory;

/// <summary>
/// Stock-adjustment operations (plan.md §6.1 inventory ops, §12): breakage/wastage, physical-count
/// correction, and expiry write-off — each an audited, ACID, non-negative change to a batch's
/// on-hand quantity, plus the adjustment history query the owner needs for display and audit.
/// <para>
/// Every mutation reuses the existing <see cref="IInventoryService.AdjustStockAsync"/> path
/// (transactional, non-negative-guarded, writes a <see cref="Domain.Entities.StockAdjustment"/>
/// audit row) and is gated on <see cref="Permission.AdjustStock"/> re-checked HERE, not just in the
/// UI (plan.md §4). Expected failures are returned as <see cref="MasterResult"/>, not thrown
/// (plan.md §6.2). No money/stock rule lives in the UI (plan.md §8).
/// </para>
/// </summary>
public interface IStockAdjustmentService
{
    /// <summary>Batches with stock on hand, product-then-expiry order, for the "adjust a batch" picker.</summary>
    Task<IReadOnlyList<AdjustableBatch>> GetAdjustableBatchesAsync(CancellationToken cancellationToken = default);

    /// <summary>Expired batches (ExpiryDate ≤ cutoff, default today) still carrying stock — write-off candidates.</summary>
    Task<IReadOnlyList<AdjustableBatch>> GetExpiredBatchesAsync(DateTime? cutoff = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Manual adjustment (breakage/wastage): applies a signed delta to a batch's on-hand with a
    /// REQUIRED reason. Rejected if the result would be negative. Recorded as
    /// <see cref="AdjustmentType.Breakage"/>.
    /// </summary>
    Task<MasterResult> AdjustByDeltaAsync(int batchId, decimal qtyDelta, string reason, int userId, UserRole actingRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Physical-count correction: sets a batch's on-hand to <paramref name="countedQty"/> (≥ 0),
    /// recording the (signed) delta as <see cref="AdjustmentType.CountCorrection"/> with a reason.
    /// </summary>
    Task<MasterResult> CorrectCountAsync(int batchId, decimal countedQty, string reason, int userId, UserRole actingRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Expiry write-off for a single batch: zeroes on-hand as <see cref="AdjustmentType.Expiry"/>
    /// with an auto reason ("Expired &lt;date&gt;"). Refused if the batch is not expired as of the
    /// cutoff (default today) or already empty. Returns the value lost at cost and MRP.
    /// </summary>
    Task<MasterResult<ExpiryWriteOffLine>> WriteOffExpiredBatchAsync(int batchId, int userId, UserRole actingRole, DateTime? cutoff = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk expiry write-off: writes off EVERY expired batch (ExpiryDate ≤ cutoff, default today)
    /// with stock, each in its OWN atomic adjustment, and reports totals (count + value lost at
    /// cost and MRP). A single batch failing does not roll back the others.
    /// </summary>
    Task<MasterResult<ExpiryWriteOffSummary>> WriteOffAllExpiredAsync(int userId, UserRole actingRole, DateTime? cutoff = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recent stock adjustments for display + audit (plan.md §6.1), newest first, optionally filtered
    /// by date range / product / batch.
    /// </summary>
    Task<IReadOnlyList<AdjustmentHistoryRow>> GetHistoryAsync(DateTime? from = null, DateTime? to = null, int? productId = null, int? batchId = null, CancellationToken cancellationToken = default);
}
