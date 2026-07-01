using ApexPharma.Domain.Entities;

namespace ApexPharma.Application.Services;

/// <summary>
/// Purchase / GRN (stock in) (plan.md §6.1). Saving a purchase creates or updates
/// batches and increases batch-level stock, transactionally.
/// </summary>
public interface IPurchaseService
{
    /// <summary>Records a supplier purchase, creating/updating batches and adding stock.</summary>
    Task<int> RecordPurchaseAsync(Purchase purchase, CancellationToken cancellationToken = default);

    /// <summary>Processes a purchase return to the supplier, decrementing the batch.</summary>
    Task ProcessPurchaseReturnAsync(int purchaseId, int batchId, decimal qty, string reason, CancellationToken cancellationToken = default);
}
