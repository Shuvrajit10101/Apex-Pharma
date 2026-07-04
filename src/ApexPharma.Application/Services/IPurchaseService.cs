using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services;

/// <summary>
/// Purchase / GRN (stock in) — plan.md §6.1, §9. Recording a purchase creates the
/// <see cref="Purchase"/> header + <see cref="PurchaseItem"/> lines and, in the SAME ACID
/// transaction, upserts a <see cref="Batch"/> per line (increasing batch-level stock).
/// Mutations require the <see cref="Permission.DoPurchases"/> permission (plan.md §4) and
/// return a <see cref="MasterResult"/> rather than throwing for expected validation
/// failures (plan.md §6.2). A purchase with any invalid line persists nothing.
/// </summary>
public interface IPurchaseService
{
    /// <summary>
    /// Records a supplier purchase, creating/updating batches and adding stock in one
    /// transaction. Returns the created <see cref="Purchase"/> on success, or a clear
    /// validation/authorization failure (nothing is persisted on failure).
    /// </summary>
    Task<MasterResult<Purchase>> RecordPurchaseAsync(PurchaseInput input, int userId, UserRole actingRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a quantity to the supplier against a specific purchased line
    /// (<see cref="PurchaseItem"/>), decrementing that line's batch in a transaction. The
    /// return qty must be ≤ (purchased − already-returned) for the line AND must not drive the
    /// batch stock negative — either check refuses the return with a clear message (plan.md §6.2, §12).
    /// </summary>
    Task<MasterResult<PurchaseReturn>> ProcessPurchaseReturnLineAsync(int purchaseItemId, decimal qty, string? reason, int userId, UserRole actingRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a purchase's lines with the quantity already returned and the remaining returnable
    /// quantity per line, so the UI can present a per-line/per-batch return picker. Returns a
    /// failed result when the purchase does not exist.
    /// </summary>
    Task<MasterResult<PurchaseReturnableLines>> GetReturnableLinesAsync(int purchaseId, CancellationToken cancellationToken = default);

    /// <summary>The most recent purchases (header rows) with their supplier + lines, newest first.</summary>
    Task<IReadOnlyList<Purchase>> GetRecentPurchasesAsync(int take = 50, CancellationToken cancellationToken = default);

    /// <summary>Resolves the batch created/updated for a (product, batch-no) — used to target a return.</summary>
    Task<Batch?> FindBatchAsync(int productId, string batchNo, CancellationToken cancellationToken = default);
}
