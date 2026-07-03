using System;
using System.Linq;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// InventoryService read-only-query tests (plan.md §6.1). Covers near-expiry (window),
/// expired, and low-stock query sets, the flattened stock grid with its highlight flags,
/// total-stock-per-product, and FEFO batch selection. No mutations are exercised here.
/// </summary>
public class InventoryServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly InventoryService _sut;
    private int _supplierId;
    private int _userId;
    private int _lowStockProductId;
    private int _healthyProductId;
    private int _adjustBatchId;

    public InventoryServiceTests()
    {
        _sut = new InventoryService(_fixture.Context, TestTz.IstProvider());
        Seed();
    }

    public void Dispose() => _fixture.Dispose();

    private void Seed()
    {
        var db = _fixture.Context;

        // A user is required so StockAdjustment.CreatedBy FK is satisfied (audit — plan.md §4).
        var role = new Role { Name = "Owner" };
        db.Roles.Add(role);
        db.SaveChanges();
        var user = new User { Username = "owner", PasswordHash = "x", FullName = "Owner", RoleId = role.RoleId };
        db.Users.Add(user);

        var cat = new Category { Name = "Medication" };
        var man = new Manufacturer { Name = "Cipla" };
        var supplier = new Supplier { Name = "MediDist", IsActive = true };
        db.Categories.Add(cat);
        db.Manufacturers.Add(man);
        db.Suppliers.Add(supplier);
        db.SaveChanges();
        _supplierId = supplier.SupplierId;
        _userId = user.UserId;

        // Low-stock product: reorder level 20, only 5 on hand.
        var low = new Product { Name = "LowStock Drug", CategoryId = cat.CategoryId, ManufacturerId = man.ManufacturerId, GstRate = 12m, IsActive = true, ReorderLevel = 20 };
        // Healthy product: reorder level 5, 100 on hand.
        var healthy = new Product { Name = "Healthy Drug", CategoryId = cat.CategoryId, ManufacturerId = man.ManufacturerId, GstRate = 12m, IsActive = true, ReorderLevel = 5 };
        db.Products.AddRange(low, healthy);
        db.SaveChanges();
        _lowStockProductId = low.ProductId;
        _healthyProductId = healthy.ProductId;

        DateTime today = DateTime.UtcNow.Date;

        var lowBatch = new Batch { ProductId = low.ProductId, BatchNo = "L1", ExpiryDate = today.AddYears(2), Mrp = 10m, PurchasePrice = 6m, SalePrice = 10m, QtyOnHand = 5m, SupplierId = _supplierId, ReceivedDate = today };
        db.Batches.AddRange(
            // low-stock product: 5 on hand, far expiry
            lowBatch,
            // healthy product: near-expiry batch (30 days), plenty on hand
            new Batch { ProductId = healthy.ProductId, BatchNo = "H-NEAR", ExpiryDate = today.AddDays(30), Mrp = 20m, PurchasePrice = 12m, SalePrice = 20m, QtyOnHand = 40m, SupplierId = _supplierId, ReceivedDate = today },
            // healthy product: far-expiry batch
            new Batch { ProductId = healthy.ProductId, BatchNo = "H-FAR", ExpiryDate = today.AddDays(300), Mrp = 20m, PurchasePrice = 12m, SalePrice = 20m, QtyOnHand = 60m, SupplierId = _supplierId, ReceivedDate = today },
            // healthy product: EXPIRED batch still carrying stock
            new Batch { ProductId = healthy.ProductId, BatchNo = "H-EXP", ExpiryDate = today.AddDays(-5), Mrp = 20m, PurchasePrice = 12m, SalePrice = 20m, QtyOnHand = 7m, SupplierId = _supplierId, ReceivedDate = today });
        db.SaveChanges();
        _adjustBatchId = lowBatch.BatchId; // starts at 5 on hand
    }

    [Fact]
    public async Task GetNearExpiry_DefaultWindow_ReturnsOnlyNotYetExpiredWithinWindow()
    {
        var near = await _sut.GetNearExpiryAsync(); // default 90 days

        Assert.Single(near);
        Assert.Equal("H-NEAR", near[0].BatchNo);
    }

    [Fact]
    public async Task GetNearExpiry_NarrowWindow_ExcludesBeyondWindow()
    {
        var near = await _sut.GetNearExpiryAsync(withinDays: 10);

        Assert.Empty(near); // H-NEAR is 30 days out, beyond a 10-day window
    }

    [Fact]
    public async Task GetExpired_ReturnsExpiredBatchesWithStock()
    {
        var expired = await _sut.GetExpiredAsync();

        Assert.Single(expired);
        Assert.Equal("H-EXP", expired[0].BatchNo);
    }

    [Fact]
    public async Task GetLowStock_ReturnsProductsAtOrBelowReorderLevel()
    {
        var low = await _sut.GetLowStockAsync();

        Assert.Single(low);
        Assert.Equal(_lowStockProductId, low[0].ProductId);
    }

    [Fact]
    public async Task GetTotalStock_SumsAcrossBatches()
    {
        decimal total = await _sut.GetTotalStockAsync(_healthyProductId);

        // 40 (near) + 60 (far) + 7 (expired) = 107
        Assert.Equal(107m, total);
    }

    [Fact]
    public async Task GetStock_FlagsExpiredNearExpiryAndLowStock()
    {
        var rows = await _sut.GetStockAsync(); // default 90-day near window

        // 4 batches all have stock > 0.
        Assert.Equal(4, rows.Count);

        StockRow expired = rows.Single(r => r.BatchNo == "H-EXP");
        Assert.True(expired.IsExpired);
        Assert.False(expired.IsNearExpiry);

        StockRow near = rows.Single(r => r.BatchNo == "H-NEAR");
        Assert.True(near.IsNearExpiry);
        Assert.False(near.IsExpired);

        StockRow lowRow = rows.Single(r => r.BatchNo == "L1");
        Assert.True(lowRow.IsLowStock);

        StockRow farHealthy = rows.Single(r => r.BatchNo == "H-FAR");
        Assert.False(farHealthy.IsLowStock);
        Assert.False(farHealthy.IsNearExpiry);
        Assert.False(farHealthy.IsExpired);
    }

    [Fact]
    public async Task SelectBatchFefo_PicksEarliestNonExpiredWithEnoughStock()
    {
        // For the healthy product, earliest NON-expired is H-NEAR (30 days). Expired H-EXP
        // must be skipped even though it's earliest overall.
        Batch? batch = await _sut.SelectBatchFefoAsync(_healthyProductId, requiredQty: 10m);

        Assert.NotNull(batch);
        Assert.Equal("H-NEAR", batch!.BatchNo);
    }

    [Fact]
    public async Task SelectBatchFefo_SkipsBatchWithoutEnoughStock()
    {
        // Require more than H-NEAR holds (40) → must fall through to H-FAR (60).
        Batch? batch = await _sut.SelectBatchFefoAsync(_healthyProductId, requiredQty: 50m);

        Assert.NotNull(batch);
        Assert.Equal("H-FAR", batch!.BatchNo);
    }

    // ---- AdjustStock: the audited, non-negative stock-mutation path (plan.md §6.1, §12) ----

    [Fact]
    public async Task AdjustStock_Decrement_CommitsAndWritesAuditRow()
    {
        // L1 starts at 5; a breakage of -2 → 3 on hand, with a matching audit row.
        await _sut.AdjustStockAsync(_adjustBatchId, AdjustmentType.Breakage, -2m, "dropped", _userId);

        var db = _fixture.NewContext();
        var batch = await db.Batches.SingleAsync(b => b.BatchId == _adjustBatchId);
        Assert.Equal(3m, batch.QtyOnHand);

        var adj = await db.StockAdjustments.SingleAsync(a => a.BatchId == _adjustBatchId);
        Assert.Equal(-2m, adj.QtyDelta);
        Assert.Equal(AdjustmentType.Breakage, adj.Type);
        Assert.Equal("dropped", adj.Reason);
        Assert.Equal(_lowStockProductId, adj.ProductId);
        Assert.Equal(_userId, adj.CreatedBy);
    }

    [Fact]
    public async Task AdjustStock_Increment_CommitsAndWritesAuditRow()
    {
        // L1 starts at 5; a count correction of +4 → 9 on hand, with a matching audit row.
        await _sut.AdjustStockAsync(_adjustBatchId, AdjustmentType.CountCorrection, 4m, "recount", _userId);

        var db = _fixture.NewContext();
        var batch = await db.Batches.SingleAsync(b => b.BatchId == _adjustBatchId);
        Assert.Equal(9m, batch.QtyOnHand);

        var adj = await db.StockAdjustments.SingleAsync(a => a.BatchId == _adjustBatchId);
        Assert.Equal(4m, adj.QtyDelta);
        Assert.Equal(AdjustmentType.CountCorrection, adj.Type);
    }

    [Fact]
    public async Task AdjustStock_WouldGoNegative_Rejected_RollsBackWithNoPartialWrite()
    {
        // L1 has 5 on hand; a -6 adjustment would drive it below zero → must throw and
        // roll back: batch qty unchanged and NO orphan StockAdjustment row.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.AdjustStockAsync(_adjustBatchId, AdjustmentType.Breakage, -6m, "over-write-off", _userId));

        Assert.Contains("below zero", ex.Message);

        var db = _fixture.NewContext();
        var batch = await db.Batches.SingleAsync(b => b.BatchId == _adjustBatchId);
        Assert.Equal(5m, batch.QtyOnHand); // unchanged
        Assert.Equal(0, await db.StockAdjustments.CountAsync()); // no partial write
    }
}
