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

    public ReportService(ApexPharmaDbContext db, IInventoryService inventory)
    {
        _db = db;
        _inventory = inventory;
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
                decimal taxable = g.Sum(x => (x.Rate * x.Qty) - x.Discount);
                decimal cgst = g.Sum(x => x.Cgst);
                decimal sgst = g.Sum(x => x.Sgst);
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
    /// Normalises an inclusive [from, to] day range into a half-open [from-date, to-date+1)
    /// interval on <c>BillDate</c>, so a sale timestamped any time on <paramref name="toDate"/>
    /// is included. Both bounds are date-floored; a reversed range is swapped so callers can't
    /// accidentally get an empty result.
    /// </summary>
    private static (DateTime From, DateTime ToExclusive) NormalizeRange(DateTime fromDate, DateTime toDate)
    {
        DateTime from = fromDate.Date;
        DateTime to = toDate.Date;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        return (from, to.AddDays(1));
    }
}
