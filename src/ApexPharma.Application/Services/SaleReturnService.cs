using ApexPharma.Application.Services.MasterData;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services;

/// <summary>
/// Concrete sales-return service (plan.md §6.1, §12). Reversing a sale is the stock-in-from-customer
/// path: in ONE ACID transaction it, per returned line,
/// <list type="bullet">
///   <item>validates the return qty is &gt; 0 and ≤ (sold − already-returned) for that line — over-return
///   is refused so a line can never be reversed beyond what it sold (cumulative tracking via
///   <see cref="SaleReturn.SaleItemId"/>);</item>
///   <item>restocks the EXACT batch the line was dispensed from (<see cref="SaleItem.BatchId"/>) —
///   increments <see cref="Batch.QtyOnHand"/>;</item>
///   <item>reverses the returned value's CGST/SGST proportionally to the returned qty, consistent with
///   how the sale computed the line GST; records a <see cref="SaleReturn"/> with the amounts + reason +
///   date + <c>CreatedBy</c>;</item>
///   <item>for a credit (khata) sale, reduces the customer's <see cref="Customer.Balance"/> by the
///   returned total — floored at the effect of THIS sale so an unrelated balance is never driven
///   negative.</item>
/// </list>
/// Any failure rolls everything back (plan.md §12). Gated on <see cref="Permission.DoBilling"/>.
/// Expected failures are returned, not thrown (plan.md §6.2). No money/stock rule lives in the UI (plan.md §8).
/// </summary>
public class SaleReturnService : ISaleReturnService
{
    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;

    public SaleReturnService(ApexPharmaDbContext db, IAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <inheritdoc />
    public async Task<MasterResult<SaleReturnableLines>> GetReturnableLinesAsync(
        string billNo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(billNo))
        {
            return MasterResult<SaleReturnableLines>.Fail("Enter a bill number.");
        }

        string trimmed = billNo.Trim();
        Sale? sale = await _db.Sales
            .AsNoTracking()
            .Include(s => s.Items)!.ThenInclude(i => i.Product)
            .Include(s => s.Items)!.ThenInclude(i => i.Batch)
            .FirstOrDefaultAsync(s => s.BillNo == trimmed, cancellationToken);

        if (sale is null)
        {
            return MasterResult<SaleReturnableLines>.Fail($"No bill found with number '{trimmed}'.");
        }

        // Already-returned quantity per sale line = SUM of return rows carrying that SaleItemId.
        Dictionary<int, decimal> returnedByItem = await ReturnedQtyByItemAsync(sale.SaleId, cancellationToken);

        var lines = sale.Items
            .OrderBy(i => i.SaleItemId)
            .Select(i => new SaleReturnableLine(
                i.SaleItemId,
                i.ProductId,
                i.Product?.Name ?? string.Empty,
                i.BatchId,
                i.Batch?.BatchNo ?? string.Empty,
                i.Qty,
                returnedByItem.TryGetValue(i.SaleItemId, out decimal r) ? r : 0m,
                i.Rate))
            .ToList();

        return MasterResult<SaleReturnableLines>.Ok(new SaleReturnableLines(
            sale.SaleId, sale.BillNo, sale.BillDate, sale.PaymentMode == PaymentMode.Credit, lines));
    }

    /// <inheritdoc />
    public async Task<MasterResult<SaleReturnReceipt>> ProcessSaleReturnAsync(
        SaleReturnInput input, int userId, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        // RBAC: sales returns are gated on DoBilling (plan.md §4).
        if (!_auth.HasPermission(actingRole, Permission.DoBilling))
        {
            return MasterResult<SaleReturnReceipt>.Fail("You do not have permission to process sales returns.");
        }

        if (input is null)
        {
            return MasterResult<SaleReturnReceipt>.Fail("Return details are required.");
        }

        if (string.IsNullOrWhiteSpace(input.BillNo))
        {
            return MasterResult<SaleReturnReceipt>.Fail("Enter a bill number.");
        }

        // Keep only lines the user actually wants to return (positive qty). A request with no
        // positive line is a no-op and treated as a validation error.
        List<SaleReturnLineInput> requested = (input.Lines ?? new List<SaleReturnLineInput>())
            .Where(l => l is not null && l.Qty > 0)
            .ToList();
        if (requested.Count == 0)
        {
            return MasterResult<SaleReturnReceipt>.Fail("Enter a return quantity for at least one line.");
        }

        // Reject a line requested twice — the caller must consolidate; otherwise the two
        // requests would each pass the remaining-qty check independently and over-return.
        if (requested.Select(l => l.SaleItemId).Distinct().Count() != requested.Count)
        {
            return MasterResult<SaleReturnReceipt>.Fail("A sale line was listed more than once — combine the quantities.");
        }

        string trimmedBill = input.BillNo.Trim();

        // ONE ACID transaction: load the sale + lines, validate every requested line, restock
        // each batch, record the returns, and reduce khata — all commit together or roll back
        // (plan.md §12). We load and mutate INSIDE the transaction so a concurrent return can't
        // both pass the remaining-qty check for the same line.
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            Sale? sale = await _db.Sales
                .Include(s => s.Items)
                .FirstOrDefaultAsync(s => s.BillNo == trimmedBill, cancellationToken);
            if (sale is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return MasterResult<SaleReturnReceipt>.Fail($"No bill found with number '{trimmedBill}'.");
            }

            Dictionary<int, decimal> alreadyReturned = await ReturnedQtyByItemAsync(sale.SaleId, cancellationToken);
            string? reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
            DateTime now = DateTime.UtcNow;

            decimal totalQty = 0m;
            decimal totalTaxable = 0m;
            decimal totalCgst = 0m;
            decimal totalSgst = 0m;
            decimal totalRefund = 0m;

            foreach (SaleReturnLineInput req in requested)
            {
                SaleItem? item = sale.Items.FirstOrDefault(i => i.SaleItemId == req.SaleItemId);
                if (item is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return MasterResult<SaleReturnReceipt>.Fail(
                        "A requested line does not belong to this bill.");
                }

                decimal returnedSoFar = alreadyReturned.TryGetValue(item.SaleItemId, out decimal r) ? r : 0m;
                decimal remaining = item.Qty - returnedSoFar;
                if (req.Qty > remaining)
                {
                    // Over-return blocked (plan.md §6.2, §12).
                    await tx.RollbackAsync(cancellationToken);
                    return MasterResult<SaleReturnReceipt>.Fail(
                        $"Cannot return {req.Qty:0.##} of '{item.SaleItemId}' — only {remaining:0.##} remain returnable " +
                        $"(sold {item.Qty:0.##}, already returned {returnedSoFar:0.##}).");
                }

                // Reverse the line figures proportionally to the returned fraction, consistent with
                // how the sale stored them. The line's NET taxable = LineTotal − Cgst − Sgst (the
                // stored SaleItem already folds line + apportioned bill discount into LineTotal).
                decimal fraction = item.Qty == 0m ? 0m : req.Qty / item.Qty;
                decimal lineNetTaxable = item.LineTotal - item.Cgst - item.Sgst;

                decimal returnTaxable = Math.Round(lineNetTaxable * fraction, 2, MidpointRounding.AwayFromZero);
                decimal returnCgst = Math.Round(item.Cgst * fraction, 2, MidpointRounding.AwayFromZero);
                decimal returnSgst = Math.Round(item.Sgst * fraction, 2, MidpointRounding.AwayFromZero);
                decimal returnAmount = returnTaxable + returnCgst + returnSgst;

                // Restock the EXACT batch this line was dispensed from (plan.md §6.1).
                Batch batch = await _db.Batches.FirstAsync(b => b.BatchId == item.BatchId, cancellationToken);
                batch.QtyOnHand += req.Qty;

                await _db.SaleReturns.AddAsync(new SaleReturn
                {
                    SaleId = sale.SaleId,
                    SaleItemId = item.SaleItemId,
                    BatchId = item.BatchId,
                    Qty = req.Qty,
                    Cgst = returnCgst,
                    Sgst = returnSgst,
                    Amount = returnAmount,
                    Reason = reason,
                    Date = now,
                    CreatedBy = userId,
                }, cancellationToken);

                totalQty += req.Qty;
                totalTaxable += returnTaxable;
                totalCgst += returnCgst;
                totalSgst += returnSgst;
                totalRefund += returnAmount;
            }

            // Credit (khata): reduce the customer's balance by the returned total, but never by
            // more than the effect of THIS sale nor below zero — a return must not drive an
            // unrelated balance negative (plan.md §6.1).
            decimal khataReduced = 0m;
            if (sale.PaymentMode == PaymentMode.Credit && sale.CustomerId is int customerId)
            {
                Customer? customer = await _db.Customers.FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
                if (customer is not null)
                {
                    // Cap the reduction so the balance never goes below zero: a return can only
                    // reverse credit that this sale created, and the running balance is the floor.
                    khataReduced = Math.Min(Math.Min(totalRefund, sale.Total), Math.Max(0m, customer.Balance));
                    customer.Balance -= khataReduced;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return MasterResult<SaleReturnReceipt>.Ok(new SaleReturnReceipt(
                sale.SaleId, sale.BillNo, requested.Count, totalQty,
                totalTaxable, totalCgst, totalSgst, totalRefund, khataReduced));
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Cumulative returned quantity per <see cref="SaleItem"/> for a sale = SUM of the return
    /// rows carrying each <c>SaleItemId</c>. Materialised then grouped in memory (the SQLite EF
    /// provider is brittle translating a grouped decimal SUM; return rows per sale are few).
    /// </summary>
    private async Task<Dictionary<int, decimal>> ReturnedQtyByItemAsync(int saleId, CancellationToken cancellationToken)
    {
        List<(int SaleItemId, decimal Qty)> rows = (await _db.SaleReturns
            .Where(sr => sr.SaleId == saleId && sr.SaleItemId != null)
            .Select(sr => new { sr.SaleItemId, sr.Qty })
            .ToListAsync(cancellationToken))
            .Select(x => (x.SaleItemId!.Value, x.Qty))
            .ToList();

        return rows
            .GroupBy(x => x.SaleItemId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
    }
}
