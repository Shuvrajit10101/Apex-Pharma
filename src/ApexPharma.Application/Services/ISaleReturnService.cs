using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services;

/// <summary>
/// Sales returns (customer → us) — plan.md §6.1, §12. A return is located by bill number and
/// processed per sold line in ONE ACID transaction: it restocks the exact batch each line was
/// dispensed from, reverses the returned value's CGST/SGST proportionally to the returned qty,
/// records a <see cref="Domain.Entities.SaleReturn"/> per line (with per-line tracking so a line
/// can never be over-returned), and — for a credit (khata) sale — reduces the customer's balance
/// by the returned total. Any failure rolls everything back. Gated on
/// <see cref="Permission.DoBilling"/> (plan.md §4). Expected failures are returned as a failed
/// <see cref="MasterResult{T}"/>, not thrown (plan.md §6.2). No money/stock rule lives in the UI.
/// </summary>
public interface ISaleReturnService
{
    /// <summary>
    /// Loads a sale's lines (found by bill number) with the quantity already returned and the
    /// remaining returnable quantity per line, so the UI can present a per-line return picker.
    /// Returns a failed result when the bill number is unknown.
    /// </summary>
    Task<MasterResult<SaleReturnableLines>> GetReturnableLinesAsync(string billNo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes a per-line sales return against the sale identified by <see cref="SaleReturnInput.BillNo"/>,
    /// restocking batches, reversing GST, recording the returns, and reducing khata on a credit sale —
    /// all in one transaction. Returns a clear failure (nothing persisted) on any validation/authorization error.
    /// </summary>
    Task<MasterResult<SaleReturnReceipt>> ProcessSaleReturnAsync(
        SaleReturnInput input, int userId, UserRole actingRole, CancellationToken cancellationToken = default);
}
