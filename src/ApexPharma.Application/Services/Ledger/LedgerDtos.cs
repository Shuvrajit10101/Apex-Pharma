using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.Ledger;

/// <summary>
/// A request to record a receipt collected from a customer against their khata (plan.md §3).
/// A flat input record (never an EF entity crosses the layer boundary — plan.md §8): the service
/// validates it, blocks over-payment, reduces <see cref="Domain.Entities.Customer.Balance"/>, and
/// writes a <see cref="Domain.Entities.CustomerReceipt"/> in one ACID transaction.
/// </summary>
/// <param name="CustomerId">The customer whose balance this receipt reduces.</param>
/// <param name="Amount">Amount received (must be &gt; 0 and ≤ the customer's current balance).</param>
/// <param name="PaymentMode">How the money was collected (cash / UPI / card).</param>
/// <param name="Reference">Optional external reference (UPI txn id, cheque no.).</param>
/// <param name="Note">Optional free-text note.</param>
/// <param name="ReceiptDate">When the receipt was collected; defaults to now (UTC) when null.</param>
public sealed record CustomerReceiptInput(
    int CustomerId,
    decimal Amount,
    PaymentMode PaymentMode,
    string? Reference = null,
    string? Note = null,
    DateTime? ReceiptDate = null);

/// <summary>
/// A request to record a payment made to a supplier against their account (plan.md §3). The
/// service validates it, computes the derived payable inside the transaction, blocks a payment
/// that would exceed it, and writes a <see cref="Domain.Entities.SupplierPayment"/>.
/// </summary>
/// <param name="SupplierId">The supplier this payment settles.</param>
/// <param name="Amount">Amount paid (must be &gt; 0 and ≤ the derived payable).</param>
/// <param name="PaymentMode">How the money was paid (cash / UPI / card).</param>
/// <param name="Reference">Optional external reference (cheque no., NEFT UTR).</param>
/// <param name="Note">Optional free-text note.</param>
/// <param name="PaymentDate">When the payment was made; defaults to now (UTC) when null.</param>
public sealed record SupplierPaymentInput(
    int SupplierId,
    decimal Amount,
    PaymentMode PaymentMode,
    string? Reference = null,
    string? Note = null,
    DateTime? PaymentDate = null);

/// <summary>
/// One line of a party statement. Every row carries a running balance so the statement reconciles
/// left-to-right: <see cref="Debit"/> increases the amount the party owes us / we owe them,
/// <see cref="Credit"/> reduces it, and <see cref="RunningBalance"/> is the accumulated figure
/// after this row.
/// </summary>
/// <param name="Date">The transaction date (UTC; the UI converts to local for display).</param>
/// <param name="DocType">Human-readable row type, e.g. "Opening balance", "Credit sale", "Purchase", "Receipt", "Payment", "Sales return", "Purchase return".</param>
/// <param name="RefNo">A reference for the row (bill/invoice/receipt number), empty when none.</param>
/// <param name="Debit">Amount added to the outstanding balance by this row (0 for a credit row).</param>
/// <param name="Credit">Amount subtracted from the outstanding balance by this row (0 for a debit row).</param>
/// <param name="RunningBalance">The outstanding balance after applying this row.</param>
public readonly record struct PartyStatementRow(
    DateTime Date,
    string DocType,
    string RefNo,
    decimal Debit,
    decimal Credit,
    decimal RunningBalance);

/// <summary>
/// A complete party (customer or supplier) statement for a date window (plan.md §3, §11). Carries
/// the carried-forward opening balance (net of every in-scope transaction strictly before the
/// window), the in-window rows (each with its running balance), and the closing balance. For an
/// all-time customer window the closing balance reconciles exactly to
/// <see cref="Domain.Entities.Customer.Balance"/>; for a supplier it reconciles to the derived
/// payable. The sign convention is "amount the party owes us / we owe the supplier", positive.
/// </summary>
/// <param name="PartyName">The customer or supplier name.</param>
/// <param name="OpeningBalance">The balance carried into the window (before any in-window row).</param>
/// <param name="Rows">The statement rows (an "Opening balance" row first, then in-window transactions).</param>
/// <param name="ClosingBalance">The balance after the last row.</param>
/// <param name="FromDate">The window's inclusive start date.</param>
/// <param name="ToDate">The window's inclusive end date.</param>
public sealed record PartyStatement(
    string PartyName,
    decimal OpeningBalance,
    IReadOnlyList<PartyStatementRow> Rows,
    decimal ClosingBalance,
    DateTime FromDate,
    DateTime ToDate);
