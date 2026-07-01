using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services;

/// <summary>
/// Batch-level stock queries and operations (plan.md §6.1). Owns FEFO batch selection for
/// billing, audited stock adjustments, and the read-only views the owner needs after a
/// purchase stocks in: current stock, near-expiry, expired, and low-stock. The read-only
/// query methods perform NO mutations (plan.md §6.1 inventory-ops).
/// </summary>
public interface IInventoryService
{
    /// <summary>The default near-expiry window in days when a caller doesn't specify one (plan.md §6.1).</summary>
    const int DefaultNearExpiryDays = 90;

    /// <summary>Selects the earliest-expiry non-expired batch with enough stock (FEFO).</summary>
    Task<Batch?> SelectBatchFefoAsync(int productId, decimal requiredQty, CancellationToken cancellationToken = default);

    /// <summary>Records an audited stock adjustment (expiry/breakage/count); never drives stock negative.</summary>
    Task AdjustStockAsync(int batchId, AdjustmentType type, decimal qtyDelta, string reason, int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One flattened stock row per batch that has stock on hand, product-then-expiry order,
    /// with near-expiry/expired/low-stock flags for colour-coding (plan.md §6.1, §10).
    /// </summary>
    Task<IReadOnlyList<StockRow>> GetStockAsync(int nearExpiryDays = DefaultNearExpiryDays, CancellationToken cancellationToken = default);

    /// <summary>Total on-hand quantity for a product across all its batches.</summary>
    Task<decimal> GetTotalStockAsync(int productId, CancellationToken cancellationToken = default);

    /// <summary>Batches expiring within the given window but not yet expired (near-expiry alert).</summary>
    Task<IReadOnlyList<Batch>> GetNearExpiryAsync(int withinDays = DefaultNearExpiryDays, CancellationToken cancellationToken = default);

    /// <summary>Batches that are already expired and still carry stock (write-off candidates).</summary>
    Task<IReadOnlyList<Batch>> GetExpiredAsync(CancellationToken cancellationToken = default);

    /// <summary>Active products whose total on-hand is at or below their reorder level (low-stock/reorder).</summary>
    Task<IReadOnlyList<Product>> GetLowStockAsync(CancellationToken cancellationToken = default);
}
