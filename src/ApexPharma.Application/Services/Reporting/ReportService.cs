using ApexPharma.Application.Time;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services.Reporting;

/// <summary>
/// Concrete read-only <see cref="IReportService"/> (plan.md §11, §14). Every method is a
/// query — nothing is mutated. Low-stock and near-expiry/expired reuse
/// <see cref="IInventoryService"/> (its stock logic already lives in one place, plan.md §8);
/// the sales/profit, Schedule register, and GST/HSN reports read <c>Sale</c>/<c>SaleItem</c>
/// directly. All reads are <c>AsNoTracking</c>. Where a SUM can't be translated by the SQLite
/// EF provider, we materialise the (modest) rows and aggregate in memory — as done elsewhere in
/// this codebase.
/// </summary>
public sealed class ReportService : IReportService
{
    private readonly ApexPharmaDbContext _db;
    private readonly IInventoryService _inventory;
    private readonly ITimeZoneProvider _tz;

    public ReportService(ApexPharmaDbContext db, IInventoryService inventory, ITimeZoneProvider tz)
    {
        _db = db;
        _inventory = inventory;
        _tz = tz;
    }

    /// <inheritdoc />
    public async Task<SalesReport> GetSalesReportAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        (DateTime from, DateTime toExclusive) = NormalizeRange(fromDate, toDate);

        // Pull the bills in range with their lines (and each line's batch for purchase cost)
        // and the customer for the name. Materialise once, then compute per-bill profit in
        // memory: profit = Σ(line net taxable ex-GST) − Σ(Batch.PurchasePrice × qty).
        List<Sale> sales = await _db.Sales
            .AsNoTracking()
            .Include(s => s.Customer)
            .Include(s => s.Items).ThenInclude(i => i.Batch)
            .Where(s => s.BillDate >= from && s.BillDate < toExclusive)
            .OrderBy(s => s.BillDate)
            .ThenBy(s => s.SaleId)
            .ToListAsync(cancellationToken);

        var rows = new List<SalesReportRow>(sales.Count);
        foreach (Sale sale in sales)
        {
            decimal profit = 0m;
            foreach (SaleItem item in sale.Items)
            {
                // Line net taxable, ex-GST, after discounts = Rate×Qty − Discount. This is the
                // authoritative ex-GST base (Σ over a bill's lines == Sale.Subtotal). Purchase
                // cost = the dispensing batch's PurchasePrice × the qty dispensed on this line.
                decimal lineNetExGst = (item.Rate * item.Qty) - item.Discount;
                decimal purchaseCost = (item.Batch?.PurchasePrice ?? 0m) * item.Qty;
                profit += lineNetExGst - purchaseCost;
            }

            rows.Add(new SalesReportRow
            {
                SaleId = sale.SaleId,
                BillNo = sale.BillNo,
                BillDate = sale.BillDate,
                CustomerName = string.IsNullOrWhiteSpace(sale.Customer?.Name) ? "Walk-in" : sale.Customer!.Name,
                PaymentMode = sale.PaymentMode,
                Subtotal = sale.Subtotal,
                Discount = sale.Discount,
                Cgst = sale.Cgst,
                Sgst = sale.Sgst,
                Total = sale.Total,
                Profit = profit,
            });
        }

        var summary = new SalesReportSummary
        {
            BillCount = rows.Count,
            Gross = rows.Sum(r => r.Total),
            Net = rows.Sum(r => r.Subtotal),
            TotalGst = rows.Sum(r => r.Cgst + r.Sgst),
            TotalDiscount = rows.Sum(r => r.Discount),
            TotalProfit = rows.Sum(r => r.Profit),
        };

        return new SalesReport { Rows = rows, Summary = summary };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LowStockRow>> GetLowStockReportAsync(CancellationToken cancellationToken = default)
    {
        // Reuse the inventory service's low-stock query (plan.md §8 — stock logic in one place).
        IReadOnlyList<Product> products = await _inventory.GetLowStockAsync(cancellationToken);
        if (products.Count == 0)
        {
            return Array.Empty<LowStockRow>();
        }

        // Total on-hand per low-stock product for the report column. Materialise the (modest)
        // batch quantities and sum in memory — the SQLite EF provider is brittle translating a
        // grouped decimal SUM here (same reason as InventoryService).
        var productIds = products.Select(p => p.ProductId).ToHashSet();
        List<(int ProductId, decimal Qty)> batchQuantities = (await _db.Batches
            .AsNoTracking()
            .Where(b => productIds.Contains(b.ProductId))
            .Select(b => new { b.ProductId, b.QtyOnHand })
            .ToListAsync(cancellationToken))
            .Select(x => (x.ProductId, x.QtyOnHand))
            .ToList();

        Dictionary<int, decimal> totals = batchQuantities
            .GroupBy(x => x.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));

        return products.Select(p => new LowStockRow
        {
            ProductId = p.ProductId,
            ProductName = p.Name,
            GenericName = p.GenericName,
            TotalOnHand = totals.TryGetValue(p.ProductId, out decimal t) ? t : 0m,
            ReorderLevel = p.ReorderLevel,
            RackLocation = p.RackLocation,
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiryRow>> GetExpiryReportAsync(int withinDays = IInventoryService.DefaultNearExpiryDays, CancellationToken cancellationToken = default)
    {
        // Reuse the inventory service for both sets, then flatten. Expired first (most urgent),
        // then near-expiry, each already ordered by expiry inside the service.
        IReadOnlyList<Batch> expired = await _inventory.GetExpiredAsync(cancellationToken);
        IReadOnlyList<Batch> nearExpiry = await _inventory.GetNearExpiryAsync(withinDays, cancellationToken);

        var rows = new List<ExpiryRow>(expired.Count + nearExpiry.Count);
        rows.AddRange(expired.Select(b => ToExpiryRow(b, isExpired: true)));
        rows.AddRange(nearExpiry.Select(b => ToExpiryRow(b, isExpired: false)));
        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScheduleRegisterRow>> GetScheduleRegisterAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        (DateTime from, DateTime toExclusive) = NormalizeRange(fromDate, toDate);

        // One row per dispensed SaleItem whose product is a scheduled drug (H, H1, or X).
        // Non-scheduled lines are excluded by the schedule filter. Ordered by date then bill so
        // the register reads chronologically (a legal register — plan.md §14).
        List<SaleItem> items = await _db.SaleItems
            .AsNoTracking()
            .Include(i => i.Product)
            .Include(i => i.Batch)
            .Include(i => i.Sale).ThenInclude(s => s!.Customer)
            .Where(i => i.Sale!.BillDate >= from && i.Sale.BillDate < toExclusive
                        && i.Product!.Schedule != DrugSchedule.None)
            .OrderBy(i => i.Sale!.BillDate)
            .ThenBy(i => i.Sale!.SaleId)
            .ThenBy(i => i.SaleItemId)
            .ToListAsync(cancellationToken);

        return items.Select(i => new ScheduleRegisterRow
        {
            BillDate = i.Sale!.BillDate,
            BillNo = i.Sale.BillNo,
            ProductName = i.Product?.Name ?? string.Empty,
            Schedule = i.Product?.Schedule ?? DrugSchedule.None,
            BatchNo = i.Batch?.BatchNo ?? string.Empty,
            ExpiryDate = i.Batch?.ExpiryDate ?? default,
            Qty = i.Qty,
            PatientName = i.Sale.Customer?.Name ?? string.Empty,
            PatientPhone = i.Sale.Customer?.Phone,
            DoctorName = i.Sale.DoctorName,
            PrescriptionRef = i.Sale.PrescriptionRef,
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<ScheduleXRegisterReport> GetScheduleXRegisterAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        (DateTime windowFrom, DateTime toExclusive) = NormalizeRange(from, to);

        // The Schedule-X products the running-balance is filtered to (plan.md §14 — Phase 2f).
        List<int> xProductIds = await _db.Products
            .AsNoTracking()
            .Where(p => p.Schedule == DrugSchedule.X)
            .Select(p => p.ProductId)
            .ToListAsync(ct);

        var balances = new List<ScheduleXBalanceRow>();
        if (xProductIds.Count > 0)
        {
            var xIds = xProductIds.ToHashSet();

            // Materialise every stock movement for the Schedule-X products (both directions of
            // purchase and sale) with its movement date, then aggregate in memory — the SQLite EF
            // provider is brittle on grouped-decimal SUMs, so we follow the codebase convention of
            // pulling the (modest) rows and folding them here. All figures are DERIVED; there is no
            // stock-movement table.
            //  · Received (+) = PurchaseItem, dated by its Purchase.InvoiceDate (as the ledger uses).
            //  · Received (−) = PurchaseReturn, dated by PurchaseReturn.Date.
            //  · Issued  (−)  = SaleItem, dated by its Sale.BillDate.
            //  · Issued  (+)  = SaleReturn (restock), dated by SaleReturn.Date.
            List<(int ProductId, DateTime Date, decimal Qty)> purchaseIn = (await _db.PurchaseItems
                .AsNoTracking()
                .Where(pi => xIds.Contains(pi.ProductId))
                .Select(pi => new { pi.ProductId, Date = pi.Purchase!.InvoiceDate, pi.Qty })
                .ToListAsync(ct))
                .Select(x => (x.ProductId, x.Date, x.Qty)).ToList();

            List<(int ProductId, DateTime Date, decimal Qty)> purchaseOut = (await _db.PurchaseReturns
                .AsNoTracking()
                .Where(pr => xIds.Contains(pr.Batch!.ProductId))
                .Select(pr => new { ProductId = pr.Batch!.ProductId, Date = pr.Date, pr.Qty })
                .ToListAsync(ct))
                .Select(x => (x.ProductId, x.Date, x.Qty)).ToList();

            List<(int ProductId, DateTime Date, decimal Qty)> saleOut = (await _db.SaleItems
                .AsNoTracking()
                .Where(si => xIds.Contains(si.ProductId))
                .Select(si => new { si.ProductId, Date = si.Sale!.BillDate, si.Qty })
                .ToListAsync(ct))
                .Select(x => (x.ProductId, x.Date, x.Qty)).ToList();

            List<(int ProductId, DateTime Date, decimal Qty)> saleIn = (await _db.SaleReturns
                .AsNoTracking()
                .Where(sr => xIds.Contains(sr.Batch!.ProductId))
                .Select(sr => new { ProductId = sr.Batch!.ProductId, Date = sr.Date, sr.Qty })
                .ToListAsync(ct))
                .Select(x => (x.ProductId, x.Date, x.Qty)).ToList();

            Dictionary<int, string> productNames = await _db.Products
                .AsNoTracking()
                .Where(p => xIds.Contains(p.ProductId))
                .ToDictionaryAsync(p => p.ProductId, p => p.Name, ct);

            foreach (int productId in xProductIds)
            {
                // Opening = net movement STRICTLY BEFORE the window; in-range figures split into
                // Received / Issued; Closing = Opening + Received − Issued.
                decimal opening =
                    NetBefore(purchaseIn, productId, windowFrom)
                    - NetBefore(purchaseOut, productId, windowFrom)
                    - NetBefore(saleOut, productId, windowFrom)
                    + NetBefore(saleIn, productId, windowFrom);

                decimal received =
                    InRange(purchaseIn, productId, windowFrom, toExclusive)
                    - InRange(purchaseOut, productId, windowFrom, toExclusive);

                decimal issued =
                    InRange(saleOut, productId, windowFrom, toExclusive)
                    - InRange(saleIn, productId, windowFrom, toExclusive);

                balances.Add(new ScheduleXBalanceRow
                {
                    ProductId = productId,
                    ProductName = productNames.TryGetValue(productId, out string? n) ? n : string.Empty,
                    Opening = opening,
                    Received = received,
                    Issued = issued,
                    Closing = opening + received - issued,
                });
            }

            balances = balances
                .OrderBy(b => b.ProductName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.ProductId)
                .ToList();
        }

        // Dispense-detail rows from the strict register in range, chronological.
        List<ScheduleXDispense> dispenses = await _db.ScheduleXDispenses
            .AsNoTracking()
            .Include(d => d.Product)
            .Include(d => d.Batch)
            .Where(d => d.DispensedAt >= windowFrom && d.DispensedAt < toExclusive)
            .OrderBy(d => d.DispensedAt)
            .ThenBy(d => d.ScheduleXDispenseId)
            .ToListAsync(ct);

        var dispenseRows = dispenses.Select(d => new ScheduleXDispenseRow
        {
            DispensedAt = d.DispensedAt,
            ProductName = d.Product?.Name ?? string.Empty,
            BatchNo = d.Batch?.BatchNo ?? string.Empty,
            Qty = d.Qty,
            PatientName = d.PatientName,
            PatientAddress = d.PatientAddress,
            PatientPhone = d.PatientPhone,
            PrescriberName = d.PrescriberName,
            PrescriberRegNo = d.PrescriberRegNo,
            PrescriptionNumber = d.PrescriptionNumber,
            PrescriptionDate = d.PrescriptionDate,
            PrescriptionRetained = d.PrescriptionRetained,
        }).ToList();

        // Display the operator-local dates the caller picked (the window bounds above are UTC
        // instants, which would render a shifted date in the register header).
        DateTime displayFrom = from.Date <= to.Date ? from.Date : to.Date;
        DateTime displayTo = from.Date <= to.Date ? to.Date : from.Date;

        return new ScheduleXRegisterReport
        {
            FromDate = displayFrom,
            ToDate = displayTo,
            Balances = balances,
            Dispenses = dispenseRows,
        };
    }

    /// <summary>Σ qty of a product's movements strictly before <paramref name="windowFrom"/>.</summary>
    private static decimal NetBefore(IEnumerable<(int ProductId, DateTime Date, decimal Qty)> rows, int productId, DateTime windowFrom)
        => rows.Where(r => r.ProductId == productId && r.Date < windowFrom).Sum(r => r.Qty);

    /// <summary>Σ qty of a product's movements within [from, toExclusive).</summary>
    private static decimal InRange(IEnumerable<(int ProductId, DateTime Date, decimal Qty)> rows, int productId, DateTime from, DateTime toExclusive)
        => rows.Where(r => r.ProductId == productId && r.Date >= from && r.Date < toExclusive).Sum(r => r.Qty);

    /// <inheritdoc />
    public async Task<HsnSummaryReport> GetHsnSummaryAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        (DateTime from, DateTime toExclusive) = NormalizeRange(fromDate, toDate);

        // Pull every sale line in range with its product's HSN, then group by (HSN, GstRate) in
        // memory. Taxable per line = Rate×Qty − Discount (net, ex-GST); CGST/SGST are stored per
        // line as computed at sale time. Grouping in memory keeps the SQLite provider off a
        // grouped-decimal-SUM it translates poorly (consistent with the rest of the codebase).
        var lines = await _db.SaleItems
            .AsNoTracking()
            .Where(i => i.Sale!.BillDate >= from && i.Sale.BillDate < toExclusive)
            .Select(i => new
            {
                Hsn = i.Product!.HsnCode,
                i.GstRate,
                i.Rate,
                i.Qty,
                i.Discount,
                i.Cgst,
                i.Sgst,
            })
            .ToListAsync(cancellationToken);

        var rows = lines
            .GroupBy(l => new { Hsn = string.IsNullOrWhiteSpace(l.Hsn) ? "(none)" : l.Hsn!.Trim(), l.GstRate })
            .Select(g =>
            {
                (decimal taxable, decimal cgst, decimal sgst) = SumLine(g.Select(x => new LineFig(x.Rate, x.Qty, x.Discount, x.Cgst, x.Sgst)));
                return new HsnSummaryRow
                {
                    HsnCode = g.Key.Hsn,
                    GstRate = g.Key.GstRate,
                    Taxable = taxable,
                    Cgst = cgst,
                    Sgst = sgst,
                    Total = taxable + cgst + sgst,
                };
            })
            .OrderBy(r => r.HsnCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.GstRate)
            .ToList();

        var totals = new HsnSummaryTotals
        {
            Taxable = rows.Sum(r => r.Taxable),
            Cgst = rows.Sum(r => r.Cgst),
            Sgst = rows.Sum(r => r.Sgst),
            Total = rows.Sum(r => r.Total),
        };

        return new HsnSummaryReport { Rows = rows, Totals = totals };
    }

    /// <inheritdoc />
    public async Task<Gstr1Report> GetGstr1Async(int year, int month, string placeOfSupply, CancellationToken cancellationToken = default)
    {
        // Derive the month window through the SAME NormalizeRange every other report uses, so the
        // day-boundary behaviour is identical: the local [first-of-month, last-of-month] calendar
        // range is converted to a half-open UTC window via the pharmacy timezone. For IST, July maps
        // to [Jun-30 18:30Z, Jul-31 18:30Z).
        var monthStart = new DateTime(year, month, 1);
        DateTime monthEndInclusive = monthStart.AddMonths(1).AddDays(-1);
        (DateTime from, DateTime toExclusive) = NormalizeRange(monthStart, monthEndInclusive);

        string pos = placeOfSupply ?? string.Empty;

        // --- Outward supplies (B2CS + HSN): every sale line in the month, materialised once.
        // Taxable per line = Rate×Qty − Discount (net, ex-GST) — identical to GetHsnSummaryAsync;
        // CGST/SGST are read from the stored per-line figures (never re-derived, so the return
        // reconciles exactly to what was billed). Cash AND credit both count (payment-agnostic).
        var lines = await _db.SaleItems
            .AsNoTracking()
            .Where(i => i.Sale!.BillDate >= from && i.Sale.BillDate < toExclusive)
            .Select(i => new
            {
                Hsn = i.Product!.HsnCode,
                Unit = i.Product.Unit,
                i.GstRate,
                i.Rate,
                i.Qty,
                i.Discount,
                i.Cgst,
                i.Sgst,
            })
            .ToListAsync(cancellationToken);

        var b2cs = lines
            .GroupBy(l => l.GstRate)
            .Select(g =>
            {
                (decimal taxable, decimal cgst, decimal sgst) = SumLine(g.Select(x => new LineFig(x.Rate, x.Qty, x.Discount, x.Cgst, x.Sgst)));
                return new Gstr1B2csRow
                {
                    GstRate = g.Key,
                    PlaceOfSupply = pos,
                    Taxable = taxable,
                    Cgst = cgst,
                    Sgst = sgst,
                    Total = taxable + cgst + sgst,
                };
            })
            .OrderBy(r => r.GstRate)
            .ToList();

        var hsn = lines
            .GroupBy(l => new { Hsn = string.IsNullOrWhiteSpace(l.Hsn) ? "(none)" : l.Hsn!.Trim(), l.GstRate })
            .Select(g =>
            {
                (decimal taxable, decimal cgst, decimal sgst) = SumLine(g.Select(x => new LineFig(x.Rate, x.Qty, x.Discount, x.Cgst, x.Sgst)));
                // UQC is one per HSN+rate group; take the first non-blank product unit, else "OTH".
                string uqc = g.Select(x => x.Unit)
                    .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))?.Trim() ?? "OTH";
                return new Gstr1HsnRow
                {
                    HsnCode = g.Key.Hsn,
                    Uqc = uqc,
                    TotalQty = g.Sum(x => x.Qty),
                    GstRate = g.Key.GstRate,
                    Taxable = taxable,
                    Cgst = cgst,
                    Sgst = sgst,
                    Total = taxable + cgst + sgst,
                };
            })
            .OrderBy(r => r.HsnCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.GstRate)
            .ToList();

        // --- Credit notes (returns) — a SEPARATE section; does NOT net into the outward totals.
        // Group the month's SaleReturn rows by rate: the sold line's snapshot GstRate when present,
        // else the batch's product default. Returned taxable = Amount − Cgst − Sgst (mirrors how the
        // sale line's gross was built). CGST/SGST summed from the stored reversal figures.
        var returns = await _db.SaleReturns
            .AsNoTracking()
            .Where(r => r.Date >= from && r.Date < toExclusive)
            .Select(r => new
            {
                Rate = r.SaleItem != null ? r.SaleItem.GstRate : r.Batch!.Product!.GstRate,
                r.Amount,
                r.Cgst,
                r.Sgst,
            })
            .ToListAsync(cancellationToken);

        var creditNotes = returns
            .GroupBy(r => r.Rate)
            .Select(g =>
            {
                decimal cgst = g.Sum(x => x.Cgst);
                decimal sgst = g.Sum(x => x.Sgst);
                decimal taxable = g.Sum(x => x.Amount - x.Cgst - x.Sgst);
                return new Gstr1CreditNoteRow
                {
                    GstRate = g.Key,
                    Taxable = taxable,
                    Cgst = cgst,
                    Sgst = sgst,
                    Total = taxable + cgst + sgst,
                };
            })
            .OrderBy(r => r.GstRate)
            .ToList();

        // --- Documents issued: first & last bill number (string-ordered) + count for the month.
        List<string> billNos = await _db.Sales
            .AsNoTracking()
            .Where(s => s.BillDate >= from && s.BillDate < toExclusive)
            .Select(s => s.BillNo)
            .ToListAsync(cancellationToken);

        billNos.Sort(StringComparer.Ordinal);
        var docs = new Gstr1DocsIssued
        {
            FromBillNo = billNos.Count > 0 ? billNos[0] : string.Empty,
            ToBillNo = billNos.Count > 0 ? billNos[^1] : string.Empty,
            Count = billNos.Count,
            Cancelled = 0,
        };

        // Gross outward totals (Σ over B2CS == Σ over HSN) + the period's bill count.
        var totals = new Gstr1Totals
        {
            Taxable = b2cs.Sum(r => r.Taxable),
            Cgst = b2cs.Sum(r => r.Cgst),
            Sgst = b2cs.Sum(r => r.Sgst),
            Total = b2cs.Sum(r => r.Total),
            BillCount = billNos.Count,
        };

        return new Gstr1Report
        {
            Year = year,
            Month = month,
            PlaceOfSupply = pos,
            B2cs = b2cs,
            Hsn = hsn,
            CreditNotes = creditNotes,
            Docs = docs,
            Totals = totals,
        };
    }

    private static ExpiryRow ToExpiryRow(Batch b, bool isExpired) => new()
    {
        ProductId = b.ProductId,
        ProductName = b.Product?.Name ?? string.Empty,
        BatchId = b.BatchId,
        BatchNo = b.BatchNo,
        ExpiryDate = b.ExpiryDate,
        QtyOnHand = b.QtyOnHand,
        Mrp = b.Mrp,
        IsExpired = isExpired,
    };

    /// <summary>
    /// Normalises an inclusive [from, to] operator-LOCAL day range into a half-open
    /// <c>[FromUtc, ToUtcExclusive)</c> window on the UTC-stamped <c>BillDate</c>, via the shared
    /// <see cref="DayWindow"/> helper and the pharmacy timezone. A sale stamped at any UTC instant
    /// that falls on the local <paramref name="toDate"/> is included; a reversed range is swapped.
    /// Converting the local calendar day to a UTC window (rather than flooring the local date and
    /// treating it as a UTC bound) prevents near-midnight rows mis-bucketing (plan.md §11).
    /// </summary>
    private (DateTime From, DateTime ToExclusive) NormalizeRange(DateTime fromDate, DateTime toDate)
        => DayWindow.ToUtcWindow(fromDate, toDate, _tz.GetPharmacyTimeZone());

    /// <summary>
    /// The shared per-line shape the GST aggregations sum over: the line's sale rate, quantity,
    /// discount, and the STORED per-line CGST/SGST. Kept minimal so <see cref="SumLine"/> is the
    /// single place that defines the taxable/tax reconciliation across HSN-summary, B2CS, and HSN.
    /// </summary>
    private readonly record struct LineFig(decimal Rate, decimal Qty, decimal Discount, decimal Cgst, decimal Sgst);

    /// <summary>
    /// Folds a group of lines into (net taxable, CGST, SGST): taxable = Σ(Rate×Qty − Discount)
    /// (ex-GST, after discounts); CGST/SGST = Σ of the STORED per-line figures — never re-derived
    /// from the aggregate, so the return reconciles exactly to what was billed. This is the one
    /// definition used by HSN-summary, GSTR-1 B2CS, and GSTR-1 HSN so they can never drift apart.
    /// </summary>
    private static (decimal Taxable, decimal Cgst, decimal Sgst) SumLine(IEnumerable<LineFig> lines)
    {
        decimal taxable = 0m;
        decimal cgst = 0m;
        decimal sgst = 0m;
        foreach (LineFig l in lines)
        {
            taxable += (l.Rate * l.Qty) - l.Discount;
            cgst += l.Cgst;
            sgst += l.Sgst;
        }

        return (taxable, cgst, sgst);
    }
}
