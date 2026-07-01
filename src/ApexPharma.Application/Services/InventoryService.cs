using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services;

/// <summary>
/// Concrete inventory service (plan.md §6.1, §12). Provides FEFO batch selection and
/// audited, non-negative stock adjustments for later phases, plus the read-only stock
/// views the owner needs right after a purchase stocks in — current stock (per batch),
/// near-expiry, expired, and low-stock. All read queries are <c>AsNoTracking</c> and run
/// server-side; the read-only ones mutate nothing (plan.md §6.1). Stock logic lives here,
/// never in the UI (plan.md §8 layering).
/// </summary>
public class InventoryService : IInventoryService
{
    private readonly ApexPharmaDbContext _db;

    public InventoryService(ApexPharmaDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<Batch?> SelectBatchFefoAsync(int productId, decimal requiredQty, CancellationToken cancellationToken = default)
    {
        DateTime today = DateTime.UtcNow.Date;

        // First-expiry-first-out: earliest non-expired batch with enough stock to cover the
        // required quantity. Expired batches are never dispensed (plan.md §6.1).
        return await _db.Batches
            .Where(b => b.ProductId == productId
                        && b.ExpiryDate > today
                        && b.QtyOnHand >= requiredQty)
            .OrderBy(b => b.ExpiryDate)
            .ThenBy(b => b.BatchId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AdjustStockAsync(int batchId, AdjustmentType type, decimal qtyDelta, string reason, int userId, CancellationToken cancellationToken = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            Batch batch = await _db.Batches.FirstOrDefaultAsync(b => b.BatchId == batchId, cancellationToken)
                ?? throw new InvalidOperationException($"Batch {batchId} not found.");

            decimal newQty = batch.QtyOnHand + qtyDelta;
            if (newQty < 0)
            {
                // Stock can never go negative (plan.md §6.2 data integrity).
                throw new InvalidOperationException(
                    $"Adjustment of {qtyDelta:0.##} would drive batch {batchId} below zero (on hand {batch.QtyOnHand:0.##}).");
            }

            batch.QtyOnHand = newQty;

            await _db.StockAdjustments.AddAsync(new StockAdjustment
            {
                BatchId = batchId,
                ProductId = batch.ProductId,
                Type = type,
                QtyDelta = qtyDelta,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                Date = DateTime.UtcNow,
                CreatedBy = userId,
            }, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StockRow>> GetStockAsync(int nearExpiryDays = IInventoryService.DefaultNearExpiryDays, CancellationToken cancellationToken = default)
    {
        if (nearExpiryDays < 0)
        {
            nearExpiryDays = IInventoryService.DefaultNearExpiryDays;
        }

        DateTime today = DateTime.UtcNow.Date;
        DateTime nearThreshold = today.AddDays(nearExpiryDays);

        // Single materialised read of all batches (with their product), then derive the
        // low-stock flag in-memory from the SAME rows — no second batch query. Note the
        // per-product total for the low-stock flag must sum ALL batches (including qty 0
        // and expired), so we total before filtering the grid to in-stock rows.
        List<Batch> allBatches = await _db.Batches
            .AsNoTracking()
            .Include(b => b.Product)
            .OrderBy(b => b.Product!.Name)
            .ThenBy(b => b.ExpiryDate)
            .ToListAsync(cancellationToken);

        Dictionary<int, decimal> totalsByProduct = allBatches
            .GroupBy(b => b.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.QtyOnHand));

        // A product is low-stock when its total on-hand is at or below its reorder level.
        // Match GetLowStockAsync's contract: only ACTIVE products count as low-stock. We read
        // the reorder level / active flag from any batch's loaded Product; products with no
        // batch never appear in the grid, so their absence here is correct.
        var lowStock = allBatches
            .Where(b => b.Product is not null
                        && b.Product.IsActive
                        && (totalsByProduct.TryGetValue(b.ProductId, out decimal total) ? total : 0m) <= b.Product.ReorderLevel)
            .Select(b => b.ProductId)
            .ToHashSet();

        return allBatches
            .Where(b => b.QtyOnHand > 0)
            .Select(b =>
        {
            bool expired = b.ExpiryDate.Date <= today;
            bool nearExpiry = !expired && b.ExpiryDate.Date <= nearThreshold;
            return new StockRow
            {
                ProductId = b.ProductId,
                ProductName = b.Product?.Name ?? string.Empty,
                BatchId = b.BatchId,
                BatchNo = b.BatchNo,
                ExpiryDate = b.ExpiryDate,
                QtyOnHand = b.QtyOnHand,
                Mrp = b.Mrp,
                SalePrice = b.SalePrice,
                IsExpired = expired,
                IsNearExpiry = nearExpiry,
                IsLowStock = lowStock.Contains(b.ProductId),
            };
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<decimal> GetTotalStockAsync(int productId, CancellationToken cancellationToken = default)
    {
        // Sum client-side over the (small) batch list for this product. The SQLite EF
        // provider can't translate a nullable-decimal SUM used to guard the empty set, so we
        // materialise the quantities and add them in memory — cheap for one product's lots.
        List<decimal> quantities = await _db.Batches
            .Where(b => b.ProductId == productId)
            .Select(b => b.QtyOnHand)
            .ToListAsync(cancellationToken);

        return quantities.Sum();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Batch>> GetNearExpiryAsync(int withinDays = IInventoryService.DefaultNearExpiryDays, CancellationToken cancellationToken = default)
    {
        if (withinDays < 0)
        {
            withinDays = IInventoryService.DefaultNearExpiryDays;
        }

        DateTime today = DateTime.UtcNow.Date;
        DateTime threshold = today.AddDays(withinDays);

        // Not-yet-expired lots that fall due within the window and still carry stock.
        return await _db.Batches.AsNoTracking()
            .Include(b => b.Product)
            .Where(b => b.QtyOnHand > 0 && b.ExpiryDate > today && b.ExpiryDate <= threshold)
            .OrderBy(b => b.ExpiryDate)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Batch>> GetExpiredAsync(CancellationToken cancellationToken = default)
    {
        DateTime today = DateTime.UtcNow.Date;

        // Expired lots still carrying stock — write-off candidates (plan.md §6.1).
        return await _db.Batches.AsNoTracking()
            .Include(b => b.Product)
            .Where(b => b.QtyOnHand > 0 && b.ExpiryDate <= today)
            .OrderBy(b => b.ExpiryDate)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Product>> GetLowStockAsync(CancellationToken cancellationToken = default)
    {
        // Active products whose total on-hand across all batches is at or below their
        // reorder level (plan.md §6.1). A product with no batches (total 0) counts as low.
        //
        // Two server-side queries then combine in memory: the SQLite EF provider can't
        // translate a correlated nullable-decimal SUM used to guard the empty set, so we pull
        // the active products and a grouped per-product total, and compare here.
        List<Product> active = await _db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

        // Pull (product, qty) and total per product in memory — the SQLite EF provider is
        // brittle translating a grouped decimal SUM here, and batch rows are modest in count.
        List<(int ProductId, decimal Qty)> batchQuantities = (await _db.Batches
            .Select(b => new { b.ProductId, b.QtyOnHand })
            .ToListAsync(cancellationToken))
            .Select(x => (x.ProductId, x.QtyOnHand))
            .ToList();

        Dictionary<int, decimal> totals = batchQuantities
            .GroupBy(x => x.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));

        return active
            .Where(p => (totals.TryGetValue(p.ProductId, out decimal total) ? total : 0m) <= p.ReorderLevel)
            .ToList();
    }
}
