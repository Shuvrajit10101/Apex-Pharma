using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services;

/// <summary>
/// Batch-level stock operations (plan.md §6.1). Owns FEFO batch selection,
/// near-expiry/low-stock alerts, and audited stock adjustments.
/// </summary>
public interface IInventoryService
{
    /// <summary>Selects the earliest-expiry non-expired batch with enough stock (FEFO).</summary>
    Task<Batch?> SelectBatchFefoAsync(int productId, decimal requiredQty, CancellationToken cancellationToken = default);

    /// <summary>Records an audited stock adjustment (expiry/breakage/count).</summary>
    Task AdjustStockAsync(int batchId, AdjustmentType type, decimal qtyDelta, string reason, int userId, CancellationToken cancellationToken = default);

    /// <summary>Products at or below their reorder level.</summary>
    Task<IReadOnlyList<Product>> GetLowStockAsync(CancellationToken cancellationToken = default);

    /// <summary>Batches expiring within the given window (near-expiry alert).</summary>
    Task<IReadOnlyList<Batch>> GetNearExpiryAsync(int withinDays, CancellationToken cancellationToken = default);
}
