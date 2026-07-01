using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services;

/// <summary>
/// POS billing — the heart of the app (plan.md §6.1, §9). Completing a sale runs in a single
/// ACID transaction that inserts the bill + lines, decrements batch stock via FEFO (never
/// negative), assigns a unique sequential bill number, enforces Schedule H/H1 capture, and —
/// for a credit sale — adds the total to the customer's khata. Any failure rolls the whole
/// sale back so nothing is half-written (plan.md §6.2, §12, §14).
/// </summary>
public interface IBillingService
{
    /// <summary>
    /// Creates and persists a complete POS sale in one transaction, returning the saved-bill
    /// summary (bill number + money roll-up). Returns a failed <see cref="MasterResult{T}"/>
    /// with a clear message for any validation/authorization failure — expected failures are
    /// return values, not exceptions (plan.md §6.2).
    /// </summary>
    /// <param name="input">The bill header + lines to sell.</param>
    /// <param name="actingRole">The signed-in role — gated on <c>DoBilling</c> (plan.md §4).</param>
    /// <param name="actingUserId">The signed-in user id, recorded as the bill's <c>CreatedBy</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<MasterResult<SaleReceipt>> CreateSaleAsync(
        SaleInput input, UserRole actingRole, int actingUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a bill number (e.g. "INV-000007") to its sale id so an earlier receipt can be
    /// reprinted (plan.md §11 reprint-by-bill-no). Returns a failed <see cref="MasterResult{T}"/>
    /// when no sale carries that bill number — an expected failure, not an exception (plan.md §6.2).
    /// </summary>
    Task<MasterResult<int>> FindSaleIdByBillNoAsync(string billNo, CancellationToken cancellationToken = default);
}
