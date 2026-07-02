using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.Ledger;

/// <summary>
/// Customer khata ledger (plan.md §3, §6.1, §11). Records receipts collected against a customer's
/// outstanding credit and produces a running-balance statement for a date window. Recording a
/// receipt runs in ONE ACID transaction that reduces <see cref="Customer.Balance"/> (the single
/// authoritative running-credit figure) and can never drive it below zero — over-payment is
/// blocked for v1. Recording is gated on <see cref="Permission.DoBilling"/>; viewing a statement
/// on <see cref="Permission.ViewReports"/> (plan.md §4). Expected failures are returned as a failed
/// <see cref="MasterResult{T}"/>, not thrown (plan.md §6.2). No money rule lives in the UI (plan.md §8).
/// </summary>
public interface ICustomerLedgerService
{
    /// <summary>
    /// Records a receipt against the customer's khata: validates the amount is positive and does
    /// not exceed the current <see cref="Customer.Balance"/>, then in one transaction reduces the
    /// balance and writes a <see cref="CustomerReceipt"/> (audit fields set). Returns a clear
    /// failure (nothing persisted) on any validation/authorization error.
    /// </summary>
    Task<MasterResult<CustomerReceipt>> RecordReceiptAsync(
        CustomerReceiptInput input, int userId, UserRole actingRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a read-only khata statement for the customer over the inclusive [from, to] window:
    /// an opening-balance row (net of every khata-affecting transaction strictly before the
    /// window), then each in-window row — credit sales (debit), sales-returns on credit sales
    /// (credit), and receipts (credit) — with a running balance, and the closing balance. Cash
    /// sales are excluded so the closing figure reconciles to <see cref="Customer.Balance"/> for
    /// an all-time window. Gated on <see cref="Permission.ViewReports"/>.
    /// </summary>
    Task<MasterResult<PartyStatement>> GetStatementAsync(
        int customerId, DateTime fromDate, DateTime toDate, UserRole actingRole, CancellationToken cancellationToken = default);
}
