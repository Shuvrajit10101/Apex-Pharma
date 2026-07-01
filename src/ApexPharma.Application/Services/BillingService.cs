using System.Globalization;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services;

/// <summary>
/// Concrete POS billing service (plan.md §6.1, §9, §12, §14). Creating a sale is the stock-out
/// path: in ONE ACID transaction it
/// <list type="bullet">
///   <item>dispenses each line by <b>FEFO</b> — earliest-expiry non-expired batch(es), spanning
///   multiple lots when needed; expired batches are never used;</item>
///   <item>rejects the whole sale (nothing persisted) if total available stock can't cover a line;</item>
///   <item>computes per-line CGST/SGST from the product's GST rate via <see cref="IGstService"/>
///   and rolls up the header totals with a rounding-to-whole-rupee round-off;</item>
///   <item>enforces Schedule H/H1 capture (doctor + Rx) when any line is a scheduled drug (plan.md §14);</item>
///   <item>assigns a unique, sequential <see cref="Sale.BillNo"/> from a <see cref="Setting"/>-backed
///   counter (gap-safe under rapid sales because it is read+incremented inside the transaction);</item>
///   <item>decrements each dispensing batch's stock — never negative; and</item>
///   <item>for a <see cref="PaymentMode.Credit"/> sale, requires a customer and adds the total to
///   their khata (<see cref="Customer.Balance"/>).</item>
/// </list>
/// Any failure rolls everything back (plan.md §12). Expected validation/authorization failures
/// are returned as a failed <see cref="MasterResult{T}"/>, not thrown (plan.md §6.2). No money or
/// stock rule lives in the UI (plan.md §8 layering).
/// </summary>
public class BillingService : IBillingService
{
    /// <summary>Setting key holding the next bill sequence number (gap-safe counter).</summary>
    private const string BillCounterKey = "Billing.NextBillNo";

    /// <summary>Prefix + zero-padding for the human-facing bill number (e.g. INV-000001).</summary>
    private const string BillPrefix = "INV-";
    private const int BillNumberWidth = 6;

    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;
    private readonly IGstService _gst;

    public BillingService(ApexPharmaDbContext db, IAuthService auth, IGstService gst)
    {
        _db = db;
        _auth = auth;
        _gst = gst;
    }

    /// <inheritdoc />
    public async Task<MasterResult<SaleReceipt>> CreateSaleAsync(
        SaleInput input, UserRole actingRole, int actingUserId, CancellationToken cancellationToken = default)
    {
        // RBAC: only roles with DoBilling may sell (Owner/Pharmacist/Cashier — plan.md §4).
        if (!_auth.HasPermission(actingRole, Permission.DoBilling))
        {
            return MasterResult<SaleReceipt>.Fail("You do not have permission to create sales.");
        }

        if (input is null)
        {
            return MasterResult<SaleReceipt>.Fail("Sale details are required.");
        }

        if (input.Lines is null || input.Lines.Count == 0)
        {
            return MasterResult<SaleReceipt>.Fail("A sale must have at least one line item.");
        }

        if (input.BillDiscount < 0)
        {
            return MasterResult<SaleReceipt>.Fail("Bill discount cannot be negative.");
        }

        foreach (SaleLineInput line in input.Lines)
        {
            if (line is null)
            {
                return MasterResult<SaleReceipt>.Fail("A sale line is missing.");
            }

            if (line.Qty <= 0)
            {
                return MasterResult<SaleReceipt>.Fail("Each line quantity must be greater than zero.");
            }

            if (line.LineDiscount < 0)
            {
                return MasterResult<SaleReceipt>.Fail("Line discount cannot be negative.");
            }
        }

        // One ACID transaction: FEFO dispense + bill numbering + stock decrement + header +
        // lines + khata all commit together or not at all (plan.md §12). We load products and
        // batches INSIDE the transaction and decrement transactionally so two rapid sales can't
        // both pass the availability check for the same lot.
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            DateTime today = DateTime.UtcNow.Date;

            var sale = new Sale
            {
                CustomerId = input.CustomerId,
                DoctorName = Nullify(input.DoctorName),
                PrescriptionRef = Nullify(input.PrescriptionRef),
                PaymentMode = input.PaymentMode,
                BillDate = DateTime.UtcNow,
                CreatedBy = actingUserId,
            };

            decimal subtotal = 0m;      // taxable base after all discounts
            decimal totalDiscount = 0m; // line discounts + bill discount
            decimal totalCgst = 0m;
            decimal totalSgst = 0m;

            bool anyScheduled = false;

            // Track batch quantity already committed to earlier lines in THIS sale so two lines
            // for the same product can't both claim the same on-hand units.
            var claimedByBatch = new Dictionary<int, decimal>();

            foreach (SaleLineInput line in input.Lines)
            {
                Product? product = await _db.Products
                    .FirstOrDefaultAsync(p => p.ProductId == line.ProductId, cancellationToken);
                if (product is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return MasterResult<SaleReceipt>.Fail("A line references a product that does not exist.");
                }

                if (!product.IsActive)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return MasterResult<SaleReceipt>.Fail($"Product '{product.Name}' is inactive and cannot be sold.");
                }

                if (product.Schedule is DrugSchedule.H or DrugSchedule.H1)
                {
                    anyScheduled = true;
                }

                // FEFO batch list for this product: earliest-expiry non-expired lots with stock.
                // We pull the ordered candidates and walk them, dispensing across lots as needed.
                List<Batch> candidates = await _db.Batches
                    .Where(b => b.ProductId == line.ProductId
                                && b.ExpiryDate > today
                                && b.QtyOnHand > 0)
                    .OrderBy(b => b.ExpiryDate)
                    .ThenBy(b => b.BatchId)
                    .ToListAsync(cancellationToken);

                decimal remaining = line.Qty;

                // Line taxable base BEFORE discount = sum of (rate × qty) dispensed per lot;
                // the batch SalePrice is the rate (plan.md §6.1). We build SaleItems per lot.
                var lineItems = new List<SaleItem>();
                decimal lineTaxableGross = 0m;

                foreach (Batch batch in candidates)
                {
                    if (remaining <= 0)
                    {
                        break;
                    }

                    decimal alreadyClaimed = claimedByBatch.TryGetValue(batch.BatchId, out decimal c) ? c : 0m;
                    decimal available = batch.QtyOnHand - alreadyClaimed;
                    if (available <= 0)
                    {
                        continue;
                    }

                    decimal take = Math.Min(available, remaining);
                    remaining -= take;
                    claimedByBatch[batch.BatchId] = alreadyClaimed + take;

                    decimal lotGross = batch.SalePrice * take;
                    lineTaxableGross += lotGross;

                    lineItems.Add(new SaleItem
                    {
                        BatchId = batch.BatchId,
                        ProductId = product.ProductId,
                        Qty = take,
                        Mrp = batch.Mrp,
                        Rate = batch.SalePrice,
                        GstRate = product.GstRate,
                    });
                }

                if (remaining > 0)
                {
                    // Not enough non-expired stock across all lots — reject the WHOLE sale.
                    await tx.RollbackAsync(cancellationToken);
                    return MasterResult<SaleReceipt>.Fail(
                        $"Insufficient stock for '{product.Name}': {line.Qty:0.##} requested but only " +
                        $"{line.Qty - remaining:0.##} available in non-expired batches.");
                }

                // Apply the line discount to the line's pre-tax value, then compute GST on the
                // net taxable amount (discount applies BEFORE tax — plan.md §12).
                decimal lineDiscount = Math.Min(line.LineDiscount, lineTaxableGross);
                decimal lineTaxableNet = lineTaxableGross - lineDiscount;
                totalDiscount += lineDiscount;

                // Distribute the line's net taxable + discount + GST across its lots so each
                // SaleItem's stored figures reconcile to the header. GST is computed on the whole
                // line's net base once, then split proportionally to keep rounding stable.
                GstResult lineGst = _gst.CalculateLineGst(lineTaxableNet, product.GstRate);
                subtotal += lineTaxableNet;
                totalCgst += lineGst.Cgst;
                totalSgst += lineGst.Sgst;

                DistributeLineFigures(lineItems, lineTaxableGross, lineDiscount, lineGst);

                foreach (SaleItem item in lineItems)
                {
                    sale.Items.Add(item);
                }
            }

            // Schedule H/H1: doctor + prescription reference are legally required (plan.md §14).
            if (anyScheduled && (string.IsNullOrWhiteSpace(sale.DoctorName) || string.IsNullOrWhiteSpace(sale.PrescriptionRef)))
            {
                await tx.RollbackAsync(cancellationToken);
                return MasterResult<SaleReceipt>.Fail(
                    "This sale includes a Schedule H/H1 drug — doctor name and prescription reference are required.");
            }

            // Whole-bill discount applies on the post-line-discount subtotal, before nothing
            // further (GST was already computed per line; the bill discount reduces the amount
            // collected but we keep the tax as invoiced per line to stay consistent with the
            // printed line GST). Guard it against exceeding the subtotal.
            decimal billDiscount = Math.Min(input.BillDiscount, subtotal);
            totalDiscount += billDiscount;
            subtotal -= billDiscount;

            // Grand total = taxable subtotal + CGST + SGST, rounded to the nearest whole rupee;
            // round_off carries the adjustment so the printed total is clean (plan.md §6.1).
            decimal preRound = subtotal + totalCgst + totalSgst;
            decimal total = Math.Round(preRound, 0, MidpointRounding.AwayFromZero);
            decimal roundOff = total - preRound;

            // Credit (khata): a customer is required and the total is added to their balance
            // (plan.md §6.1). Cash/Upi/Card need no customer.
            Customer? customer = null;
            if (input.CustomerId is int customerId)
            {
                customer = await _db.Customers.FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
                if (customer is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return MasterResult<SaleReceipt>.Fail("The selected customer does not exist.");
                }
            }

            if (input.PaymentMode == PaymentMode.Credit)
            {
                if (customer is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return MasterResult<SaleReceipt>.Fail("A credit (khata) sale requires a customer.");
                }

                customer.Balance += total;
            }

            sale.Subtotal = subtotal;
            sale.Discount = totalDiscount;
            sale.Cgst = totalCgst;
            sale.Sgst = totalSgst;
            sale.RoundOff = roundOff;
            sale.Total = total;

            // Decrement each dispensing batch's stock (never negative — the claimed amounts were
            // capped at availability above; re-check defensively inside the tx).
            foreach ((int batchId, decimal claimed) in claimedByBatch)
            {
                Batch batch = await _db.Batches.FirstAsync(b => b.BatchId == batchId, cancellationToken);
                if (batch.QtyOnHand < claimed)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return MasterResult<SaleReceipt>.Fail(
                        "Stock changed while completing the sale — please retry.");
                }

                batch.QtyOnHand -= claimed;
            }

            // Assign a unique, sequential bill number from the transactional counter.
            sale.BillNo = await NextBillNoAsync(cancellationToken);

            await _db.Sales.AddAsync(sale, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return MasterResult<SaleReceipt>.Ok(new SaleReceipt(
                sale.SaleId, sale.BillNo, sale.Subtotal, sale.Discount,
                sale.Cgst, sale.Sgst, sale.RoundOff, sale.Total));
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Reserves and returns the next unique, gap-free bill number from a <see cref="Setting"/>
    /// counter, initialising it from the current MAX(existing sequence)+1 on first use so it is
    /// correct even against a pre-existing sales table. Because this runs inside the caller's
    /// transaction, two rapid sales serialise on the counter row and can never collide (the
    /// UNIQUE index on <see cref="Sale.BillNo"/> is the DB backstop).
    /// </summary>
    private async Task<string> NextBillNoAsync(CancellationToken cancellationToken)
    {
        Setting? counter = await _db.Settings.FirstOrDefaultAsync(s => s.Key == BillCounterKey, cancellationToken);

        long next;
        if (counter is null || !long.TryParse(counter.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long stored))
        {
            // First bill (or a corrupt/missing counter): seed from the highest existing sequence.
            long maxExisting = await MaxExistingSequenceAsync(cancellationToken);
            next = maxExisting + 1;
        }
        else
        {
            next = stored;
        }

        string billNo = BillPrefix + next.ToString(CultureInfo.InvariantCulture).PadLeft(BillNumberWidth, '0');

        long following = next + 1;
        if (counter is null)
        {
            await _db.Settings.AddAsync(new Setting { Key = BillCounterKey, Value = following.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
        }
        else
        {
            counter.Value = following.ToString(CultureInfo.InvariantCulture);
        }

        return billNo;
    }

    /// <summary>
    /// Highest numeric sequence already used in <see cref="Sale.BillNo"/> (parsing the digits
    /// after the prefix), or 0 when there are none. Used only to seed the counter.
    /// </summary>
    private async Task<long> MaxExistingSequenceAsync(CancellationToken cancellationToken)
    {
        List<string> billNos = await _db.Sales.Select(s => s.BillNo).ToListAsync(cancellationToken);
        long max = 0;
        foreach (string billNo in billNos)
        {
            string digits = billNo.StartsWith(BillPrefix, StringComparison.Ordinal)
                ? billNo[BillPrefix.Length..]
                : billNo;
            if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) && value > max)
            {
                max = value;
            }
        }

        return max;
    }

    /// <summary>
    /// Splits a line's total discount and GST across its per-lot <see cref="SaleItem"/>s so each
    /// item's stored Discount/Cgst/Sgst/LineTotal reconcile to the header. The last lot absorbs
    /// any rounding remainder so the parts sum exactly to the line figures.
    /// </summary>
    private static void DistributeLineFigures(List<SaleItem> lineItems, decimal lineTaxableGross, decimal lineDiscount, GstResult lineGst)
    {
        if (lineItems.Count == 0)
        {
            return;
        }

        decimal allocatedDiscount = 0m;
        decimal allocatedCgst = 0m;
        decimal allocatedSgst = 0m;

        for (int i = 0; i < lineItems.Count; i++)
        {
            SaleItem item = lineItems[i];
            decimal lotGross = item.Rate * item.Qty;
            bool last = i == lineItems.Count - 1;

            decimal share = lineTaxableGross > 0 ? lotGross / lineTaxableGross : 0m;

            decimal itemDiscount = last
                ? lineDiscount - allocatedDiscount
                : Math.Round(lineDiscount * share, 2, MidpointRounding.AwayFromZero);
            decimal itemCgst = last
                ? lineGst.Cgst - allocatedCgst
                : Math.Round(lineGst.Cgst * share, 2, MidpointRounding.AwayFromZero);
            decimal itemSgst = last
                ? lineGst.Sgst - allocatedSgst
                : Math.Round(lineGst.Sgst * share, 2, MidpointRounding.AwayFromZero);

            allocatedDiscount += itemDiscount;
            allocatedCgst += itemCgst;
            allocatedSgst += itemSgst;

            item.Discount = itemDiscount;
            item.Cgst = itemCgst;
            item.Sgst = itemSgst;
            // Line gross = net taxable (gross − discount) + its GST.
            item.LineTotal = lotGross - itemDiscount + itemCgst + itemSgst;
        }
    }

    private static string? Nullify(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
