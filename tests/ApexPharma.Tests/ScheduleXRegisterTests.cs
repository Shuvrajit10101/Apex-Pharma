using System;
using System.Linq;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Reporting;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// Schedule-X strict register tests (plan.md §14, §15 — Phase 2f). Assert the DERIVED
/// running balance (opening = net movement before the window; received = purchases −
/// purchase-returns in range; issued = sales − sales-returns in range; closing = opening +
/// received − issued) and that the dispense-detail rows come from the strict
/// <see cref="ScheduleXDispense"/> register. Sales run through the REAL
/// <see cref="BillingService"/> so the dispense rows and stock decrements are authoritative;
/// purchases and purchase-returns are seeded directly (there is no purchase service dependency
/// under test here).
/// </summary>
public class ScheduleXRegisterTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly BillingService _billing;
    private readonly SaleReturnService _returns;
    private readonly ReportService _sut;

    private int _userId;
    private int _supplierId;
    private int _catId;
    private int _manId;

    public ScheduleXRegisterTests()
    {
        var auth = new AuthService(_fixture.Context);
        var gst = new GstService();
        _billing = new BillingService(_fixture.Context, auth, gst);
        _returns = new SaleReturnService(_fixture.Context, auth);
        _sut = new ReportService(_fixture.Context, new InventoryService(_fixture.Context), TestTz.IstProvider());
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

    private Product AddProduct(string name, DrugSchedule schedule = DrugSchedule.X)
    {
        var db = _fixture.Context;
        var p = new Product
        {
            Name = name,
            CategoryId = _catId,
            ManufacturerId = _manId,
            GstRate = 12m,
            Schedule = schedule,
            IsActive = true,
        };
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

    /// <summary>Seeds a purchase line (received stock) dated to a specific instant.</summary>
    private void AddPurchase(int productId, string batchNo, decimal qty, DateTime invoiceDate)
    {
        var db = _fixture.Context;
        var purchase = new Purchase
        {
            SupplierId = _supplierId,
            InvoiceDate = invoiceDate,
            CreatedBy = _userId,
            CreatedAt = invoiceDate,
            Items =
            {
                new PurchaseItem
                {
                    ProductId = productId,
                    BatchNo = batchNo,
                    ExpiryDate = invoiceDate.AddYears(2),
                    Qty = qty,
                    PurchasePrice = 10m,
                    Mrp = 20m,
                    GstRate = 12m,
                },
            },
        };
        db.Purchases.Add(purchase);
        db.SaveChanges();
    }

    /// <summary>Seeds a purchase-return line (goods sent back to supplier) dated to an instant.</summary>
    private void AddPurchaseReturn(int batchId, decimal qty, DateTime date)
    {
        var db = _fixture.Context;
        db.PurchaseReturns.Add(new PurchaseReturn
        {
            PurchaseId = db.Purchases.First().PurchaseId,
            BatchId = batchId,
            Qty = qty,
            Amount = qty * 10m,
            Date = date,
            CreatedBy = _userId,
        });
        db.SaveChanges();
    }

    private static ScheduleXCapture FullXCapture() => new()
    {
        PatientName = "Anil Kumar",
        PatientAddress = "12 MG Road, Kolkata",
        PrescriberName = "Dr. Sen",
        PrescriberAddress = "Apollo Clinic",
        PrescriberRegNo = "WBMC-12345",
        PrescriptionNumber = "RX-X-777",
        PrescriptionDate = DateTime.Today,
        PrescriptionRetained = true,
    };

    private async Task SetBillDate(string billNo, DateTime date)
    {
        var db = _fixture.NewContext();
        Sale sale = await db.Sales.SingleAsync(s => s.BillNo == billNo);
        sale.BillDate = date;
        await db.SaveChangesAsync();
    }

    private async Task<int> FirstSaleItemId(string billNo)
    {
        var db = _fixture.NewContext();
        return await db.SaleItems
            .Where(i => i.Sale!.BillNo == billNo)
            .OrderBy(i => i.SaleItemId)
            .Select(i => i.SaleItemId)
            .FirstAsync();
    }

    [Fact]
    public async Task RunningBalance_Opening_Received_Issued_Closing_AreCorrect()
    {
        var x = AddProduct("Morphine", DrugSchedule.X);
        var b = AddBatch(x.ProductId, "MX1", qty: 1000m, salePrice: 40m, expiry: DateTime.UtcNow.Date.AddYears(2));

        // Window = June 2026.
        var windowFrom = new DateTime(2026, 6, 1);
        var windowTo = new DateTime(2026, 6, 30);

        // ---- BEFORE the window (opening) ----
        // Received 100 before, returned 10 to supplier before, sold 30 before, sale-returned 5 before.
        AddPurchase(x.ProductId, "MX1", 100m, new DateTime(2026, 5, 10));
        AddPurchaseReturn(b.BatchId, 10m, new DateTime(2026, 5, 12));

        var preSale = await _billing.CreateSaleAsync(XSale(x.ProductId, 30m), UserRole.Owner, _userId);
        Assert.True(preSale.Succeeded);
        await SetBillDate(preSale.Value!.BillNo, new DateTime(2026, 5, 20));
        int preItemId = await FirstSaleItemId(preSale.Value.BillNo);
        var preRet = await _returns.ProcessSaleReturnAsync(
            new SaleReturnInput { BillNo = preSale.Value.BillNo, Lines = { new SaleReturnLineInput { SaleItemId = preItemId, Qty = 5m } } },
            _userId, UserRole.Owner);
        Assert.True(preRet.Succeeded);
        await SetSaleReturnDate(new DateTime(2026, 5, 22));
        // Opening = +100 −10 −30 +5 = 65.

        // ---- IN the window ----
        // Received 50, returned 8 to supplier, sold 20, sale-returned 3.
        AddPurchase(x.ProductId, "MX1", 50m, new DateTime(2026, 6, 5));
        AddPurchaseReturn(b.BatchId, 8m, new DateTime(2026, 6, 6));

        var inSale = await _billing.CreateSaleAsync(XSale(x.ProductId, 20m), UserRole.Owner, _userId);
        Assert.True(inSale.Succeeded);
        await SetBillDate(inSale.Value!.BillNo, new DateTime(2026, 6, 10));
        int inItemId = await FirstSaleItemId(inSale.Value.BillNo);
        var inRet = await _returns.ProcessSaleReturnAsync(
            new SaleReturnInput { BillNo = inSale.Value.BillNo, Lines = { new SaleReturnLineInput { SaleItemId = inItemId, Qty = 3m } } },
            _userId, UserRole.Owner);
        Assert.True(inRet.Succeeded);
        await SetInWindowSaleReturnDate(new DateTime(2026, 6, 12));

        ScheduleXRegisterReport report = await _sut.GetScheduleXRegisterAsync(windowFrom, windowTo);

        ScheduleXBalanceRow row = Assert.Single(report.Balances);
        Assert.Equal(x.ProductId, row.ProductId);
        Assert.Equal(65m, row.Opening);            // pre-window net
        Assert.Equal(42m, row.Received);           // +50 −8
        Assert.Equal(17m, row.Issued);             // 20 − 3
        Assert.Equal(90m, row.Closing);            // 65 + 42 − 17
    }

    [Fact]
    public async Task DispenseDetail_ComesFromStrictRegister_InRangeOnly()
    {
        var x = AddProduct("Morphine", DrugSchedule.X);
        AddBatch(x.ProductId, "MX1", qty: 1000m, salePrice: 40m, expiry: DateTime.UtcNow.Date.AddYears(2));

        // One in-window sale, one out-of-window sale.
        var inSale = await _billing.CreateSaleAsync(XSale(x.ProductId, 2m), UserRole.Owner, _userId);
        var outSale = await _billing.CreateSaleAsync(XSale(x.ProductId, 4m), UserRole.Owner, _userId);
        Assert.True(inSale.Succeeded && outSale.Succeeded);

        // Pin dispense timestamps: one in June, one in May (both dispense rows exist).
        var db = _fixture.NewContext();
        var dispenses = await db.ScheduleXDispenses.OrderBy(d => d.ScheduleXDispenseId).ToListAsync();
        dispenses[0].DispensedAt = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        dispenses[1].DispensedAt = new DateTime(2026, 5, 15, 10, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        ScheduleXRegisterReport report = await _sut.GetScheduleXRegisterAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 30));

        ScheduleXDispenseRow d = Assert.Single(report.Dispenses); // only the June dispense
        Assert.Equal("Morphine", d.ProductName);
        Assert.Equal(2m, d.Qty);
        Assert.Equal("Anil Kumar", d.PatientName);
        Assert.Equal("12 MG Road, Kolkata", d.PatientAddress);
        Assert.Equal("Dr. Sen", d.PrescriberName);
        Assert.Equal("WBMC-12345", d.PrescriberRegNo);
        Assert.Equal("RX-X-777", d.PrescriptionNumber);
        Assert.True(d.PrescriptionRetained);
    }

    [Fact]
    public async Task NonScheduleXProduct_ExcludedFromBalances()
    {
        var x = AddProduct("Morphine", DrugSchedule.X);
        var h1 = AddProduct("Azithromycin", DrugSchedule.H1);
        AddBatch(x.ProductId, "MX1", qty: 1000m, salePrice: 40m, expiry: DateTime.UtcNow.Date.AddYears(2));
        AddPurchase(h1.ProductId, "AZ1", 100m, new DateTime(2026, 6, 5)); // H1 purchase in-window

        ScheduleXRegisterReport report = await _sut.GetScheduleXRegisterAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 30));

        // Only the Schedule-X product appears in the balances — the H1 drug never does.
        ScheduleXBalanceRow row = Assert.Single(report.Balances);
        Assert.Equal("Morphine", row.ProductName);
        Assert.DoesNotContain(report.Balances, r => r.ProductName == "Azithromycin");
    }

    // --- helpers that need to run in a fresh context after seeding sale returns ---

    private async Task SetSaleReturnDate(DateTime date)
    {
        var db = _fixture.NewContext();
        // Only the single existing return row at this point (the pre-window one).
        SaleReturn r = await db.SaleReturns.OrderBy(x => x.ReturnId).FirstAsync();
        r.Date = date;
        await db.SaveChangesAsync();
    }

    private async Task SetInWindowSaleReturnDate(DateTime date)
    {
        var db = _fixture.NewContext();
        SaleReturn r = await db.SaleReturns.OrderByDescending(x => x.ReturnId).FirstAsync();
        r.Date = date;
        await db.SaveChangesAsync();
    }

    private static SaleInput XSale(int productId, decimal qty) => new()
    {
        PaymentMode = PaymentMode.Cash,
        ScheduleX = FullXCapture(),
        Lines = { new SaleLineInput { ProductId = productId, Qty = qty } },
    };
}
