using System.ComponentModel.DataAnnotations.Schema;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A receipt collected from a customer against their khata (plan.md §3 lightweight ledger).
/// Money IN: recording one reduces the customer's outstanding <see cref="Customer.Balance"/> in
/// an ACID transaction — <c>Customer.Balance</c> stays the single authoritative running credit
/// figure (the same field billing and sales-returns already mutate). A receipt can never drive
/// the balance below zero (over-payment is blocked for v1). Kept as its own delete-restricted
/// entity so receipt history survives (mirrors <see cref="SaleReturn"/>).
/// </summary>
public class CustomerReceipt
{
    public int CustomerReceiptId { get; set; }

    /// <summary>The customer whose khata this receipt reduces.</summary>
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>Amount received (positive; the service blocks a receipt exceeding the balance).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>When the receipt was collected (stored UTC; the UI converts to local for display).</summary>
    public DateTime ReceiptDate { get; set; }

    /// <summary>How the receipt was settled (cash / UPI / card — reuses the sale payment modes).</summary>
    public PaymentMode PaymentMode { get; set; }

    /// <summary>Optional external reference (UPI txn id, cheque no.) for reconciliation.</summary>
    public string? Reference { get; set; }

    /// <summary>Optional free-text note.</summary>
    public string? Note { get; set; }

    /// <summary>FK to the <see cref="User"/> who recorded the receipt (audit — plan.md §4).</summary>
    public int CreatedBy { get; set; }
    public User? CreatedByUser { get; set; }
}
