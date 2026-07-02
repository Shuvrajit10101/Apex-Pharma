using ApexPharma.Application.Services.MasterData;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services.Ledger;

/// <summary>
/// Concrete <see cref="ISupplierLedgerService"/> (plan.md §3, §6.1, §11).
/// <para>
/// There is no stored supplier-balance column (confirmed by <see cref="PurchaseService"/>): the
/// payable is always <b>derived</b> inside the transaction as
/// <c>Supplier.OpeningBalance + ΣPurchase.Total − ΣPurchaseReturn.Amount − ΣSupplierPayment.Amount</c>.
/// Recording a payment loads the supplier, recomputes that payable INSIDE one ACID transaction,
/// blocks a payment exceeding it (over-payment is blocked for v1), writes the
/// <see cref="SupplierPayment"/> audit row, saves, and commits — rolling back (and rethrowing) on
/// any failure.
/// </para>
/// <para>
/// The statement is derived the same way as the customer side: materialise purchases (debit),
/// purchase-returns (credit), and payments (credit), then accumulate the running balance in memory
/// (avoiding SQLite's brittle grouped-decimal SUM). Opening carries forward the supplier's stored
/// <see cref="Supplier.OpeningBalance"/> plus the net of everything strictly before the window.
/// </para>
/// </summary>
public sealed class SupplierLedgerService : ISupplierLedgerService
{
    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;

    public SupplierLedgerService(ApexPharmaDbContext db, IAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <inheritdoc />
    public async Task<MasterResult<SupplierPayment>> RecordPaymentAsync(
        SupplierPaymentInput input, int userId, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        // RBAC: paying a supplier is a purchasing action, gated on DoPurchases (plan.md §4).
        if (!_auth.HasPermission(actingRole, Permission.DoPurchases))
        {
            return MasterResult<SupplierPayment>.Fail("You do not have permission to record supplier payments.");
        }

        if (input is null)
        {
            return MasterResult<SupplierPayment>.Fail("Payment details are required.");
        }

        if (input.Amount <= 0)
        {
            return MasterResult<SupplierPayment>.Fail("Payment amount must be greater than zero.");
        }

        // ONE ACID transaction: load the supplier, derive the payable, check it, and write the
        // payment — all commit together or roll back (plan.md §12). Deriving INSIDE the transaction
        // means a concurrent payment can't both pass the payable check for the same supplier.
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            Supplier? supplier = await _db.Suppliers
                .FirstOrDefaultAsync(s => s.SupplierId == input.SupplierId, cancellationToken);
            if (supplier is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return MasterResult<SupplierPayment>.Fail("A valid supplier is required.");
            }

            decimal payable = await DerivePayableAsync(supplier, cancellationToken);

            // Over-payment blocked for v1: a payment can never exceed the derived payable
            // (symmetric with the purchase-return non-negative floor). Advances are additive later.
            if (input.Amount > payable)
            {
                await tx.RollbackAsync(cancellationToken);
                return MasterResult<SupplierPayment>.Fail(
                    $"Payment of {input.Amount:0.00} exceeds the outstanding payable of {payable:0.00}.");
            }

            var payment = new SupplierPayment
            {
                SupplierId = supplier.SupplierId,
                Amount = input.Amount,
                PaymentDate = input.PaymentDate ?? DateTime.UtcNow,
                PaymentMode = input.PaymentMode,
                Reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim(),
                Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
                CreatedBy = userId,
            };

            await _db.SupplierPayments.AddAsync(payment, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return MasterResult<SupplierPayment>.Ok(payment);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MasterResult<PartyStatement>> GetStatementAsync(
        int supplierId, DateTime fromDate, DateTime toDate, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        // RBAC: a statement is a report — gated on ViewReports (plan.md §4).
        if (!_auth.HasPermission(actingRole, Permission.ViewReports))
        {
            return MasterResult<PartyStatement>.Fail("You do not have permission to view ledger statements.");
        }

        Supplier? supplier = await _db.Suppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SupplierId == supplierId, cancellationToken);
        if (supplier is null)
        {
            return MasterResult<PartyStatement>.Fail("A valid supplier is required.");
        }

        DateTime from = fromDate.Date;
        DateTime to = toDate.Date;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        DateTime toExclusive = to.AddDays(1);

        var txns = new List<LedgerTxn>();

        // Purchases = DEBIT (we owe the supplier more).
        List<(DateTime Date, string RefNo, decimal Total, int PurchaseId)> purchases = (await _db.Purchases.AsNoTracking()
            .Where(p => p.SupplierId == supplierId)
            .Select(p => new { p.InvoiceDate, p.SupplierInvoiceNo, p.Total, p.PurchaseId })
            .ToListAsync(cancellationToken))
            .Select(x => (x.InvoiceDate, x.SupplierInvoiceNo ?? $"PUR-{x.PurchaseId}", x.Total, x.PurchaseId))
            .ToList();
        foreach (var p in purchases)
        {
            txns.Add(new LedgerTxn(p.Date, "Purchase", p.RefNo, p.Total, 0m, p.PurchaseId, 0));
        }

        // Purchase-returns against this supplier's purchases = CREDIT (we owe less).
        List<(DateTime Date, string RefNo, decimal Amount, int ReturnId)> returns = (await _db.PurchaseReturns.AsNoTracking()
            .Where(r => r.Purchase!.SupplierId == supplierId)
            .Select(r => new { r.Date, r.Purchase!.SupplierInvoiceNo, r.Purchase.PurchaseId, r.Amount, r.ReturnId })
            .ToListAsync(cancellationToken))
            .Select(x => (x.Date, x.SupplierInvoiceNo ?? $"PUR-{x.PurchaseId}", x.Amount, x.ReturnId))
            .ToList();
        foreach (var r in returns)
        {
            txns.Add(new LedgerTxn(r.Date, "Purchase return", r.RefNo, 0m, r.Amount, r.ReturnId, 1));
        }

        // Payments = CREDIT (we owe less).
        List<(DateTime Date, decimal Amount, int PaymentId)> payments = (await _db.SupplierPayments.AsNoTracking()
            .Where(sp => sp.SupplierId == supplierId)
            .Select(sp => new { sp.PaymentDate, sp.Amount, sp.SupplierPaymentId })
            .ToListAsync(cancellationToken))
            .Select(x => (x.PaymentDate, x.Amount, x.SupplierPaymentId))
            .ToList();
        foreach (var p in payments)
        {
            txns.Add(new LedgerTxn(p.Date, "Payment", $"PMT-{p.PaymentId}", 0m, p.Amount, p.PaymentId, 2));
        }

        // Supplier opening constant is its stored OpeningBalance (plan.md §3). The window opening
        // is that plus the net effect of everything strictly before the window (carry-forward).
        PartyStatement statement = LedgerMath.BuildStatement(
            supplier.Name, openingConstant: supplier.OpeningBalance, txns, from, to, toExclusive);
        return MasterResult<PartyStatement>.Ok(statement);
    }

    /// <summary>
    /// Derived supplier payable = <c>OpeningBalance + ΣPurchase.Total − ΣPurchaseReturn.Amount −
    /// ΣSupplierPayment.Amount</c>. Each sum is materialised then aggregated in memory (the SQLite
    /// EF provider is brittle translating a grouped/aggregated decimal SUM — consistent with the
    /// rest of the codebase). There is no stored balance to keep in sync, so the figure can never drift.
    /// </summary>
    private async Task<decimal> DerivePayableAsync(Supplier supplier, CancellationToken cancellationToken)
    {
        List<decimal> purchaseTotals = await _db.Purchases
            .Where(p => p.SupplierId == supplier.SupplierId)
            .Select(p => p.Total)
            .ToListAsync(cancellationToken);

        List<decimal> returnAmounts = await _db.PurchaseReturns
            .Where(r => r.Purchase!.SupplierId == supplier.SupplierId)
            .Select(r => r.Amount)
            .ToListAsync(cancellationToken);

        List<decimal> paymentAmounts = await _db.SupplierPayments
            .Where(sp => sp.SupplierId == supplier.SupplierId)
            .Select(sp => sp.Amount)
            .ToListAsync(cancellationToken);

        return supplier.OpeningBalance + purchaseTotals.Sum() - returnAmounts.Sum() - paymentAmounts.Sum();
    }
}
