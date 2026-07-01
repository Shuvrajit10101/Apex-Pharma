using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Reporting;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// ReportService tests (plan.md §11, §14). Data is seeded by running the REAL
/// <see cref="BillingService"/> so every figure (GST split, discount apportionment, profit
/// base) is authoritative, then the reports are asserted:
/// <list type="bullet">
///   <item>sales totals + per-bill <b>profit</b> (sale net ex-GST − purchase cost) are correct;</item>
///   <item>the date-range filter includes/excludes the right bills;</item>
///   <item>the Schedule-H register contains EXACTLY the scheduled-drug lines with doctor/Rx and excludes non-scheduled;</item>
///   <item>the GST/HSN summary aggregates taxable/CGST/SGST by HSN+rate and foots to the sales totals;</item>
///   <item>low-stock and near-expiry/expired return the right sets.</item>
/// </list>
/// </summary>
public class ReportServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly BillingService _billing;
    private readonly InventoryService _inventory;
    private readonly ReportService _sut;

    private int _userId;
    private int _supplierId;
    private int _catId;
    private int _manId;

    public ReportServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        var gst = new GstService();
        _billing = new BillingService(_fixture.Context, auth, gst);
        _inventory = new InventoryService(_fixture.Context);
        _sut = new ReportService(_fixture.Context, _inventory);
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

    private Product AddProduct(string name, decimal gstRate = 12m, DrugSchedule schedule = DrugSchedule.None,
        string? hsn = "3004", int reorderLevel = 0)
    {
        var db = _fixture.Context;
        var p = new Product
        {
            Name = name,
            CategoryId = _catId,
            ManufacturerId = _manId,
            GstRate = gstRate,
            Schedule = schedule,
            HsnCode = hsn,
            IsActive = true,
            ReorderLevel = reorderLevel,
        };
        db.Products.Add(p);
        db.SaveChanges();
        return p;
    }

    /// <summary>Adds a batch with an explicit purchase price and sale price so profit is testable.</summary>
    private Batch AddBatch(int productId, string batchNo, decimal qty, decimal salePrice, decimal purchasePrice, DateTime expiry)
    {
        var db = _fixture.Context;
        var b = new Batch
        {
            ProductId = productId,
            BatchNo = batchNo,
            ExpiryDate = expiry,
            Mrp = salePrice,
            PurchasePrice = purchasePrice,
            SalePrice = salePrice,
            QtyOnHand = qty,
            SupplierId = _supplierId,
            ReceivedDate = DateTime.UtcNow,
        };
        db.Batches.Add(b);
        db.SaveChanges();
        return b;
    }

    private int AddCustomer(string name, string? phone = null)
    {
        var db = _fixture.Context;
        var c = new Customer { Name = name, Phone = phone };
        db.Customers.Add(c);
        db.SaveChanges();
        return c.CustomerId;
    }

    private static SaleInput Sale(PaymentMode mode, IEnumerable<SaleLineInput> lines, int? customerId = null,
        string? doctor = null, string? rx = null, decimal billDiscount = 0m)
        => new()
        {
            PaymentMode = mode,
            CustomerId = customerId,
            DoctorName = doctor,
            PrescriptionRef = rx,
            BillDiscount = billDiscount,
            Lines = lines.ToList(),
        };

    private static SaleLineInput Line(int productId, decimal qty, decimal lineDiscount = 0m)
        => new() { ProductId = productId, Qty = qty, LineDiscount = lineDiscount };

    /// <summary>Backdates a sale's BillDate so the date-range filter can be exercised.</summary>
    private async Task SetBillDate(string billNo, DateTime date)
    {
        var db = _fixture.NewContext();
        Sale sale = await db.Sales.SingleAsync(s => s.BillNo == billNo);
        sale.BillDate = date;
        await db.SaveChangesAsync();
    }

    // ---------------- Sales report + profit ----------------

    [Fact]
    public async Task SalesReport_ComputesTotalsAndProfit()
    {
        // Product at 12% GST, sold at 100, bought at 60 → margin 40/unit. Sell 5 → profit 200.
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 100m, purchasePrice: 60m, expiry: DateTime.UtcNow.Date.AddYears(2));

        var r = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 5m) }), UserRole.Owner, _userId);
        Assert.True(r.Succeeded);

        SalesReport report = await _sut.GetSalesReportAsync(DateTime.Today.AddDays(-1), DateTime.Today);

        Assert.Single(report.Rows);
        SalesReportRow row = report.Rows[0];

        // Net taxable ex-GST = 5 × 100 = 500; profit = 500 − (5 × 60) = 200.
        Assert.Equal(500m, row.Subtotal);
        Assert.Equal(200m, row.Profit);
        Assert.Equal(30m, row.Cgst);  // 12% of 500 = 60 GST → 30 CGST + 30 SGST
        Assert.Equal(30m, row.Sgst);

        // Summary foots to the single bill.
        Assert.Equal(1, report.Summary.BillCount);
        Assert.Equal(500m, report.Summary.Net);
        Assert.Equal(60m, report.Summary.TotalGst);
        Assert.Equal(200m, report.Summary.TotalProfit);
        Assert.Equal(row.Total, report.Summary.Gross);
    }

    [Fact]
    public async Task SalesReport_ProfitAccountsForBillDiscount()
    {
        // Bill discount reduces net taxable → reduces profit by the discount (cost unchanged).
        var p = AddProduct("Amox", gstRate: 5m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 100m, purchasePrice: 70m, expiry: DateTime.UtcNow.Date.AddYears(2));

        // Sell 10 at 100 = 1000 gross; 100 bill discount → net taxable 900; cost = 700 → profit 200.
        var r = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 10m) }, billDiscount: 100m), UserRole.Owner, _userId);
        Assert.True(r.Succeeded);

        SalesReport report = await _sut.GetSalesReportAsync(DateTime.Today.AddDays(-1), DateTime.Today);
        SalesReportRow row = report.Rows[0];

        Assert.Equal(900m, row.Subtotal);
        Assert.Equal(100m, row.Discount);
        Assert.Equal(200m, row.Profit);
    }

    [Fact]
    public async Task SalesReport_DateRangeFilter_ExcludesOutOfRange()
    {
        var p = AddProduct("Para", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 50m, purchasePrice: 30m, expiry: DateTime.UtcNow.Date.AddYears(2));

        var inRange = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 2m) }), UserRole.Owner, _userId);
        var outOfRange = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 3m) }), UserRole.Owner, _userId);
        Assert.True(inRange.Succeeded && outOfRange.Succeeded);

        // Push the second sale 60 days into the past — outside a "last 7 days" window.
        await SetBillDate(outOfRange.Value!.BillNo, DateTime.Today.AddDays(-60));

        SalesReport report = await _sut.GetSalesReportAsync(DateTime.Today.AddDays(-7), DateTime.Today);

        Assert.Single(report.Rows);
        Assert.Equal(inRange.Value!.BillNo, report.Rows[0].BillNo);
    }

    // ---------------- Schedule H/H1/X register ----------------

    [Fact]
    public async Task ScheduleRegister_ContainsOnlyScheduledLines_WithDoctorAndRx()
    {
        var scheduledH1 = AddProduct("Azithromycin", gstRate: 5m, schedule: DrugSchedule.H1, hsn: "3004");
        var scheduledX = AddProduct("Alprazolam", gstRate: 12m, schedule: DrugSchedule.X, hsn: "3003");
        var nonScheduled = AddProduct("Vitamin C", gstRate: 12m, schedule: DrugSchedule.None, hsn: "2106");

        AddBatch(scheduledH1.ProductId, "AZ1", 100m, 50m, 30m, DateTime.UtcNow.Date.AddYears(1));
        AddBatch(scheduledX.ProductId, "AL1", 100m, 80m, 50m, DateTime.UtcNow.Date.AddYears(1));
        AddBatch(nonScheduled.ProductId, "VC1", 100m, 20m, 10m, DateTime.UtcNow.Date.AddYears(1));

        int customerId = AddCustomer("Patient A", "9876543210");

        // One sale mixing a scheduled + non-scheduled line, with doctor + Rx.
        var r1 = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Cash, new[] { Line(scheduledH1.ProductId, 2m), Line(nonScheduled.ProductId, 3m) },
                 customerId: customerId, doctor: "Dr. Rao", rx: "RX-100"),
            UserRole.Owner, _userId);
        Assert.True(r1.Succeeded);

        // A second sale with a Schedule X line.
        var r2 = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Cash, new[] { Line(scheduledX.ProductId, 1m) }, doctor: "Dr. Sen", rx: "RX-200"),
            UserRole.Owner, _userId);
        Assert.True(r2.Succeeded);

        // A purely non-scheduled sale → must NOT appear on the register.
        var r3 = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Cash, new[] { Line(nonScheduled.ProductId, 4m) }), UserRole.Owner, _userId);
        Assert.True(r3.Succeeded);

        IReadOnlyList<ScheduleRegisterRow> register =
            await _sut.GetScheduleRegisterAsync(DateTime.Today.AddDays(-1), DateTime.Today);

        // Exactly the two scheduled lines — the non-scheduled lines are excluded.
        Assert.Equal(2, register.Count);
        Assert.All(register, row => Assert.NotEqual(DrugSchedule.None, row.Schedule));
        Assert.Contains(register, row => row.ProductName == "Azithromycin" && row.Schedule == DrugSchedule.H1
            && row.DoctorName == "Dr. Rao" && row.PrescriptionRef == "RX-100"
            && row.PatientName == "Patient A" && row.PatientPhone == "9876543210" && row.Qty == 2m);
        Assert.Contains(register, row => row.ProductName == "Alprazolam" && row.Schedule == DrugSchedule.X
            && row.DoctorName == "Dr. Sen" && row.PrescriptionRef == "RX-200" && row.Qty == 1m);
        Assert.DoesNotContain(register, row => row.ProductName == "Vitamin C");
    }

    // ---------------- GST / HSN summary ----------------

    [Fact]
    public async Task HsnSummary_AggregatesByHsnAndRate_AndFootsToSalesTotals()
    {
        // Three products across two HSN+rate groups.
        var a = AddProduct("A", gstRate: 12m, hsn: "3004");
        var b = AddProduct("B", gstRate: 12m, hsn: "3004"); // same HSN + rate as A → grouped together
        var c = AddProduct("C", gstRate: 5m, hsn: "3003");  // different HSN + rate

        AddBatch(a.ProductId, "A1", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(1));
        AddBatch(b.ProductId, "B1", 100m, 200m, 120m, DateTime.UtcNow.Date.AddYears(1));
        AddBatch(c.ProductId, "C1", 100m, 50m, 30m, DateTime.UtcNow.Date.AddYears(1));

        var r1 = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Cash, new[] { Line(a.ProductId, 2m), Line(c.ProductId, 4m) }), UserRole.Owner, _userId);
        var r2 = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Cash, new[] { Line(b.ProductId, 1m) }), UserRole.Owner, _userId);
        Assert.True(r1.Succeeded && r2.Succeeded);

        HsnSummaryReport hsn = await _sut.GetHsnSummaryAsync(DateTime.Today.AddDays(-1), DateTime.Today);
        SalesReport sales = await _sut.GetSalesReportAsync(DateTime.Today.AddDays(-1), DateTime.Today);

        // Two groups: (3004, 12%) and (3003, 5%).
        Assert.Equal(2, hsn.Rows.Count);

        HsnSummaryRow g12 = hsn.Rows.Single(x => x.HsnCode == "3004" && x.GstRate == 12m);
        // A: 2×100 = 200 taxable; B: 1×200 = 200 taxable → 400 taxable, 12% = 48 GST → 24/24.
        Assert.Equal(400m, g12.Taxable);
        Assert.Equal(24m, g12.Cgst);
        Assert.Equal(24m, g12.Sgst);

        HsnSummaryRow g5 = hsn.Rows.Single(x => x.HsnCode == "3003" && x.GstRate == 5m);
        // C: 4×50 = 200 taxable, 5% = 10 GST → 5/5.
        Assert.Equal(200m, g5.Taxable);
        Assert.Equal(5m, g5.Cgst);
        Assert.Equal(5m, g5.Sgst);

        // The HSN summary foots to the sales totals (taxable == net; CGST/SGST == sales GST halves).
        Assert.Equal(sales.Summary.Net, hsn.Totals.Taxable);
        Assert.Equal(sales.Rows.Sum(x => x.Cgst), hsn.Totals.Cgst);
        Assert.Equal(sales.Rows.Sum(x => x.Sgst), hsn.Totals.Sgst);
        Assert.Equal(hsn.Totals.Taxable + hsn.Totals.Cgst + hsn.Totals.Sgst, hsn.Totals.Total);
    }

    // ---------------- Low-stock & expiry ----------------

    [Fact]
    public async Task LowStock_ReturnsProductsAtOrBelowReorderLevel()
    {
        var low = AddProduct("LowMed", reorderLevel: 20);
        var ok = AddProduct("OkMed", reorderLevel: 5);
        AddBatch(low.ProductId, "L1", qty: 10m, salePrice: 10m, purchasePrice: 5m, expiry: DateTime.UtcNow.Date.AddYears(1));
        AddBatch(ok.ProductId, "O1", qty: 50m, salePrice: 10m, purchasePrice: 5m, expiry: DateTime.UtcNow.Date.AddYears(1));

        IReadOnlyList<LowStockRow> rows = await _sut.GetLowStockReportAsync();

        Assert.Contains(rows, r => r.ProductName == "LowMed" && r.TotalOnHand == 10m && r.ReorderLevel == 20);
        Assert.DoesNotContain(rows, r => r.ProductName == "OkMed");
    }

    [Fact]
    public async Task Expiry_SeparatesExpiredAndNearExpiry()
    {
        var p = AddProduct("ExpMed");
        AddBatch(p.ProductId, "EXPIRED", qty: 5m, salePrice: 10m, purchasePrice: 5m, expiry: DateTime.UtcNow.Date.AddDays(-3));
        AddBatch(p.ProductId, "NEAR", qty: 5m, salePrice: 10m, purchasePrice: 5m, expiry: DateTime.UtcNow.Date.AddDays(30));
        AddBatch(p.ProductId, "FAR", qty: 5m, salePrice: 10m, purchasePrice: 5m, expiry: DateTime.UtcNow.Date.AddYears(2));

        IReadOnlyList<ExpiryRow> rows = await _sut.GetExpiryReportAsync(withinDays: 90);

        Assert.Contains(rows, r => r.BatchNo == "EXPIRED" && r.IsExpired);
        Assert.Contains(rows, r => r.BatchNo == "NEAR" && !r.IsExpired);
        Assert.DoesNotContain(rows, r => r.BatchNo == "FAR");
    }
}
