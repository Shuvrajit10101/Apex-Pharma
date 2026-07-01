using System;
using System.Linq;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// BillingService tests (plan.md §6.1, §9, §12, §14) — the flagship money/stock path. Cover
/// FEFO (earliest-expiry first, spanning lots, never dispensing expired stock), insufficient
/// stock rejecting the WHOLE sale, GST header totals, unique + sequential bill numbers,
/// Schedule H/H1 capture, credit/khata balance, cash-needs-no-customer, exact batch decrement,
/// transaction atomicity (a mid-sale failure persists NOTHING), and RBAC.
/// </summary>
public class BillingServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly BillingService _sut;
    private int _userId;
    private int _supplierId;
    private int _catId;
    private int _manId;

    public BillingServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        var gst = new GstService();
        _sut = new BillingService(_fixture.Context, auth, gst);
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

    private Product AddProduct(string name, decimal gstRate = 12m, DrugSchedule schedule = DrugSchedule.None)
    {
        var db = _fixture.Context;
        var p = new Product
        {
            Name = name,
            CategoryId = _catId,
            ManufacturerId = _manId,
            GstRate = gstRate,
            Schedule = schedule,
            IsActive = true,
            ReorderLevel = 0,
        };
        db.Products.Add(p);
        db.SaveChanges();
        return p;
    }

    private Batch AddBatch(int productId, string batchNo, decimal qty, decimal salePrice, DateTime expiry, decimal mrp = 0m)
    {
        var db = _fixture.Context;
        var b = new Batch
        {
            ProductId = productId,
            BatchNo = batchNo,
            ExpiryDate = expiry,
            Mrp = mrp == 0m ? salePrice : mrp,
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

    private int AddCustomer(string name = "Ravi", decimal creditLimit = 1000m, decimal balance = 0m)
    {
        var db = _fixture.Context;
        var c = new Customer { Name = name, CreditLimit = creditLimit, Balance = balance };
        db.Customers.Add(c);
        db.SaveChanges();
        return c.CustomerId;
    }

    private static SaleInput Sale(PaymentMode mode, params SaleLineInput[] lines)
        => new() { PaymentMode = mode, Lines = lines.ToList() };

    private static SaleLineInput Line(int productId, decimal qty, decimal lineDiscount = 0m)
        => new() { ProductId = productId, Qty = qty, LineDiscount = lineDiscount };

    // ---- FEFO ----

    [Fact]
    public async Task Fefo_PicksEarliestExpiryFirst()
    {
        var p = AddProduct("Paracetamol");
        var later = AddBatch(p.ProductId, "LATER", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(2));
        var earlier = AddBatch(p.ProductId, "EARLY", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddMonths(3));

        var result = await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 5m)), UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        var item = await db.SaleItems.SingleAsync();
        Assert.Equal(earlier.BatchId, item.BatchId); // earliest-expiry lot used
        Assert.Equal(5m, (await db.Batches.SingleAsync(b => b.BatchId == earlier.BatchId)).QtyOnHand);
        Assert.Equal(10m, (await db.Batches.SingleAsync(b => b.BatchId == later.BatchId)).QtyOnHand);
    }

    [Fact]
    public async Task Fefo_SpansMultipleBatches()
    {
        var p = AddProduct("Paracetamol");
        var b1 = AddBatch(p.ProductId, "B1", qty: 4m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddMonths(2));
        var b2 = AddBatch(p.ProductId, "B2", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddMonths(6));

        var result = await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 7m)), UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        var items = await db.SaleItems.OrderBy(i => i.BatchId).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal(0m, (await db.Batches.SingleAsync(b => b.BatchId == b1.BatchId)).QtyOnHand); // 4 taken
        Assert.Equal(7m, (await db.Batches.SingleAsync(b => b.BatchId == b2.BatchId)).QtyOnHand); // 3 taken
    }

    [Fact]
    public async Task Fefo_NeverDispensesExpiredBatch()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "EXPIRED", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddDays(-1));
        var good = AddBatch(p.ProductId, "GOOD", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddMonths(6));

        var result = await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 5m)), UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        var item = await db.SaleItems.SingleAsync();
        Assert.Equal(good.BatchId, item.BatchId);
    }

    [Fact]
    public async Task InsufficientStock_RejectsWholeSale()
    {
        var p1 = AddProduct("Paracetamol");
        var p2 = AddProduct("Amoxicillin");
        var okBatch = AddBatch(p1.ProductId, "OK", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));
        AddBatch(p2.ProductId, "SHORT", qty: 2m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        // Second line asks for 5 but only 2 exist → whole sale rejected, first line NOT dispensed.
        var result = await _sut.CreateSaleAsync(
            Sale(PaymentMode.Cash, Line(p1.ProductId, 3m), Line(p2.ProductId, 5m)), UserRole.Owner, _userId);

        Assert.False(result.Succeeded);
        Assert.Contains("Insufficient stock", result.Error!);
        var db = _fixture.NewContext();
        Assert.Equal(0, await db.Sales.CountAsync());
        Assert.Equal(0, await db.SaleItems.CountAsync());
        Assert.Equal(10m, (await db.Batches.SingleAsync(b => b.BatchId == okBatch.BatchId)).QtyOnHand); // untouched
    }

    [Fact]
    public async Task ExpiredOnlyStock_IsTreatedAsInsufficient()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "EXPIRED", qty: 50m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddDays(-1));

        var result = await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 1m)), UserRole.Owner, _userId);

        Assert.False(result.Succeeded);
        Assert.Contains("Insufficient stock", result.Error!);
    }

    // ---- GST header totals ----

    [Fact]
    public async Task GstHeaderTotals_AreCorrect()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));

        // 10 @ 20 = 200 base; 12% GST => 24 (12 CGST + 12 SGST). Total 224 (already whole).
        var result = await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 10m)), UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var r = result.Value;
        Assert.Equal(200m, r.Subtotal);
        Assert.Equal(12m, r.Cgst);
        Assert.Equal(12m, r.Sgst);
        Assert.Equal(224m, r.Total);
        Assert.Equal(0m, r.RoundOff);
    }

    [Fact]
    public async Task LineDiscount_AppliedBeforeTax()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));

        // 10 @ 20 = 200; discount 20 => net 180; 12% => 21.6 (10.80 + 10.80). Total 201.6 -> round 202.
        var result = await _sut.CreateSaleAsync(
            Sale(PaymentMode.Cash, Line(p.ProductId, 10m, lineDiscount: 20m)), UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var r = result.Value;
        Assert.Equal(180m, r.Subtotal);
        Assert.Equal(20m, r.Discount);
        Assert.Equal(10.80m, r.Cgst);
        Assert.Equal(10.80m, r.Sgst);
        Assert.Equal(202m, r.Total); // 201.60 rounded away-from-zero
        Assert.Equal(0.40m, r.RoundOff);
    }

    // ---- Bill numbering ----

    [Fact]
    public async Task BillNo_IsUniqueAndSequential_AcrossTwoSales()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var first = await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 1m)), UserRole.Owner, _userId);
        var second = await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 1m)), UserRole.Owner, _userId);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal("INV-000001", first.Value.BillNo);
        Assert.Equal("INV-000002", second.Value.BillNo);
        Assert.NotEqual(first.Value.BillNo, second.Value.BillNo);
    }

    [Fact]
    public async Task BillNo_IsUnique_InDatabase()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        for (int i = 0; i < 5; i++)
        {
            await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 1m)), UserRole.Owner, _userId);
        }

        var db = _fixture.NewContext();
        var billNos = await db.Sales.Select(s => s.BillNo).ToListAsync();
        Assert.Equal(5, billNos.Count);
        Assert.Equal(billNos.Count, billNos.Distinct().Count());
        Assert.Contains("INV-000005", billNos);
    }

    // ---- Schedule H/H1 ----

    [Fact]
    public async Task ScheduleH_WithoutDoctorOrRx_IsRejected()
    {
        var p = AddProduct("Antibiotic", schedule: DrugSchedule.H1);
        AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 1m)); // no doctor/Rx
        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.False(result.Succeeded);
        Assert.Contains("Schedule H", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().Sales.CountAsync());
    }

    [Fact]
    public async Task ScheduleH_WithDoctorAndRx_Succeeds()
    {
        var p = AddProduct("Antibiotic", schedule: DrugSchedule.H);
        AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 1m));
        input.DoctorName = "Dr. Sharma";
        input.PrescriptionRef = "RX-100";

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        var sale = await db.Sales.SingleAsync();
        Assert.Equal("Dr. Sharma", sale.DoctorName);
        Assert.Equal("RX-100", sale.PrescriptionRef);
    }

    // ---- Credit / khata ----

    [Fact]
    public async Task CreditSale_RequiresCustomer()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Credit, Line(p.ProductId, 1m)); // no customer
        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.False(result.Succeeded);
        Assert.Contains("requires a customer", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().Sales.CountAsync());
    }

    [Fact]
    public async Task CreditSale_IncreasesCustomerBalanceByTotal()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));
        int customerId = AddCustomer(balance: 50m);

        // 10 @ 20 = 200; 12% => 24; total 224. Balance 50 -> 274.
        var input = Sale(PaymentMode.Credit, Line(p.ProductId, 10m));
        input.CustomerId = customerId;

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        Assert.Equal(224m, result.Value.Total);
        var customer = await _fixture.NewContext().Customers.SingleAsync(c => c.CustomerId == customerId);
        Assert.Equal(274m, customer.Balance);
    }

    [Fact]
    public async Task CashSale_NeedsNoCustomer_AndDoesNotTouchAnyBalance()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var result = await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 1m)), UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        Assert.Null((await db.Sales.SingleAsync()).CustomerId);
    }

    // ---- Stock decrement / atomicity ----

    [Fact]
    public async Task BatchStock_DecrementedExactly_AcrossMultipleBatches()
    {
        var p = AddProduct("Paracetamol");
        var b1 = AddBatch(p.ProductId, "B1", qty: 5m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddMonths(1));
        var b2 = AddBatch(p.ProductId, "B2", qty: 5m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddMonths(2));

        await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 8m)), UserRole.Owner, _userId);

        var db = _fixture.NewContext();
        Assert.Equal(0m, (await db.Batches.SingleAsync(b => b.BatchId == b1.BatchId)).QtyOnHand);
        Assert.Equal(2m, (await db.Batches.SingleAsync(b => b.BatchId == b2.BatchId)).QtyOnHand);
        Assert.True((await db.Batches.ToListAsync()).All(b => b.QtyOnHand >= 0));
    }

    [Fact]
    public async Task TwoLinesSameProduct_ShareStock_WithoutOverselling()
    {
        var p = AddProduct("Paracetamol");
        var b = AddBatch(p.ProductId, "B1", qty: 5m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddMonths(1));

        // Two lines each ask 3 from the same 5-unit lot → 6 > 5, whole sale rejected.
        var result = await _sut.CreateSaleAsync(
            Sale(PaymentMode.Cash, Line(p.ProductId, 3m), Line(p.ProductId, 3m)), UserRole.Owner, _userId);

        Assert.False(result.Succeeded);
        Assert.Equal(5m, (await _fixture.NewContext().Batches.SingleAsync(x => x.BatchId == b.BatchId)).QtyOnHand);
    }

    [Fact]
    public async Task FailedSaleMidway_PersistsNothing()
    {
        var p1 = AddProduct("Paracetamol", gstRate: 12m);
        var p2 = AddProduct("Antibiotic", schedule: DrugSchedule.H1); // scheduled, no doctor => fail late
        var b1 = AddBatch(p1.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));
        AddBatch(p2.ProductId, "B2", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));
        int customerId = AddCustomer(balance: 0m);

        var input = Sale(PaymentMode.Credit, Line(p1.ProductId, 5m), Line(p2.ProductId, 5m));
        input.CustomerId = customerId; // credit + scheduled-without-doctor: fails after lines processed

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.False(result.Succeeded);
        var db = _fixture.NewContext();
        Assert.Equal(0, await db.Sales.CountAsync());       // no bill
        Assert.Equal(0, await db.SaleItems.CountAsync());   // no lines
        Assert.Equal(100m, (await db.Batches.SingleAsync(b => b.BatchId == b1.BatchId)).QtyOnHand); // no stock change
        Assert.Equal(0m, (await db.Customers.SingleAsync(c => c.CustomerId == customerId)).Balance); // no khata change
        Assert.Null(await db.Settings.FirstOrDefaultAsync(s => s.Key == "Billing.NextBillNo")); // counter untouched
    }

    // ---- RBAC ----

    [Fact]
    public async Task Sale_WithoutDoBilling_IsRefused()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        // A role without DoBilling. Simulate by a bogus role value not granted DoBilling —
        // use a custom role via HasPermission: only Owner/Pharmacist/Cashier have DoBilling.
        // There is no 4th role, so assert the three that DO and craft a refusal via a role that
        // maps to none. We use (UserRole)999 to represent an unknown/ungranted role.
        var result = await _sut.CreateSaleAsync(
            Sale(PaymentMode.Cash, Line(p.ProductId, 1m)), (UserRole)999, _userId);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().Sales.CountAsync());
    }

    [Fact]
    public async Task Sale_AsCashier_Succeeds()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var result = await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 1m)), UserRole.Cashier, _userId);

        Assert.True(result.Succeeded);
    }
}
