using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services;

/// <summary>
/// Skeleton <see cref="IInventoryService"/>. Implemented in Phase 1 (FEFO,
/// non-negative stock, alerts) with QA coverage (plan.md §12).
/// </summary>
public class InventoryService : IInventoryService
{
    public Task<Batch?> SelectBatchFefoAsync(int productId, decimal requiredQty, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task AdjustStockAsync(int batchId, AdjustmentType type, decimal qtyDelta, string reason, int userId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<Product>> GetLowStockAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<Batch>> GetNearExpiryAsync(int withinDays, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
