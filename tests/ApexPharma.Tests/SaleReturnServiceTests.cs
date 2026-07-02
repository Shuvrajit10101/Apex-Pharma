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
/// SaleReturnService tests (plan.md §6.1, §12) — the sales-return money/stock path. Cover:
/// restocking the exact dispensed batch, proportional CGST/SGST reversal, credit-sale khata
/// reduction by the returned total, over-return blocked (per-line cumulative), transaction
/// atomicity (a bad return persists nothing), and RBAC (DoBilling required).
/// </summary>
public class SaleReturnServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly BillingService _billing;
    private readonly SaleReturnService _sut;
    private int _userId;
    private int _supplierId;
    private int _catId;
    private int _manId;

    public SaleReturnServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        var gst = new GstService();
        _billing = new BillingService(_fixture.Context, auth, gst);
        _sut = new SaleReturnService(_fixture.Context, auth);
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
        _userId = user.UserId;
        _supplierId = supplier.SupplierId;
        _catId = cat.CategoryId;
        _manId = man.ManufacturerId;
    }

    private Product AddProduct(string name, decimal gstRate = 12m)
    {
        var db = _fixture.Context;
        var p = new Product { Name = name, CategoryId = _catId, ManufacturerId = _manId, GstRate = gstRate, IsActive = true, ReorderLevel = 0 };
        db.Products.Add(p);
        db.SaveChanges();
        return p;
    }

    private Batch AddBatch(int productId, string batchNo, decimal qty, decimal salePrice, DateTime expiry)
    {
        var db = _fixture.Context;
        var b = new Batch
        {
            ProductId = productId,
            BatchNo = batchNo,
            ExpiryDate = expiry,
            Mrp = salePrice,
            PurchasePrice = salePrice,
            SalePrice = salePrice,
            QtyOnHand = qty,
            SupplierId = _supplierId,
            ReceivedDate = DateTime.UtcNow,
        };
        db.Batches.Add(b);
        db.SaveChanges();
        return b;
    }

    private int AddCustomer(decimal balance)
    {
        var db = _fixture.Context;
        var c = new Customer { Name = "Ravi", CreditLimit = 100000m, Balance = balance };
        db.Customers.Add(c);
        db.SaveChanges();
        return c.CustomerId;
    }

    private static SaleInput Sale(PaymentMode mode, params SaleLineInput[] lines)
        => new() { PaymentMode = mode, Lines = lines.ToList() };

    private static SaleLineInput Line(int productId, decimal qty)
        => new() { ProductId = productId, Qty = qty };

    private static SaleReturnInput ReturnOf(string billNo, int saleItemId, decimal qty, string? reason = "customer changed mind")
        => new() { BillNo = billNo, Reason = reason, Lines = { new SaleReturnLineInput { SaleItemId = saleItemId, Qty = qty } } };

    // ---- Restock the correct batch ----

    [Fact]
    public async Task SaleReturn_RestocksTheExactDispensedBatch()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        var b = AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var sale = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 10m)), UserRole.Owner, _userId);
        Assert.True(sale.Succeeded);
        int saleItemId = (await _fixture.NewContext().SaleItems.SingleAsync()).SaleItemId;
        // 100 sold-out -> 90 on hand after selling 10.
        Assert.Equal(90m, (await _fixture.NewContext().Batches.SingleAsync(x => x.BatchId == b.BatchId)).QtyOnHand);

        var result = await _sut.ProcessSaleReturnAsync(ReturnOf(sale.Value.BillNo, saleItemId, 4m), _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        // Restocked exactly the dispensed batch: 90 + 4 = 94.
        Assert.Equal(94m, (await _fixture.NewContext().Batches.SingleAsync(x => x.BatchId == b.BatchId)).QtyOnHand);
        Assert.Equal(4m, result.Value.TotalQty);
    }

    // ---- Proportional GST reversal ----

    [Fact]
    public async Task SaleReturn_ReversesCgstSgstProportionally()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));

        // 10 @ 20 = 200 taxable; 12% => 12 CGST + 12 SGST. Return 5 of 10 -> half.
        var sale = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 10m)), UserRole.Owner, _userId);
        int saleItemId = (await _fixture.NewContext().SaleItems.SingleAsync()).SaleItemId;

        var result = await _sut.ProcessSaleReturnAsync(ReturnOf(sale.Value.BillNo, saleItemId, 5m), _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Equal(100m, result.Value.TaxableReversed);  // half of 200
        Assert.Equal(6m, result.Value.Cgst);               // half of 12
        Assert.Equal(6m, result.Value.Sgst);
        Assert.Equal(112m, result.Value.TotalRefund);      // 100 + 6 + 6

        var sr = await _fixture.NewContext().SaleReturns.SingleAsync();
        Assert.Equal(saleItemId, sr.SaleItemId);
        Assert.Equal(6m, sr.Cgst);
        Assert.Equal(6m, sr.Sgst);
        Assert.Equal(112m, sr.Amount);
    }

    // ---- Credit / khata ----

    [Fact]
    public async Task SaleReturn_CreditSale_ReducesCustomerBalanceByReturnedTotal()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));
        int customerId = AddCustomer(balance: 0m);

        // 10 @ 20 = 200 + 24 GST = 224; balance -> 224.
        var input = Sale(PaymentMode.Credit, Line(p.ProductId, 10m));
        input.CustomerId = customerId;
        var sale = await _billing.CreateSaleAsync(input, UserRole.Owner, _userId);
        Assert.True(sale.Succeeded);
        Assert.Equal(224m, (await _fixture.NewContext().Customers.SingleAsync()).Balance);

        int saleItemId = (await _fixture.NewContext().SaleItems.SingleAsync()).SaleItemId;

        // Return all 10 -> refund 224 -> balance back to 0.
        var result = await _sut.ProcessSaleReturnAsync(ReturnOf(sale.Value.BillNo, saleItemId, 10m), _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Equal(224m, result.Value.TotalRefund);
        Assert.Equal(224m, result.Value.KhataReduced);
        Assert.Equal(0m, (await _fixture.NewContext().Customers.SingleAsync()).Balance);
    }

    [Fact]
    public async Task SaleReturn_CreditSale_NeverDrivesBalanceNegative()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));
        int customerId = AddCustomer(balance: 0m);

        var input = Sale(PaymentMode.Credit, Line(p.ProductId, 10m)); // +224
        input.CustomerId = customerId;
        var sale = await _billing.CreateSaleAsync(input, UserRole.Owner, _userId);
        int saleItemId = (await _fixture.NewContext().SaleItems.SingleAsync()).SaleItemId;

        // Customer pays off the whole balance before returning. Mutate via the SUT's own
        // (tracked) context so the service sees the paid-down balance.
        var live = await _fixture.Context.Customers.SingleAsync();
        live.Balance = 0m;
        await _fixture.Context.SaveChangesAsync();

        var result = await _sut.ProcessSaleReturnAsync(ReturnOf(sale.Value.BillNo, saleItemId, 10m), _userId, UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Equal(0m, result.Value.KhataReduced);            // nothing to reduce — already paid
        Assert.Equal(0m, (await _fixture.NewContext().Customers.SingleAsync()).Balance); // not negative
    }

    // ---- Over-return blocked ----

    [Fact]
    public async Task SaleReturn_OverReturn_IsBlocked()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));
        var sale = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 5m)), UserRole.Owner, _userId);
        int saleItemId = (await _fixture.NewContext().SaleItems.SingleAsync()).SaleItemId;

        var result = await _sut.ProcessSaleReturnAsync(ReturnOf(sale.Value.BillNo, saleItemId, 6m), _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("only 5", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().SaleReturns.CountAsync());
    }

    [Fact]
    public async Task SaleReturn_CumulativeOverReturn_IsBlocked()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));
        var sale = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 10m)), UserRole.Owner, _userId);
        int saleItemId = (await _fixture.NewContext().SaleItems.SingleAsync()).SaleItemId;

        // First return 7 of 10 (ok), then attempt 5 more -> only 3 remain, blocked.
        var first = await _sut.ProcessSaleReturnAsync(ReturnOf(sale.Value.BillNo, saleItemId, 7m), _userId, UserRole.Owner);
        Assert.True(first.Succeeded);

        var second = await _sut.ProcessSaleReturnAsync(ReturnOf(sale.Value.BillNo, saleItemId, 5m), _userId, UserRole.Owner);
        Assert.False(second.Succeeded);
        Assert.Contains("only 3", second.Error!);

        // Only the first return persisted; batch reflects only the +7 restock.
        Assert.Equal(1, await _fixture.NewContext().SaleReturns.CountAsync());
    }

    // ---- Atomicity ----

    [Fact]
    public async Task SaleReturn_BadLineInBatch_PersistsNothing()
    {
        var pa = AddProduct("Amoxicillin");
        var pb = AddProduct("Paracetamol");
        var ba = AddBatch(pa.ProductId, "A1", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));
        var bb = AddBatch(pb.ProductId, "B1", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var sale = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Cash, Line(pa.ProductId, 5m), Line(pb.ProductId, 5m)), UserRole.Owner, _userId);
        Assert.True(sale.Succeeded);
        var items = await _fixture.NewContext().SaleItems.OrderBy(i => i.SaleItemId).ToListAsync();
        decimal aBefore = (await _fixture.NewContext().Batches.SingleAsync(b => b.BatchId == ba.BatchId)).QtyOnHand;

        // First line valid (return 2), second line over-returns (return 99 of 5) -> whole return rolls back.
        var input = new SaleReturnInput
        {
            BillNo = sale.Value.BillNo,
            Reason = "mixed",
            Lines =
            {
                new SaleReturnLineInput { SaleItemId = items[0].SaleItemId, Qty = 2m },
                new SaleReturnLineInput { SaleItemId = items[1].SaleItemId, Qty = 99m },
            },
        };

        var result = await _sut.ProcessSaleReturnAsync(input, _userId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Equal(0, await _fixture.NewContext().SaleReturns.CountAsync());        // no return rows
        Assert.Equal(aBefore, (await _fixture.NewContext().Batches.SingleAsync(b => b.BatchId == ba.BatchId)).QtyOnHand); // no restock
    }

    // ---- Lookup for the UI ----

    [Fact]
    public async Task GetReturnableLines_ReportsSoldReturnedRemaining()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));
        var sale = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 10m)), UserRole.Owner, _userId);
        int saleItemId = (await _fixture.NewContext().SaleItems.SingleAsync()).SaleItemId;
        await _sut.ProcessSaleReturnAsync(ReturnOf(sale.Value.BillNo, saleItemId, 3m), _userId, UserRole.Owner);

        var lines = await _sut.GetReturnableLinesAsync(sale.Value.BillNo);

        Assert.True(lines.Succeeded);
        var line = Assert.Single(lines.Value!.Lines);
        Assert.Equal(10m, line.SoldQty);
        Assert.Equal(3m, line.ReturnedQty);
        Assert.Equal(7m, line.RemainingQty);
    }

    [Fact]
    public async Task GetReturnableLines_UnknownBill_Fails()
    {
        var result = await _sut.GetReturnableLinesAsync("INV-999999");
        Assert.False(result.Succeeded);
        Assert.Contains("No bill found", result.Error!);
    }

    // ---- RBAC ----

    [Fact]
    public async Task SaleReturn_WithoutDoBilling_IsRefused()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));
        var sale = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 5m)), UserRole.Owner, _userId);
        int saleItemId = (await _fixture.NewContext().SaleItems.SingleAsync()).SaleItemId;

        // (UserRole)999 maps to no permissions.
        var result = await _sut.ProcessSaleReturnAsync(ReturnOf(sale.Value.BillNo, saleItemId, 1m), _userId, (UserRole)999);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().SaleReturns.CountAsync());
    }

    [Fact]
    public async Task SaleReturn_AsCashier_Succeeds()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));
        var sale = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 5m)), UserRole.Owner, _userId);
        int saleItemId = (await _fixture.NewContext().SaleItems.SingleAsync()).SaleItemId;

        var result = await _sut.ProcessSaleReturnAsync(ReturnOf(sale.Value.BillNo, saleItemId, 2m), _userId, UserRole.Cashier);

        Assert.True(result.Succeeded);
    }
}
