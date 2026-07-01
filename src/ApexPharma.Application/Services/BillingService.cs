using ApexPharma.Domain.Entities;

namespace ApexPharma.Application.Services;

/// <summary>
/// Skeleton <see cref="IBillingService"/>. Implemented in Phase 1 with transactional
/// stock control and FEFO (plan.md §6.1, §12); QA-tested for non-negative stock and
/// bill-number integrity.
/// </summary>
public class BillingService : IBillingService
{
    public Task<string> CompleteSaleAsync(Sale sale, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task ProcessSaleReturnAsync(string billNo, int batchId, decimal qty, string reason, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<string> GenerateBillNoAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
