using System.Globalization;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Time;
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
    private readonly ITimeZoneProvider _tz;

    public BillingService(ApexPharmaDbContext db, IAuthService auth, IGstService gst, ITimeZoneProvider tz)
    {
        _db = db;
        _auth = auth;
        _gst = gst;
        _tz = tz;
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
            // "Today" for the FEFO expiry check is the pharmacy's LOCAL (IST) calendar day, NOT the
            // UTC day. A batch is "expired" relative to the day the pharmacy is actually trading in:
            // a sale rung up at 00:30 IST (which is 19:00 the previous day in UTC) must judge expiry
            // against today's IST date, otherwise a batch whose ExpiryDate is that IST date would be
            // wrongly treated as still sellable across the UTC-midnight boundary (plan.md §11, §14).
            // The stored Sale.BillDate below stays UTC — only this day-derivation is localized.
            DateTime today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz.GetPharmacyTimeZone()).Date;

            var sale = new Sale
            {
                CustomerId = input.CustomerId,
                DoctorName = Nullify(input.DoctorName),
                PrescriptionRef = Nullify(input.PrescriptionRef),
                PaymentMode = input.PaymentMode,
                BillDate = DateTime.UtcNow,
                CreatedBy = actingUserId,
            };

            bool anyScheduled = false;
            bool anyScheduleX = false;

            // Track batch quantity already committed to earlier lines in THIS sale so two lines
            // for the same product can't both claim the same on-hand units.
            var claimedByBatch = new Dictionary<int, decimal>();

            // Stage every line first (FEFO dispense + line discount) WITHOUT computing GST. GST is
            // deferred until after the whole-bill discount is apportioned across the lines, so the
            // tax is charged on the post-discount (net) taxable value and the SaleItem figures foot
            // to the header exactly (plan.md §12; GST-on-net for India).
            var stagedLines = new List<StagedLine>();

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

                if (product.Schedule is DrugSchedule.H or DrugSchedule.H1 or DrugSchedule.X)
                {
                    // Every scheduled drug shares the doctor + Rx gate below (plan.md §14).
                    anyScheduled = true;
                }

                if (product.Schedule is DrugSchedule.X)
                {
                    // Schedule X (narcotic/psychotropic) carries the strictest legal controls: on
                    // top of doctor + Rx it needs full patient/prescriber identity, the Rx number/
                    // date, and a retained duplicate copy, and one register row per line (plan.md
                    // §14, §15 — Phase 2f). Enforced after all lines are staged, below.
                    anyScheduleX = true;
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

                // Apply the line discount to the line's pre-tax value (discount applies BEFORE
                // tax — plan.md §12). GST is NOT computed yet: it waits for the bill-discount
                // apportionment below so it lands on the final net taxable.
                decimal lineDiscount = Math.Min(line.LineDiscount, lineTaxableGross);
                decimal lineTaxableNet = lineTaxableGross - lineDiscount;

                stagedLines.Add(new StagedLine(lineItems, product.GstRate, lineTaxableGross, lineDiscount, lineTaxableNet, product.Schedule));
            }

            // Schedule X RBAC (owner-approved — plan.md §4): only a role with DispenseScheduleX
            // (Owner + Pharmacist, NOT Cashier) may dispense a narcotic/psychotropic. Checked FIRST
            // (before the capture-completeness gate below) so a Cashier is refused up front even when
            // the capture is fully filled in. H/H1 lines are unaffected — they need only DoBilling.
            // This is IN ADDITION to the strict dual-Rx capture, which every biller must still supply.
            if (anyScheduleX && !_auth.HasPermission(actingRole, Permission.DispenseScheduleX))
            {
                await tx.RollbackAsync(cancellationToken);
                return MasterResult<SaleReceipt>.Fail("Schedule-X drugs can only be dispensed by a pharmacist.");
            }

            // Schedule X (narcotic/psychotropic): the strict dual-Rx capture is legally required
            // (plan.md §14, §15 — Phase 2f). Validate the WHOLE capture before anything persists:
            // every required field non-blank, a retained duplicate copy, and patient name+address.
            // Any failure rolls the whole sale back (no stock decrement, no bill).
            if (anyScheduleX)
            {
                ScheduleXCapture? cap = input.ScheduleX;
                if (cap is null
                    || string.IsNullOrWhiteSpace(cap.PatientName)
                    || string.IsNullOrWhiteSpace(cap.PatientAddress)
                    || string.IsNullOrWhiteSpace(cap.PrescriberName)
                    || string.IsNullOrWhiteSpace(cap.PrescriberAddress)
                    || string.IsNullOrWhiteSpace(cap.PrescriberRegNo)
                    || string.IsNullOrWhiteSpace(cap.PrescriptionNumber)
                    || cap.PrescriptionDate == default
                    || !cap.PrescriptionRetained)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return MasterResult<SaleReceipt>.Fail(
                        "This sale includes a Schedule X drug — patient name & address, prescriber name, address & " +
                        "registration number, prescription number & date, and a retained prescription copy are all required.");
                }

                // So an X sale still appears in the existing combined H/H1/X register (which derives
                // from Sale.DoctorName / PrescriptionRef), backfill the header from the capture when
                // the biller didn't otherwise supply it — no double data-entry (plan.md §14).
                sale.DoctorName ??= Nullify(cap.PrescriberName);
                sale.PrescriptionRef ??= Nullify(cap.PrescriptionNumber);
            }

            // Schedule H/H1/X: doctor + prescription reference are legally required (plan.md §14).
            if (anyScheduled && (string.IsNullOrWhiteSpace(sale.DoctorName) || string.IsNullOrWhiteSpace(sale.PrescriptionRef)))
            {
                await tx.RollbackAsync(cancellationToken);
                return MasterResult<SaleReceipt>.Fail(
                    "This sale includes a Schedule H/H1/X drug — doctor name and prescription reference are required.");
            }

            // Re-apportion the whole-bill discount across the lines proportionally to their net
            // taxable, so GST is charged on the post-discount value and Σ(SaleItem.LineTotal) foots
            // to the header (GST-correct for India — plan.md §12, §17.5). Largest-remainder keeps
            // the split to the paise and makes Σ apportioned == billDiscount exactly.
            decimal totalNetTaxable = stagedLines.Sum(s => s.TaxableNet);
            decimal billDiscount = Math.Min(input.BillDiscount, totalNetTaxable);
            ApportionBillDiscount(stagedLines, billDiscount, totalNetTaxable);

            decimal subtotal = 0m;      // taxable base after ALL discounts (line + bill)
            decimal totalDiscount = 0m; // line discounts + apportioned bill discount
            decimal totalCgst = 0m;
            decimal totalSgst = 0m;
            SaleItem? lastItem = null;  // last dispensed SaleItem (absorbs the whole-rupee round-off)

            // With the bill discount apportioned, compute each line's GST on its final net taxable,
            // distribute the line figures across its lots, and roll up the header from the lines so
            // header totals == Σ SaleItem figures.
            foreach (StagedLine staged in stagedLines)
            {
                decimal lineNet = staged.TaxableNet - staged.BillDiscountShare;
                decimal lineTotalDiscount = staged.LineDiscount + staged.BillDiscountShare;

                GstResult lineGst = _gst.CalculateLineGst(lineNet, staged.GstRate);
                subtotal += lineNet;
                totalDiscount += lineTotalDiscount;
                totalCgst += lineGst.Cgst;
                totalSgst += lineGst.Sgst;

                DistributeLineFigures(staged.Items, staged.TaxableGross, lineTotalDiscount, lineGst);

                foreach (SaleItem item in staged.Items)
                {
                    sale.Items.Add(item);
                    lastItem = item;
                }
            }

            // Grand total = taxable subtotal + CGST + SGST, rounded to the nearest whole rupee;
            // round_off carries the adjustment so the printed total is clean (plan.md §6.1).
            decimal preRound = subtotal + totalCgst + totalSgst;
            decimal total = Math.Round(preRound, 0, MidpointRounding.AwayFromZero);
            decimal roundOff = total - preRound;

            // Fold the whole-rupee round-off into the last SaleItem so Σ(SaleItem.LineTotal) foots
            // to Sale.Total exactly (the printed line items reconcile to the header — plan.md §17.5).
            if (roundOff != 0m && lastItem is not null)
            {
                lastItem.LineTotal += roundOff;
            }

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

            // Schedule-X strict register: one ScheduleXDispense per Schedule-X SaleItem, written in
            // the SAME transaction so a Schedule-X sale can never persist without its legal register
            // entry (plan.md §14, §15 — Phase 2f). Saved after the sale so SaleId/SaleItemId exist.
            if (anyScheduleX)
            {
                ScheduleXCapture cap = input.ScheduleX!; // validated above
                // Stamp the dispense at the SAME instant as the sale (Sale.BillDate) — it is the
                // same dispense event. Using a fresh UtcNow could bucket the Issued leg (by BillDate)
                // and the dispense-detail row (by DispensedAt) into different narcotic-register
                // windows across a UTC-midnight boundary, so the register would not reconcile.
                DateTime dispensedAt = sale.BillDate;
                foreach (StagedLine staged in stagedLines.Where(s => s.Schedule == DrugSchedule.X))
                {
                    foreach (SaleItem item in staged.Items)
                    {
                        await _db.ScheduleXDispenses.AddAsync(new ScheduleXDispense
                        {
                            SaleId = sale.SaleId,
                            SaleItemId = item.SaleItemId,
                            ProductId = item.ProductId,
                            BatchId = item.BatchId,
                            Qty = item.Qty,
                            PatientName = cap.PatientName!.Trim(),
                            PatientAddress = cap.PatientAddress!.Trim(),
                            PatientPhone = Nullify(cap.PatientPhone),
                            PrescriberName = cap.PrescriberName!.Trim(),
                            PrescriberAddress = cap.PrescriberAddress!.Trim(),
                            PrescriberRegNo = cap.PrescriberRegNo!.Trim(),
                            PrescriptionNumber = cap.PrescriptionNumber!.Trim(),
                            PrescriptionDate = cap.PrescriptionDate,
                            PrescriptionRetained = cap.PrescriptionRetained,
                            DispensedAt = dispensedAt,
                            CreatedBy = actingUserId,
                        }, cancellationToken);
                    }
                }

                await _db.SaveChangesAsync(cancellationToken);
            }

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

    /// <inheritdoc />
    public async Task<MasterResult<int>> FindSaleIdByBillNoAsync(string billNo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(billNo))
        {
            return MasterResult<int>.Fail("Enter a bill number.");
        }

        string trimmed = billNo.Trim();
        Sale? sale = await _db.Sales
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.BillNo == trimmed, cancellationToken);

        return sale is null
            ? MasterResult<int>.Fail($"No bill found with number '{trimmed}'.")
            : MasterResult<int>.Ok(sale.SaleId);
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
            // Self-heal a mis-set counter: if someone edited it below an already-used sequence,
            // jump past the existing max so we never attempt a duplicate BillNo (the UNIQUE index
            // would otherwise fail the sale). Cheap and only matters when the counter is suspect.
            long maxExisting = await MaxExistingSequenceAsync(cancellationToken);
            next = Math.Max(stored, maxExisting + 1);
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
    /// A line staged after FEFO dispense + line discount but BEFORE GST, so the whole-bill discount
    /// can be apportioned onto it first (GST then lands on the net taxable). <see cref="BillDiscountShare"/>
    /// is filled in by <see cref="ApportionBillDiscount"/>.
    /// </summary>
    private sealed class StagedLine
    {
        public StagedLine(List<SaleItem> items, decimal gstRate, decimal taxableGross, decimal lineDiscount, decimal taxableNet, DrugSchedule schedule)
        {
            Items = items;
            GstRate = gstRate;
            TaxableGross = taxableGross;
            LineDiscount = lineDiscount;
            TaxableNet = taxableNet;
            Schedule = schedule;
        }

        public List<SaleItem> Items { get; }
        public decimal GstRate { get; }
        public decimal TaxableGross { get; }
        public decimal LineDiscount { get; }

        /// <summary>The line's drug schedule — drives the Schedule-X register entry.</summary>
        public DrugSchedule Schedule { get; }

        /// <summary>Line taxable after its own line discount, before the bill discount.</summary>
        public decimal TaxableNet { get; }

        /// <summary>Portion of the whole-bill discount apportioned to this line.</summary>
        public decimal BillDiscountShare { get; set; }
    }

    /// <summary>
    /// Distributes <paramref name="billDiscount"/> across the staged lines proportionally to their
    /// net taxable, to the paise, using the largest-remainder method so the parts sum EXACTLY to
    /// <paramref name="billDiscount"/> (any residual paise land on the largest-fractional-remainder
    /// line, capped so no line's share exceeds its own net taxable).
    /// </summary>
    private static void ApportionBillDiscount(List<StagedLine> lines, decimal billDiscount, decimal totalNetTaxable)
    {
        if (billDiscount <= 0m || totalNetTaxable <= 0m || lines.Count == 0)
        {
            return;
        }

        // Ideal (unrounded) share per line, floored to paise; track the fractional remainder to
        // hand out the leftover paise deterministically (largest remainder first).
        var frac = new (int Index, decimal Remainder)[lines.Count];
        decimal allocated = 0m;
        for (int i = 0; i < lines.Count; i++)
        {
            decimal ideal = billDiscount * lines[i].TaxableNet / totalNetTaxable;
            decimal floored = Math.Floor(ideal * 100m) / 100m;   // truncate to paise
            floored = Math.Min(floored, lines[i].TaxableNet);    // never exceed the line's net
            lines[i].BillDiscountShare = floored;
            allocated += floored;
            frac[i] = (i, ideal - floored);
        }

        // Hand out the residual paise (billDiscount − Σ floored) one paise at a time, largest
        // remainder first, skipping lines already at their net-taxable cap.
        decimal residual = billDiscount - allocated;
        const decimal paise = 0.01m;
        foreach ((int index, decimal _) in frac.OrderByDescending(f => f.Remainder).ThenBy(f => f.Index))
        {
            if (residual < paise)
            {
                break;
            }

            StagedLine line = lines[index];
            if (line.BillDiscountShare + paise <= line.TaxableNet)
            {
                line.BillDiscountShare += paise;
                residual -= paise;
            }
        }

        // Any un-allocatable sub-paise residual (all lines capped) falls to the last line that has
        // room, else it simply reduces the effective bill discount — the header Discount is summed
        // from the per-line shares below, so it always stays consistent.
        if (residual >= paise)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].BillDiscountShare + residual <= lines[i].TaxableNet)
                {
                    lines[i].BillDiscountShare += residual;
                    break;
                }
            }
        }
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
