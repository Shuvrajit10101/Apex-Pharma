using ApexPharma.Domain.Entities;

namespace ApexPharma.Application.Services;

/// <summary>
/// Skeleton <see cref="IPurchaseService"/>. Implemented in Phase 1 (batch creation,
/// transactional stock-in) (plan.md §6.1).
/// </summary>
public class PurchaseService : IPurchaseService
{
    public Task<int> RecordPurchaseAsync(Purchase purchase, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task ProcessPurchaseReturnAsync(int purchaseId, int batchId, decimal qty, string reason, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
