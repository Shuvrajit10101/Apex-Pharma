using System;
using System.Linq;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Inventory;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// StockAdjustmentService tests (plan.md §6.1, §12): manual breakage adjustment, physical-count
/// correction, single + bulk expiry write-off with value-lost, RBAC gating, and transaction
/// atomicity. Uses real in-memory SQLite so the actual schema/keys/audit rows are exercised. Every
/// mutation flows through the existing InventoryService.AdjustStockAsync audited/transactional path.
/// </summary>
public class StockAdjustmentServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly StockAdjustmentService _sut;
    private readonly InventoryService _inventory;
    private int _supplierId;
    private int _userId;
    private int _productId;

    // Batches: L1 = 5 on hand, far expiry (manual/count target); EXP1 & EXP2 = expired w/ stock;
    // NEAR = not expired w/ stock (must never be touched by write-off).
    private int _liveBatchId;
    private int _expired1Id;
    private int _expired2Id;
    private int _nearBatchId;

    public StockAdjustmentServiceTests()
    {
        _inventory = new InventoryService(_fixture.Context);
        _sut = new StockAdjustmentService(_fixture.Context, _inventory, new AuthService(_fixture.Context), TestTz.IstProvider());
        Seed();
    }

    public void Dispose() => _fixture.Dispose();

    private void Seed()
    {
        var db = _fixture.Context;

        var role = new Role { Name = "Owner" };
        db.Roles.Add(role);
        db.SaveChanges();
        var user = new User { Username = "owner", PasswordHash = "x", FullName = "Owner User", RoleId = role.RoleId };
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

        var product = new Product { Name = "Paracetamol", CategoryId = cat.CategoryId, ManufacturerId = man.ManufacturerId, GstRate = 12m, IsActive = true, ReorderLevel = 10 };
        db.Products.Add(product);
        db.SaveChanges();
        _productId = product.ProductId;

        DateTime today = DateTime.UtcNow.Date;

        var live = new Batch { ProductId = _productId, BatchNo = "L1", ExpiryDate = today.AddYears(1), Mrp = 10m, PurchasePrice = 6m, SalePrice = 10m, QtyOnHand = 5m, SupplierId = _supplierId, ReceivedDate = today };
        var exp1 = new Batch { ProductId = _productId, BatchNo = "EXP1", ExpiryDate = today.AddDays(-3), Mrp = 20m, PurchasePrice = 12m, SalePrice = 20m, QtyOnHand = 4m, SupplierId = _supplierId, ReceivedDate = today.AddYears(-1) };
        var exp2 = new Batch { ProductId = _productId, BatchNo = "EXP2", ExpiryDate = today, Mrp = 30m, PurchasePrice = 18m, SalePrice = 30m, QtyOnHand = 2m, SupplierId = _supplierId, ReceivedDate = today.AddYears(-1) };
        var near = new Batch { ProductId = _productId, BatchNo = "NEAR", ExpiryDate = today.AddDays(20), Mrp = 15m, PurchasePrice = 9m, SalePrice = 15m, QtyOnHand = 8m, SupplierId = _supplierId, ReceivedDate = today };
        db.Batches.AddRange(live, exp1, exp2, near);
        db.SaveChanges();
        _liveBatchId = live.BatchId;
        _expired1Id = exp1.BatchId;
        _expired2Id = exp2.BatchId;
        _nearBatchId = near.BatchId;
    }

    // ---- Manual (breakage/wastage) adjustment ----

    [Fact]
    public async Task AdjustByDelta_WritesAuditRow_WithCorrectDeltaTypeReasonUser_AndUpdatesQty()
    {
        MasterResult result = await _sut.AdjustByDeltaAsync(_liveBatchId, -2m, "dropped a strip", _userId, UserRole.Owner);

        Assert.True(result.Succeeded);

        var db = _fixture.NewContext();
        Batch batch = await db.Batches.SingleAsync(b => b.BatchId == _liveBatchId);
        Assert.Equal(3m, batch.QtyOnHand);

        StockAdjustment adj = await db.StockAdjustments.SingleAsync(a => a.BatchId == _liveBatchId);
        Assert.Equal(-2m, adj.QtyDelta);
        Assert.Equal(AdjustmentType.Breakage, adj.Type);
        Assert.Equal("dropped a strip", adj.Reason);
        Assert.Equal(_productId, adj.ProductId);
        Assert.Equal(_userId, adj.CreatedBy);
    }

    [Fact]
    public async Task AdjustByDelta_MissingReason_Rejected_NoAuditRow()
    {
        MasterResult result = await _sut.AdjustByDeltaAsync(_liveBatchId, -1m, "  ", _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("reason", result.Error!, StringComparison.OrdinalIgnoreCase);

        var db = _fixture.NewContext();
        Assert.Equal(0, await db.StockAdjustments.CountAsync());
        Assert.Equal(5m, (await db.Batches.SingleAsync(b => b.BatchId == _liveBatchId)).QtyOnHand);
    }

    [Fact]
    public async Task AdjustByDelta_WouldGoNegative_Rejected_RollsBack_NoAuditRow()
    {
        // L1 has 5; -6 would go below zero → rejected via the AdjustStock non-negative guard.
        MasterResult result = await _sut.AdjustByDeltaAsync(_liveBatchId, -6m, "over-write-off", _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("below zero", result.Error!);

        var db = _fixture.NewContext();
        Assert.Equal(5m, (await db.Batches.SingleAsync(b => b.BatchId == _liveBatchId)).QtyOnHand);
        Assert.Equal(0, await db.StockAdjustments.CountAsync()); // atomic: no orphan row
    }

    // ---- Physical-count correction ----

    [Fact]
    public async Task CorrectCount_SetsOnHand_AndRecordsExactDelta()
    {
        // L1 recorded 5, counted 8 → delta +3, on-hand becomes 8, CountCorrection audit row.
        MasterResult result = await _sut.CorrectCountAsync(_liveBatchId, 8m, "shelf recount", _userId, UserRole.Owner);

        Assert.True(result.Succeeded);

        var db = _fixture.NewContext();
        Assert.Equal(8m, (await db.Batches.SingleAsync(b => b.BatchId == _liveBatchId)).QtyOnHand);

        StockAdjustment adj = await db.StockAdjustments.SingleAsync(a => a.BatchId == _liveBatchId);
        Assert.Equal(3m, adj.QtyDelta);
        Assert.Equal(AdjustmentType.CountCorrection, adj.Type);
        Assert.Equal("shelf recount", adj.Reason);
    }

    [Fact]
    public async Task CorrectCount_DownwardDelta_Works()
    {
        // L1 recorded 5, counted 1 → delta -4.
        MasterResult result = await _sut.CorrectCountAsync(_liveBatchId, 1m, "recount", _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        Assert.Equal(1m, (await db.Batches.SingleAsync(b => b.BatchId == _liveBatchId)).QtyOnHand);
        StockAdjustment adj = await db.StockAdjustments.SingleAsync(a => a.BatchId == _liveBatchId);
        Assert.Equal(-4m, adj.QtyDelta);
    }

    [Fact]
    public async Task CorrectCount_NegativeCount_Rejected()
    {
        MasterResult result = await _sut.CorrectCountAsync(_liveBatchId, -1m, "bad", _userId, UserRole.Owner);
        Assert.False(result.Succeeded);
        var db = _fixture.NewContext();
        Assert.Equal(0, await db.StockAdjustments.CountAsync());
    }

    // ---- Single-batch expiry write-off ----

    [Fact]
    public async Task WriteOffExpiredBatch_ZeroesOnlyThatBatch_RecordsExpiry_ComputesValueLost()
    {
        // EXP1: 4 on hand @ cost 12, MRP 20 → valueAtCost 48, valueAtMrp 80.
        MasterResult<ExpiryWriteOffLine> result = await _sut.WriteOffExpiredBatchAsync(_expired1Id, _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        ExpiryWriteOffLine line = result.Value!;
        Assert.Equal(4m, line.QtyWrittenOff);
        Assert.Equal(48m, line.ValueAtCost);
        Assert.Equal(80m, line.ValueAtMrp);

        var db = _fixture.NewContext();
        Assert.Equal(0m, (await db.Batches.SingleAsync(b => b.BatchId == _expired1Id)).QtyOnHand);

        StockAdjustment adj = await db.StockAdjustments.SingleAsync(a => a.BatchId == _expired1Id);
        Assert.Equal(AdjustmentType.Expiry, adj.Type);
        Assert.Equal(-4m, adj.QtyDelta);
        Assert.Contains("Expired", adj.Reason!);

        // Other batches untouched.
        Assert.Equal(5m, (await db.Batches.SingleAsync(b => b.BatchId == _liveBatchId)).QtyOnHand);
        Assert.Equal(8m, (await db.Batches.SingleAsync(b => b.BatchId == _nearBatchId)).QtyOnHand);
    }

    [Fact]
    public async Task WriteOffExpiredBatch_NonExpiredBatch_Refused_Untouched()
    {
        MasterResult<ExpiryWriteOffLine> result = await _sut.WriteOffExpiredBatchAsync(_nearBatchId, _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("not expired", result.Error!, StringComparison.OrdinalIgnoreCase);

        var db = _fixture.NewContext();
        Assert.Equal(8m, (await db.Batches.SingleAsync(b => b.BatchId == _nearBatchId)).QtyOnHand);
        Assert.Equal(0, await db.StockAdjustments.CountAsync());
    }

    // ---- Bulk expiry write-off ----

    [Fact]
    public async Task WriteOffAllExpired_ZeroesEveryExpiredBatch_LeavesNonExpired_TotalsCorrect()
    {
        // Expired candidates: EXP1 (4 @ cost12/mrp20) + EXP2 (2 @ cost18/mrp30).
        // Totals: qty 6; cost 48+36=84; mrp 80+60=140.
        MasterResult<ExpiryWriteOffSummary> result = await _sut.WriteOffAllExpiredAsync(_userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        ExpiryWriteOffSummary s = result.Value!;
        Assert.Equal(2, s.BatchCount);
        Assert.Equal(6m, s.TotalQty);
        Assert.Equal(84m, s.TotalValueAtCost);
        Assert.Equal(140m, s.TotalValueAtMrp);

        var db = _fixture.NewContext();
        Assert.Equal(0m, (await db.Batches.SingleAsync(b => b.BatchId == _expired1Id)).QtyOnHand);
        Assert.Equal(0m, (await db.Batches.SingleAsync(b => b.BatchId == _expired2Id)).QtyOnHand);
        // Non-expired and live untouched.
        Assert.Equal(8m, (await db.Batches.SingleAsync(b => b.BatchId == _nearBatchId)).QtyOnHand);
        Assert.Equal(5m, (await db.Batches.SingleAsync(b => b.BatchId == _liveBatchId)).QtyOnHand);

        // Two Expiry audit rows written.
        Assert.Equal(2, await db.StockAdjustments.CountAsync(a => a.Type == AdjustmentType.Expiry));
    }

    [Fact]
    public async Task WriteOffAllExpired_WithCutoff_IncludesBatchesUpToCutoff()
    {
        // Cutoff = today + 25 days pulls in NEAR (expires today+20) too → 3 batches.
        DateTime cutoff = DateTime.UtcNow.Date.AddDays(25);
        MasterResult<ExpiryWriteOffSummary> result = await _sut.WriteOffAllExpiredAsync(_userId, UserRole.Owner, cutoff);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Value!.BatchCount);
        var db = _fixture.NewContext();
        Assert.Equal(0m, (await db.Batches.SingleAsync(b => b.BatchId == _nearBatchId)).QtyOnHand);
        Assert.Equal(5m, (await db.Batches.SingleAsync(b => b.BatchId == _liveBatchId)).QtyOnHand); // live still safe
    }

    // ---- RBAC ----

    [Fact]
    public async Task Cashier_CannotAdjust_CorrectCount_OrWriteOff()
    {
        MasterResult adj = await _sut.AdjustByDeltaAsync(_liveBatchId, -1m, "x", _userId, UserRole.Cashier);
        MasterResult cnt = await _sut.CorrectCountAsync(_liveBatchId, 2m, "x", _userId, UserRole.Cashier);
        MasterResult<ExpiryWriteOffLine> wo = await _sut.WriteOffExpiredBatchAsync(_expired1Id, _userId, UserRole.Cashier);
        MasterResult<ExpiryWriteOffSummary> bulk = await _sut.WriteOffAllExpiredAsync(_userId, UserRole.Cashier);

        Assert.False(adj.Succeeded);
        Assert.False(cnt.Succeeded);
        Assert.False(wo.Succeeded);
        Assert.False(bulk.Succeeded);
        Assert.Contains("permission", adj.Error!, StringComparison.OrdinalIgnoreCase);

        // Nothing changed.
        var db = _fixture.NewContext();
        Assert.Equal(0, await db.StockAdjustments.CountAsync());
        Assert.Equal(5m, (await db.Batches.SingleAsync(b => b.BatchId == _liveBatchId)).QtyOnHand);
        Assert.Equal(4m, (await db.Batches.SingleAsync(b => b.BatchId == _expired1Id)).QtyOnHand);
    }

    [Fact]
    public async Task Pharmacist_CanAdjust()
    {
        MasterResult result = await _sut.AdjustByDeltaAsync(_liveBatchId, -1m, "spillage", _userId, UserRole.Pharmacist);
        Assert.True(result.Succeeded);
    }

    // ---- History ----

    [Fact]
    public async Task GetHistory_ReturnsRecentAdjustments_NewestFirst_WithProductBatchUser()
    {
        await _sut.AdjustByDeltaAsync(_liveBatchId, -1m, "first", _userId, UserRole.Owner);
        await _sut.WriteOffExpiredBatchAsync(_expired1Id, _userId, UserRole.Owner);

        var history = await _sut.GetHistoryAsync();
        Assert.Equal(2, history.Count);

        // Newest first: the expiry write-off was recorded last.
        AdjustmentHistoryRow first = history[0];
        Assert.Equal(_productId, first.ProductId);
        Assert.Equal("Paracetamol", first.ProductName);
        Assert.Equal("Owner User", first.CreatedByName);

        // Filter by batch.
        var byBatch = await _sut.GetHistoryAsync(batchId: _liveBatchId);
        Assert.Single(byBatch);
        Assert.Equal(AdjustmentType.Breakage, byBatch[0].Type);
        Assert.Equal("first", byBatch[0].Reason);
    }

    // ---- IST-aware history window (Phase 2g — IST-stamping) ----

    [Fact]
    public async Task GetHistory_DateWindow_BucketsByIstDay_NotUtcDay()
    {
        // The operator picks LOCAL (IST) calendar dates; adjustment rows are stamped in UTC. For the
        // IST day D = 2026-06-15: an adjustment at IST D 00:05 (== 2026-06-14 18:35Z) must appear when
        // filtering [D, D], while one at IST D+1 00:05 (== 2026-06-15 18:35Z) must NOT — even though a
        // naive UTC-day filter on 06-15 would flip both. Host-timezone-independent (IST provider in ctor).
        var db = _fixture.Context;

        // Two audit rows created via the real path, then their Date overridden to explicit instants.
        await _sut.AdjustByDeltaAsync(_liveBatchId, -1m, "inDay", _userId, UserRole.Owner);
        await _sut.AdjustByDeltaAsync(_liveBatchId, -1m, "outDay", _userId, UserRole.Owner);

        StockAdjustment inDay = await db.StockAdjustments.SingleAsync(a => a.Reason == "inDay");
        StockAdjustment outDay = await db.StockAdjustments.SingleAsync(a => a.Reason == "outDay");
        inDay.Date = new DateTime(2026, 6, 14, 18, 35, 0, DateTimeKind.Utc);   // IST 2026-06-15 00:05
        outDay.Date = new DateTime(2026, 6, 15, 18, 35, 0, DateTimeKind.Utc);  // IST 2026-06-16 00:05
        await db.SaveChangesAsync();

        DateTime d = new(2026, 6, 15);
        var window = await _sut.GetHistoryAsync(from: d, to: d);

        // Only the IST-D-in-window row is returned; the IST-D+1 row is excluded.
        Assert.Single(window);
        Assert.Equal("inDay", window[0].Reason);
    }

    // ---- Read models ----

    [Fact]
    public async Task GetAdjustableBatches_ReturnsInStockBatches_WithExpiredFlag()
    {
        var batches = await _sut.GetAdjustableBatchesAsync();

        // All four seeded batches carry stock.
        Assert.Equal(4, batches.Count);
        Assert.True(batches.Single(b => b.BatchNo == "EXP1").IsExpired);
        Assert.False(batches.Single(b => b.BatchNo == "NEAR").IsExpired);
    }

    [Fact]
    public async Task GetExpiredBatches_ReturnsOnlyExpiredWithStock()
    {
        var expired = await _sut.GetExpiredBatchesAsync();
        Assert.Equal(2, expired.Count);
        Assert.All(expired, b => Assert.True(b.IsExpired));
        Assert.DoesNotContain(expired, b => b.BatchNo == "NEAR");
    }
}
