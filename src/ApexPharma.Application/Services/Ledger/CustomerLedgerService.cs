using ApexPharma.Application.Services.MasterData;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services.Ledger;

/// <summary>
/// Concrete <see cref="ICustomerLedgerService"/> (plan.md §3, §6.1, §11).
/// <para>
/// Recording a receipt mirrors the balance-reducing transaction used by
/// <see cref="SaleReturnService"/>: it loads the customer INSIDE one ACID transaction, blocks a
/// receipt greater than the current <see cref="Customer.Balance"/> (never below zero), reduces the
/// balance, writes the <see cref="CustomerReceipt"/> audit row, saves, and commits — rolling back
/// (and rethrowing) on any failure.
/// </para>
/// <para>
/// The statement is <b>derived</b>: khata-affecting rows are materialised then the running balance
/// is accumulated in memory (SQLite's EF provider is brittle translating grouped decimal SUMs —
/// same reason as <see cref="SaleReturnService.ReturnedQtyByItemAsync"/>). Only credit sales,
/// sales-returns on credit sales, and receipts appear, so an all-time closing balance reconciles
/// exactly to <see cref="Customer.Balance"/>; cash sales are excluded.
/// </para>
/// </summary>
public sealed class CustomerLedgerService : ICustomerLedgerService
{
    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;

    public CustomerLedgerService(ApexPharmaDbContext db, IAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <inheritdoc />
    public async Task<MasterResult<CustomerReceipt>> RecordReceiptAsync(
        CustomerReceiptInput input, int userId, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        // RBAC: collecting a receipt is a counter action, gated on DoBilling (plan.md §4).
        if (!_auth.HasPermission(actingRole, Permission.DoBilling))
        {
            return MasterResult<CustomerReceipt>.Fail("You do not have permission to record customer receipts.");
        }

        if (input is null)
        {
            return MasterResult<CustomerReceipt>.Fail("Receipt details are required.");
        }

        if (input.Amount <= 0)
        {
            return MasterResult<CustomerReceipt>.Fail("Receipt amount must be greater than zero.");
        }

        // ONE ACID transaction: load the customer, check the balance, reduce it, and write the
        // receipt — all commit together or roll back (plan.md §12). Loading INSIDE the transaction
        // means a concurrent receipt can't both pass the balance check for the same customer.
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            Customer? customer = await _db.Customers
                .FirstOrDefaultAsync(c => c.CustomerId == input.CustomerId, cancellationToken);
            if (customer is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return MasterResult<CustomerReceipt>.Fail("A valid customer is required.");
            }

            // Over-payment blocked for v1: a receipt can never drive the khata below zero
            // (symmetric with the sales-return non-negative floor). Advances are additive later.
            if (input.Amount > customer.Balance)
            {
                await tx.RollbackAsync(cancellationToken);
                return MasterResult<CustomerReceipt>.Fail(
                    $"Receipt of {input.Amount:0.00} exceeds the outstanding balance of {customer.Balance:0.00}.");
            }

            customer.Balance -= input.Amount;

            var receipt = new CustomerReceipt
            {
                CustomerId = customer.CustomerId,
                Amount = input.Amount,
                ReceiptDate = input.ReceiptDate ?? DateTime.UtcNow,
                PaymentMode = input.PaymentMode,
                Reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim(),
                Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
                CreatedBy = userId,
            };

            await _db.CustomerReceipts.AddAsync(receipt, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return MasterResult<CustomerReceipt>.Ok(receipt);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MasterResult<PartyStatement>> GetStatementAsync(
        int customerId, DateTime fromDate, DateTime toDate, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        // RBAC: a statement is a report — gated on ViewReports (plan.md §4).
        if (!_auth.HasPermission(actingRole, Permission.ViewReports))
        {
            return MasterResult<PartyStatement>.Fail("You do not have permission to view ledger statements.");
        }

        Customer? customer = await _db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
        if (customer is null)
        {
            return MasterResult<PartyStatement>.Fail("A valid customer is required.");
        }

        DateTime from = fromDate.Date;
        DateTime to = toDate.Date;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        DateTime toExclusive = to.AddDays(1);

        // Materialise every khata-affecting transaction for this customer, then accumulate in
        // memory. Each is a (date, docType, refNo, debit, credit) tuple; the running balance is
        // computed after ordering. Reads are AsNoTracking (pure query, plan.md §6.1).

        // Credit sales = DEBIT (the khata grows). Cash sales are excluded on purpose.
        List<LedgerTxn> txns = new();

        List<(DateTime Date, string BillNo, decimal Total, int SaleId)> creditSales = (await _db.Sales.AsNoTracking()
            .Where(s => s.CustomerId == customerId && s.PaymentMode == PaymentMode.Credit)
            .Select(s => new { s.BillDate, s.BillNo, s.Total, s.SaleId })
            .ToListAsync(cancellationToken))
            .Select(x => (x.BillDate, x.BillNo, x.Total, x.SaleId))
            .ToList();
        foreach (var s in creditSales)
        {
            txns.Add(new LedgerTxn(s.Date, "Credit sale", s.BillNo, s.Total, 0m, s.SaleId, 0));
        }

        // Sales-returns on this customer's CREDIT sales = CREDIT (the khata shrinks). Only returns
        // whose parent sale was on credit affect the khata (a cash-sale return never touched it).
        List<(DateTime Date, string BillNo, decimal Amount, int ReturnId)> returns = (await _db.SaleReturns.AsNoTracking()
            .Where(r => r.Sale!.CustomerId == customerId && r.Sale.PaymentMode == PaymentMode.Credit)
            .Select(r => new { r.Date, r.Sale!.BillNo, r.Amount, r.ReturnId })
            .ToListAsync(cancellationToken))
            .Select(x => (x.Date, x.BillNo, x.Amount, x.ReturnId))
            .ToList();
        foreach (var r in returns)
        {
            txns.Add(new LedgerTxn(r.Date, "Sales return", r.BillNo, 0m, r.Amount, r.ReturnId, 1));
        }

        // Receipts = CREDIT (the khata shrinks).
        List<(DateTime Date, decimal Amount, int ReceiptId)> receipts = (await _db.CustomerReceipts.AsNoTracking()
            .Where(cr => cr.CustomerId == customerId)
            .Select(cr => new { cr.ReceiptDate, cr.Amount, cr.CustomerReceiptId })
            .ToListAsync(cancellationToken))
            .Select(x => (x.ReceiptDate, x.Amount, x.CustomerReceiptId))
            .ToList();
        foreach (var r in receipts)
        {
            txns.Add(new LedgerTxn(r.Date, "Receipt", $"RCPT-{r.ReceiptId}", 0m, r.Amount, r.ReceiptId, 2));
        }

        // The customer has no stored opening-balance field, so the ledger opening constant is 0;
        // the window opening is that constant plus the net effect of every transaction strictly
        // before the window (carry-forward). balance += debit − credit throughout.
        PartyStatement statement = LedgerMath.BuildStatement(
            customer.Name, openingConstant: 0m, txns, from, to, toExclusive);
        return MasterResult<PartyStatement>.Ok(statement);
    }
}
