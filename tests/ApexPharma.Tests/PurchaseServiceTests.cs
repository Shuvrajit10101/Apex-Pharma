using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// PurchaseService tests (plan.md §6.1, §9, §12). Cover the stock-in path (new batches +
/// increased stock), batch upsert (same product/batch ADDS), header GST roll-up,
/// transaction atomicity (a bad line persists nothing), every validation branch, RBAC, and
/// purchase returns (decrement the right batch; reject over-return — no negative stock).
/// </summary>
public class PurchaseServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly PurchaseService _sut;
    private int _userId;
    private int _supplierId;
    private int _productId;
    private int _productBId;

    public PurchaseServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        var gst = new GstService();
        _sut = new PurchaseService(_fixture.Context, auth, gst);
        Seed();
    }

    public void Dispose() => _fixture.Dispose();

    private void Seed()
    {
        var db = _fixture.Context;

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

        var product = new Product { Name = "Paracetamol 500", CategoryId = cat.CategoryId, ManufacturerId = man.ManufacturerId, GstRate = 12m, IsActive = true, ReorderLevel = 10 };
        var productB = new Product { Name = "Amoxicillin 250", CategoryId = cat.CategoryId, ManufacturerId = man.ManufacturerId, GstRate = 5m, IsActive = true, ReorderLevel = 5 };
        db.Products.AddRange(product, productB);
        db.SaveChanges();

        _userId = user.UserId;
        _supplierId = supplier.SupplierId;
        _productId = product.ProductId;
        _productBId = productB.ProductId;
    }

    private PurchaseLineInput Line(int? productId = null, string batchNo = "B001", decimal qty = 10m, decimal purchasePrice = 20m, decimal mrp = 30m, decimal gst = 12m, DateTime? expiry = null)
        => new()
        {
            ProductId = productId ?? _productId,
            BatchNo = batchNo,
            ExpiryDate = expiry ?? DateTime.UtcNow.Date.AddYears(1),
            Qty = qty,
            PurchasePrice = purchasePrice,
            Mrp = mrp,
            GstRate = gst,
        };

    private PurchaseInput Purchase(params PurchaseLineInput[] lines)
        => new()
        {
            SupplierId = _supplierId,
            SupplierInvoiceNo = "INV-1",
            InvoiceDate = DateTime.UtcNow.Date,
            Lines = lines.ToList(),
        };

    // ---- Stock-in / batch creation ----

    [Fact]
    public async Task RecordPurchase_CreatesBatch_AndAddsStock()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(Line(qty: 15m)), _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        var batch = await _fixture.NewContext().Batches.SingleAsync(b => b.ProductId == _productId && b.BatchNo == "B001");
        Assert.Equal(15m, batch.QtyOnHand);
        Assert.Equal(30m, batch.Mrp);
        Assert.Equal(20m, batch.PurchasePrice);
        Assert.Equal(30m, batch.SalePrice); // SalePrice defaults to MRP
        Assert.Equal(_supplierId, batch.SupplierId);
    }

    [Fact]
    public async Task SecondPurchase_SameProductAndBatch_AddsToExistingBatch()
    {
        await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "B001", qty: 10m)), _userId, UserRole.Owner);
        await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "B001", qty: 7m)), _userId, UserRole.Owner);

        var batches = await _fixture.NewContext().Batches.Where(b => b.ProductId == _productId && b.BatchNo == "B001").ToListAsync();
        Assert.Single(batches);
        Assert.Equal(17m, batches[0].QtyOnHand);
    }

    [Fact]
    public async Task RecordPurchase_TwoLines_SameProductAndBatch_MergeIntoOneLot()
    {
        // Two lines in ONE purchase for the same (product, batch) must accumulate into a
        // single Batch row (10 + 7 = 17), not create two duplicate rows.
        var result = await _sut.RecordPurchaseAsync(
            Purchase(
                Line(batchNo: "DUP", qty: 10m),
                Line(batchNo: "DUP", qty: 7m)),
            _userId, UserRole.Owner);

        Assert.True(result.Succeeded);

        var db = _fixture.NewContext();
        var batches = await db.Batches.Where(b => b.ProductId == _productId && b.BatchNo == "DUP").ToListAsync();
        Assert.Single(batches);
        Assert.Equal(17m, batches[0].QtyOnHand);

        // The whole merged lot is reachable: FEFO should surface it and a return can pull it all.
        var inventory = new InventoryService(_fixture.NewContext(), TestTz.IstProvider());
        var fefo = await inventory.SelectBatchFefoAsync(_productId, requiredQty: 17m);
        Assert.NotNull(fefo);
        Assert.Equal("DUP", fefo!.BatchNo);

        var returnResult = await _sut.ProcessPurchaseReturnAsync(
            result.Value!.PurchaseId, batches[0].BatchId, 17m, "recall", _userId, UserRole.Owner);
        Assert.True(returnResult.Succeeded);
        var reloaded = await _fixture.NewContext().Batches.SingleAsync(b => b.BatchId == batches[0].BatchId);
        Assert.Equal(0m, reloaded.QtyOnHand);
    }

    [Fact]
    public async Task RecordPurchase_RollsUpHeaderGstTotals()
    {
        // Line: 10 @ 20 = 200 base; GST 12% => 24 total (12 CGST + 12 SGST). Total 224.
        var result = await _sut.RecordPurchaseAsync(Purchase(Line(qty: 10m, purchasePrice: 20m, gst: 12m)), _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        var p = result.Value!;
        Assert.Equal(200m, p.Subtotal);
        Assert.Equal(24m, p.GstAmount);
        Assert.Equal(224m, p.Total);
    }

    [Fact]
    public async Task RecordPurchase_MultipleLines_SumsTotalsAndCreatesBatches()
    {
        var result = await _sut.RecordPurchaseAsync(
            Purchase(
                Line(productId: _productId, batchNo: "PA1", qty: 10m, purchasePrice: 20m, gst: 12m),   // base 200, gst 24
                Line(productId: _productBId, batchNo: "AM1", qty: 4m, purchasePrice: 50m, gst: 5m)),    // base 200, gst 10
            _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Equal(400m, result.Value!.Subtotal);
        Assert.Equal(34m, result.Value.GstAmount);
        Assert.Equal(434m, result.Value.Total);

        var db = _fixture.NewContext();
        Assert.Equal(2, await db.Batches.CountAsync());
    }

    // ---- Transaction atomicity ----

    [Fact]
    public async Task RecordPurchase_WithOneInvalidLine_PersistsNothing()
    {
        // First line valid, second has qty 0 → whole purchase must roll back.
        var result = await _sut.RecordPurchaseAsync(
            Purchase(Line(batchNo: "OK", qty: 10m), Line(batchNo: "BAD", qty: 0m)),
            _userId, UserRole.Owner);

        Assert.False(result.Succeeded);

        var db = _fixture.NewContext();
        Assert.Equal(0, await db.Purchases.CountAsync());
        Assert.Equal(0, await db.PurchaseItems.CountAsync());
        Assert.Equal(0, await db.Batches.CountAsync());
    }

    // ---- Validation branches ----

    [Fact]
    public async Task RecordPurchase_NoSupplier_Fails()
    {
        var input = Purchase(Line());
        input.SupplierId = 9999;

        var result = await _sut.RecordPurchaseAsync(input, _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("supplier", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordPurchase_EmptyLines_Fails()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("at least one line", result.Error!);
    }

    [Fact]
    public async Task RecordPurchase_QtyZero_Fails()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(Line(qty: 0m)), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("quantity", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordPurchase_NegativePurchasePrice_Fails()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(Line(purchasePrice: -1m)), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("Purchase price", result.Error!);
    }

    [Fact]
    public async Task RecordPurchase_NegativeMrp_Fails()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(Line(mrp: -5m)), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("MRP", result.Error!);
    }

    [Fact]
    public async Task RecordPurchase_BadGstRate_Fails()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(Line(gst: 7m)), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("GST rate", result.Error!);
    }

    [Fact]
    public async Task RecordPurchase_MissingBatchNo_Fails()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "   ")), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("Batch number", result.Error!);
    }

    [Fact]
    public async Task RecordPurchase_ExpiryToday_Fails()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(Line(expiry: DateTime.UtcNow.Date)), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("Expiry", result.Error!);
    }

    [Fact]
    public async Task RecordPurchase_ExpiryInPast_Fails()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(Line(expiry: DateTime.UtcNow.Date.AddDays(-1))), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("Expiry", result.Error!);
    }

    [Fact]
    public async Task RecordPurchase_FutureInvoiceDate_Fails_AndPersistsNothing()
    {
        // A supplier invoice cannot be dated in the future (plan.md §14). The guard runs before
        // any DB write, so no purchase, item, or batch is persisted.
        var input = Purchase(Line());
        input.InvoiceDate = DateTime.UtcNow.Date.AddDays(1);

        var result = await _sut.RecordPurchaseAsync(input, _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("future", result.Error!, StringComparison.OrdinalIgnoreCase);

        var db = _fixture.NewContext();
        Assert.Equal(0, await db.Purchases.CountAsync());
        Assert.Equal(0, await db.PurchaseItems.CountAsync());
        Assert.Equal(0, await db.Batches.CountAsync());
    }

    [Fact]
    public async Task RecordPurchase_InactiveProduct_Fails()
    {
        var db = _fixture.Context;
        var p = await db.Products.FirstAsync(x => x.ProductId == _productId);
        p.IsActive = false;
        await db.SaveChangesAsync();

        var result = await _sut.RecordPurchaseAsync(Purchase(Line()), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("inactive", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordPurchase_MissingProduct_Fails()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(Line(productId: 9999)), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", result.Error!);
    }

    // ---- RBAC ----

    [Fact]
    public async Task RecordPurchase_AsCashier_IsRefused()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(Line()), _userId, UserRole.Cashier);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().Purchases.CountAsync());
    }

    [Fact]
    public async Task RecordPurchase_AsPharmacist_Succeeds()
    {
        var result = await _sut.RecordPurchaseAsync(Purchase(Line()), _userId, UserRole.Pharmacist);

        Assert.True(result.Succeeded);
    }

    // ---- Purchase returns ----

    [Fact]
    public async Task PurchaseReturn_DecrementsTheRightBatch()
    {
        var purchase = (await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "B001", qty: 10m)), _userId, UserRole.Owner)).Value!;
        var batch = await _fixture.NewContext().Batches.SingleAsync(b => b.ProductId == _productId && b.BatchNo == "B001");

        var result = await _sut.ProcessPurchaseReturnAsync(purchase.PurchaseId, batch.BatchId, 4m, "damaged", _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        var reloaded = await _fixture.NewContext().Batches.SingleAsync(b => b.BatchId == batch.BatchId);
        Assert.Equal(6m, reloaded.QtyOnHand);
        Assert.Equal(80m, result.Value!.Amount); // 4 @ purchase price 20
    }

    [Fact]
    public async Task PurchaseReturn_OverReturn_IsRejected_NoNegativeStock()
    {
        var purchase = (await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "B001", qty: 10m)), _userId, UserRole.Owner)).Value!;
        var batch = await _fixture.NewContext().Batches.SingleAsync(b => b.ProductId == _productId && b.BatchNo == "B001");

        var result = await _sut.ProcessPurchaseReturnAsync(purchase.PurchaseId, batch.BatchId, 15m, "too many", _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("only 10", result.Error!);
        var reloaded = await _fixture.NewContext().Batches.SingleAsync(b => b.BatchId == batch.BatchId);
        Assert.Equal(10m, reloaded.QtyOnHand); // unchanged
        Assert.Equal(0, await _fixture.NewContext().PurchaseReturns.CountAsync());
    }

    [Fact]
    public async Task PurchaseReturn_AsCashier_IsRefused()
    {
        var purchase = (await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "B001", qty: 10m)), _userId, UserRole.Owner)).Value!;
        var batch = await _fixture.NewContext().Batches.SingleAsync(b => b.ProductId == _productId && b.BatchNo == "B001");

        var result = await _sut.ProcessPurchaseReturnAsync(purchase.PurchaseId, batch.BatchId, 1m, null, _userId, UserRole.Cashier);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
    }

    [Fact]
    public async Task PurchaseReturn_TracksPerLine_CumulativeOverReturnBlocked()
    {
        // Purchase 10; return 6 (ok) then attempt 6 more -> only 4 remain returnable on the line.
        var purchase = (await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "B001", qty: 10m)), _userId, UserRole.Owner)).Value!;
        var item = await _fixture.NewContext().PurchaseItems.SingleAsync(pi => pi.PurchaseId == purchase.PurchaseId);

        var first = await _sut.ProcessPurchaseReturnLineAsync(item.PurchaseItemId, 6m, "damaged", _userId, UserRole.Owner);
        Assert.True(first.Succeeded);

        var second = await _sut.ProcessPurchaseReturnLineAsync(item.PurchaseItemId, 6m, "more", _userId, UserRole.Owner);
        Assert.False(second.Succeeded);
        Assert.Contains("only 4", second.Error!);

        // Only the first return persisted; batch is 10 - 6 = 4.
        Assert.Equal(1, await _fixture.NewContext().PurchaseReturns.CountAsync());
        var batch = await _fixture.NewContext().Batches.SingleAsync(b => b.ProductId == _productId && b.BatchNo == "B001");
        Assert.Equal(4m, batch.QtyOnHand);
    }

    [Fact]
    public async Task PurchaseReturnLine_RecordsPurchaseItemId()
    {
        var purchase = (await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "B001", qty: 10m)), _userId, UserRole.Owner)).Value!;
        var item = await _fixture.NewContext().PurchaseItems.SingleAsync(pi => pi.PurchaseId == purchase.PurchaseId);

        var result = await _sut.ProcessPurchaseReturnLineAsync(item.PurchaseItemId, 3m, "short-dated", _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        var pr = await _fixture.NewContext().PurchaseReturns.SingleAsync();
        Assert.Equal(item.PurchaseItemId, pr.PurchaseItemId);
        Assert.Equal(3m, pr.Qty);
        Assert.Equal(60m, pr.Amount); // 3 @ purchase price 20
    }

    [Fact]
    public async Task PurchaseReturnLine_NeverDrivesStockNegative_WhenPartlySold()
    {
        // Purchase 10, then 8 units leave via a separate stock movement, leaving 2 on hand.
        // A line-return of 5 is within purchased-minus-returned (10) but exceeds on-hand (2):
        // the non-negative stock backstop must reject it.
        var purchase = (await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "B001", qty: 10m)), _userId, UserRole.Owner)).Value!;
        var item = await _fixture.NewContext().PurchaseItems.SingleAsync(pi => pi.PurchaseId == purchase.PurchaseId);

        // Mutate via the SUT's own (tracked) context so the service sees the reduced stock.
        var live = await _fixture.Context.Batches.SingleAsync(x => x.ProductId == _productId && x.BatchNo == "B001");
        live.QtyOnHand = 2m; // simulate 8 sold
        await _fixture.Context.SaveChangesAsync();

        var result = await _sut.ProcessPurchaseReturnLineAsync(item.PurchaseItemId, 5m, "recall", _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("only 2", result.Error!);
        Assert.Equal(2m, (await _fixture.NewContext().Batches.SingleAsync(x => x.ProductId == _productId && x.BatchNo == "B001")).QtyOnHand);
        Assert.Equal(0, await _fixture.NewContext().PurchaseReturns.CountAsync());
    }

    [Fact]
    public async Task PurchaseReturnLine_AsCashier_IsRefused()
    {
        var purchase = (await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "B001", qty: 10m)), _userId, UserRole.Owner)).Value!;
        var item = await _fixture.NewContext().PurchaseItems.SingleAsync(pi => pi.PurchaseId == purchase.PurchaseId);

        var result = await _sut.ProcessPurchaseReturnLineAsync(item.PurchaseItemId, 1m, null, _userId, UserRole.Cashier);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
    }

    [Fact]
    public async Task GetReturnableLines_Purchase_ReportsPurchasedReturnedRemaining()
    {
        var purchase = (await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "B001", qty: 10m)), _userId, UserRole.Owner)).Value!;
        var item = await _fixture.NewContext().PurchaseItems.SingleAsync(pi => pi.PurchaseId == purchase.PurchaseId);
        await _sut.ProcessPurchaseReturnLineAsync(item.PurchaseItemId, 4m, null, _userId, UserRole.Owner);

        var lines = await _sut.GetReturnableLinesAsync(purchase.PurchaseId);

        Assert.True(lines.Succeeded);
        var line = Assert.Single(lines.Value!.Lines);
        Assert.Equal(10m, line.PurchasedQty);
        Assert.Equal(4m, line.ReturnedQty);
        Assert.Equal(6m, line.RemainingQty); // min(10-4, on-hand 6)
    }

    [Fact]
    public async Task GetRecentPurchases_ReturnsNewestFirst_WithSupplierAndItems()
    {
        await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "OLD")), _userId, UserRole.Owner);
        await _sut.RecordPurchaseAsync(Purchase(Line(batchNo: "NEW")), _userId, UserRole.Owner);

        var recent = await _sut.GetRecentPurchasesAsync();

        Assert.Equal(2, recent.Count);
        Assert.NotNull(recent[0].Supplier);
        Assert.NotEmpty(recent[0].Items);
    }
}
