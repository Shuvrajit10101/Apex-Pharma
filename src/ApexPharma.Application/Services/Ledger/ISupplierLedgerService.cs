using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.Ledger;

/// <summary>
/// Supplier account ledger (plan.md §3, §6.1, §11). Records payments made to a supplier and
/// produces a running-balance statement for a date window. There is no stored supplier-balance
/// column: the payable is always <b>derived</b> inside the transaction as
/// <c>Supplier.OpeningBalance + ΣPurchase.Total − ΣPurchaseReturn.Amount − ΣSupplierPayment.Amount</c>,
/// so it can never drift. A payment can never exceed the derived payable — over-payment is blocked
/// for v1. Recording is gated on <see cref="Permission.DoPurchases"/>; viewing a statement on
/// <see cref="Permission.ViewReports"/> (plan.md §4). Expected failures are returned as a failed
/// <see cref="MasterResult{T}"/>, not thrown (plan.md §6.2). No money rule lives in the UI (plan.md §8).
/// </summary>
public interface ISupplierLedgerService
{
    /// <summary>
    /// Records a payment to the supplier: validates the amount is positive and does not exceed the
    /// derived payable computed inside the transaction, then writes a <see cref="SupplierPayment"/>
    /// (audit fields set). Returns a clear failure (nothing persisted) on any validation/authorization error.
    /// </summary>
    Task<MasterResult<SupplierPayment>> RecordPaymentAsync(
        SupplierPaymentInput input, int userId, UserRole actingRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a read-only account statement for the supplier over the inclusive [from, to] window:
    /// an opening-balance row (supplier <see cref="Supplier.OpeningBalance"/> plus the net of every
    /// in-scope transaction strictly before the window), then each in-window row — purchases (debit),
    /// purchase-returns (credit), and payments (credit) — with a running balance, and the closing
    /// balance (which reconciles to the derived payable for an all-time window). Gated on
    /// <see cref="Permission.ViewReports"/>.
    /// </summary>
    Task<MasterResult<PartyStatement>> GetStatementAsync(
        int supplierId, DateTime fromDate, DateTime toDate, UserRole actingRole, CancellationToken cancellationToken = default);
}
