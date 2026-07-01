using ApexPharma.Domain.Entities;

namespace ApexPharma.Application.Services;

/// <summary>
/// POS billing — the heart of the app (plan.md §6.1, §9). Completing a sale runs in
/// a single ACID transaction that inserts the bill + lines, decrements batch stock
/// (never negative), and assigns a unique sequential bill number.
/// </summary>
public interface IBillingService
{
    /// <summary>Persists a completed sale transactionally; returns the assigned bill number.</summary>
    Task<string> CompleteSaleAsync(Sale sale, CancellationToken cancellationToken = default);

    /// <summary>Processes a sales return by bill number, restocking the exact batch.</summary>
    Task ProcessSaleReturnAsync(string billNo, int batchId, decimal qty, string reason, CancellationToken cancellationToken = default);

    /// <summary>Reserves and returns the next unique, gap-free bill number.</summary>
    Task<string> GenerateBillNoAsync(CancellationToken cancellationToken = default);
}
