using System.ComponentModel.DataAnnotations.Schema;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A payment made to a supplier against their account (plan.md §3 lightweight ledger).
/// Money OUT: recording one reduces the supplier's <b>derived</b> payable
/// (<c>OpeningBalance + ΣPurchase.Total − ΣPurchaseReturn − ΣSupplierPayment</c>) — there is
/// deliberately no stored supplier-balance column, so the payable is always reconstructed from
/// history and can never drift. Kept as its own delete-restricted entity so payment history
/// survives (mirrors <see cref="PurchaseReturn"/>).
/// </summary>
public class SupplierPayment
{
    public int SupplierPaymentId { get; set; }

    /// <summary>The supplier this payment settles.</summary>
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>Amount paid (positive; the service blocks a payment exceeding the derived payable).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>When the payment was made (stored UTC; the UI converts to local for display).</summary>
    public DateTime PaymentDate { get; set; }

    /// <summary>How the payment was settled (cash / UPI / card / — reuses the sale payment modes).</summary>
    public PaymentMode PaymentMode { get; set; }

    /// <summary>Optional external reference (cheque no., UPI txn id, NEFT UTR) for reconciliation.</summary>
    public string? Reference { get; set; }

    /// <summary>Optional free-text note.</summary>
    public string? Note { get; set; }

    /// <summary>FK to the <see cref="User"/> who recorded the payment (audit — plan.md §4).</summary>
    public int CreatedBy { get; set; }
    public User? CreatedByUser { get; set; }
}
