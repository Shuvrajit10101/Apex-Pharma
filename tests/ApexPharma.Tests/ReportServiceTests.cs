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
    private readonly SaleReturnService _returns;
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
        _returns = new SaleReturnService(_fixture.Context, auth);
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

    // ---------------- GSTR-1 / GST return ----------------

    /// <summary>Forces a sale's BillDate to a specific UTC instant (month-window tests).</summary>
    private async Task SetBillDateExact(string billNo, DateTime dateUtc)
    {
        var db = _fixture.NewContext();
        Sale sale = await db.Sales.SingleAsync(s => s.BillNo == billNo);
        sale.BillDate = dateUtc;
        await db.SaveChangesAsync();
    }

    /// <summary>Forces a sale's stored BillNo (docs-issued ordering tests).</summary>
    private async Task SetBillNo(string currentBillNo, string newBillNo)
    {
        var db = _fixture.NewContext();
        Sale sale = await db.Sales.SingleAsync(s => s.BillNo == currentBillNo);
        sale.BillNo = newBillNo;
        await db.SaveChangesAsync();
    }

    /// <summary>The first SaleItemId of a bill (for building a return request).</summary>
    private async Task<int> FirstSaleItemId(string billNo)
    {
        var db = _fixture.NewContext();
        return await db.SaleItems
            .Where(i => i.Sale!.BillNo == billNo)
            .OrderBy(i => i.SaleItemId)
            .Select(i => i.SaleItemId)
            .FirstAsync();
    }

    /// <summary>Forces every return row's Date to a specific UTC instant (month-window tests).</summary>
    private async Task SetReturnDates(DateTime dateUtc)
    {
        var db = _fixture.NewContext();
        List<SaleReturn> rows = await db.SaleReturns.ToListAsync();
        foreach (SaleReturn r in rows)
        {
            r.Date = dateUtc;
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Gstr1_B2cs_GroupsByRate_WithCorrectSplits()
    {
        // Three rates → three B2CS rows.
        var p5 = AddProduct("R5", gstRate: 5m, hsn: "3003");
        var p12 = AddProduct("R12", gstRate: 12m, hsn: "3004");
        var p18 = AddProduct("R18", gstRate: 18m, hsn: "3005");
        AddBatch(p5.ProductId, "B5", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(1));
        AddBatch(p12.ProductId, "B12", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(1));
        AddBatch(p18.ProductId, "B18", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(1));

        // Cash AND credit both count.
        int cust = AddCustomer("Khata A");
        var r = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Cash, new[] { Line(p5.ProductId, 2m), Line(p12.ProductId, 3m) }), UserRole.Owner, _userId);
        var r2 = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Credit, new[] { Line(p18.ProductId, 4m) }, customerId: cust), UserRole.Owner, _userId);
        Assert.True(r.Succeeded && r2.Succeeded);

        DateTime now = DateTime.UtcNow;
        Gstr1Report g = await _sut.GetGstr1Async(now.Year, now.Month, "West Bengal");

        Assert.Equal(3, g.B2cs.Count);
        Assert.All(g.B2cs, row => Assert.Equal("West Bengal", row.PlaceOfSupply));

        Gstr1B2csRow b5 = g.B2cs.Single(x => x.GstRate == 5m);   // 2×100 = 200 taxable, 5% → 5/5
        Assert.Equal(200m, b5.Taxable);
        Assert.Equal(5m, b5.Cgst);
        Assert.Equal(5m, b5.Sgst);

        Gstr1B2csRow b12 = g.B2cs.Single(x => x.GstRate == 12m); // 3×100 = 300 taxable, 12% → 18/18
        Assert.Equal(300m, b12.Taxable);
        Assert.Equal(18m, b12.Cgst);
        Assert.Equal(18m, b12.Sgst);

        Gstr1B2csRow b18 = g.B2cs.Single(x => x.GstRate == 18m); // 4×100 = 400 taxable, 18% → 36/36
        Assert.Equal(400m, b18.Taxable);
        Assert.Equal(36m, b18.Cgst);
        Assert.Equal(36m, b18.Sgst);
    }

    [Fact]
    public async Task Gstr1_Totals_ReconcileToHsnSummaryAndSalesReport()
    {
        var a = AddProduct("A", gstRate: 12m, hsn: "3004");
        var c = AddProduct("C", gstRate: 5m, hsn: "3003");
        AddBatch(a.ProductId, "A1", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(1));
        AddBatch(c.ProductId, "C1", 100m, 50m, 30m, DateTime.UtcNow.Date.AddYears(1));

        int cust = AddCustomer("Khata B");
        // A cash and a credit sale — both must be included.
        var r1 = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Cash, new[] { Line(a.ProductId, 2m), Line(c.ProductId, 4m) }), UserRole.Owner, _userId);
        var r2 = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Credit, new[] { Line(a.ProductId, 1m) }, customerId: cust), UserRole.Owner, _userId);
        Assert.True(r1.Succeeded && r2.Succeeded);

        DateTime now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        DateTime monthEnd = monthStart.AddMonths(1).AddDays(-1);

        Gstr1Report g = await _sut.GetGstr1Async(now.Year, now.Month, "WB");
        HsnSummaryReport hsn = await _sut.GetHsnSummaryAsync(monthStart, monthEnd);
        SalesReport sales = await _sut.GetSalesReportAsync(monthStart, monthEnd);

        // GSTR-1 gross == HSN summary totals == sales-report gross (cash + credit; no double-count).
        Assert.Equal(hsn.Totals.Taxable, g.Totals.Taxable);
        Assert.Equal(hsn.Totals.Cgst, g.Totals.Cgst);
        Assert.Equal(hsn.Totals.Sgst, g.Totals.Sgst);
        Assert.Equal(sales.Summary.Net, g.Totals.Taxable);
        Assert.Equal(sales.Rows.Sum(x => x.Cgst), g.Totals.Cgst);
        Assert.Equal(sales.Rows.Sum(x => x.Sgst), g.Totals.Sgst);
        Assert.Equal(2, g.Totals.BillCount);

        // B2CS totals == HSN totals (two views of the same outward supply).
        Assert.Equal(g.Hsn.Sum(x => x.Taxable), g.B2cs.Sum(x => x.Taxable));
        Assert.Equal(g.Hsn.Sum(x => x.Cgst), g.B2cs.Sum(x => x.Cgst));
        Assert.Equal(g.Hsn.Sum(x => x.Sgst), g.B2cs.Sum(x => x.Sgst));

        // HSN foots to the report totals.
        Assert.Equal(g.Totals.Taxable, g.Hsn.Sum(x => x.Taxable));
        Assert.Equal(g.Totals.Cgst, g.Hsn.Sum(x => x.Cgst));
        Assert.Equal(g.Totals.Sgst, g.Hsn.Sum(x => x.Sgst));
    }

    [Fact]
    public async Task Gstr1_MultiRateBill_SplitsIntoBothRateBuckets()
    {
        var p5 = AddProduct("R5", gstRate: 5m, hsn: "3003");
        var p18 = AddProduct("R18", gstRate: 18m, hsn: "3005");
        AddBatch(p5.ProductId, "B5", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(1));
        AddBatch(p18.ProductId, "B18", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(1));

        // A SINGLE bill with two rates.
        var r = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Cash, new[] { Line(p5.ProductId, 1m), Line(p18.ProductId, 1m) }), UserRole.Owner, _userId);
        Assert.True(r.Succeeded);

        DateTime now = DateTime.UtcNow;
        Gstr1Report g = await _sut.GetGstr1Async(now.Year, now.Month, "WB");

        Assert.Equal(2, g.B2cs.Count);
        Assert.Contains(g.B2cs, x => x.GstRate == 5m && x.Taxable == 100m);
        Assert.Contains(g.B2cs, x => x.GstRate == 18m && x.Taxable == 100m);
        Assert.Equal(1, g.Totals.BillCount);
    }

    [Fact]
    public async Task Gstr1_Cgst_Sgst_ReadFromStoredLineFigures_NotReDerived()
    {
        var p = AddProduct("P", gstRate: 12m, hsn: "3004");
        AddBatch(p.ProductId, "B1", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(1));

        var r = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 7m) }), UserRole.Owner, _userId);
        Assert.True(r.Succeeded);

        DateTime now = DateTime.UtcNow;
        Gstr1Report g = await _sut.GetGstr1Async(now.Year, now.Month, "WB");

        // B2CS/HSN CGST+SGST == Σ stored SaleItem.Cgst/Sgst.
        var db = _fixture.NewContext();
        decimal storedCgst = await db.SaleItems.SumAsync(i => i.Cgst);
        decimal storedSgst = await db.SaleItems.SumAsync(i => i.Sgst);
        Assert.Equal(storedCgst, g.B2cs.Sum(x => x.Cgst));
        Assert.Equal(storedSgst, g.B2cs.Sum(x => x.Sgst));
        Assert.Equal(storedCgst, g.Hsn.Sum(x => x.Cgst));
        Assert.Equal(storedSgst, g.Hsn.Sum(x => x.Sgst));
    }

    [Fact]
    public async Task Gstr1_Cgst_Sgst_UseStoredPerLineSum_NotAggregateReDerivation()
    {
        // Make the "read stored, not re-derived" invariant FALSIFIABLE: seed a case where the
        // stored Σ(per-line CGST) differs from a naive Round(aggregateTaxable × rate / 200) by a
        // paise, because each per-line GST half sits on a .5-paise boundary that rounds UP per line
        // but rounds cleanly in the aggregate.
        //
        // Two products at 5%, SAME HSN "3003" → one B2CS row and one HSN row (group taxable 50).
        // Each line: rate 30, qty 1, line discount 5 → NET taxable 25. Per-line GST half =
        // Round(25 × 5 / 200, 2, AwayFromZero) = Round(0.625, 2) = 0.63. Stored Σ = 0.63 + 0.63 =
        // 1.26. Naive aggregate re-derive = Round(50 × 5 / 200, 2) = Round(1.25, 2) = 1.25.
        // So a re-derivation regression would report 1.25 and this test would fail — as intended.
        var p1 = AddProduct("D1", gstRate: 5m, hsn: "3003");
        var p2 = AddProduct("D2", gstRate: 5m, hsn: "3003");
        AddBatch(p1.ProductId, "D1B", 100m, 30m, 20m, DateTime.UtcNow.Date.AddYears(1));
        AddBatch(p2.ProductId, "D2B", 100m, 30m, 20m, DateTime.UtcNow.Date.AddYears(1));

        // One bill, two lines, each with a 5 line discount → each line net taxable = 25.
        var r = await _billing.CreateSaleAsync(
            Sale(PaymentMode.Cash, new[] { Line(p1.ProductId, 1m, lineDiscount: 5m), Line(p2.ProductId, 1m, lineDiscount: 5m) }),
            UserRole.Owner, _userId);
        Assert.True(r.Succeeded);
        string billNo = r.Value!.BillNo;

        DateTime now = DateTime.UtcNow;
        Gstr1Report g = await _sut.GetGstr1Async(now.Year, now.Month, "WB");

        // Ground truth #1: the stored per-line CGST/SGST sum at 5% (what was actually billed).
        var db = _fixture.NewContext();
        decimal storedCgst = await db.SaleItems.Where(i => i.GstRate == 5m).SumAsync(i => i.Cgst);
        decimal storedSgst = await db.SaleItems.Where(i => i.GstRate == 5m).SumAsync(i => i.Sgst);
        decimal groupTaxable = await db.SaleItems.Where(i => i.GstRate == 5m).SumAsync(i => (i.Rate * i.Qty) - i.Discount);

        // Ground truth #2: the naive aggregate re-derivation a regression would use.
        decimal aggregateReDerive = Math.Round(groupTaxable * 5m / 200m, 2, MidpointRounding.AwayFromZero);

        // The seed genuinely diverges: 1.26 (stored sum) vs 1.25 (aggregate). If it didn't, this
        // test couldn't tell a correct impl from a re-deriving one.
        Assert.Equal(50m, groupTaxable);
        Assert.Equal(1.26m, storedCgst);
        Assert.Equal(1.25m, aggregateReDerive);
        Assert.NotEqual(aggregateReDerive, storedCgst);

        // (a) B2CS and HSN at 5% equal the STORED per-line sum ...
        Gstr1B2csRow b5 = g.B2cs.Single(x => x.GstRate == 5m);
        Gstr1HsnRow h5 = g.Hsn.Single(x => x.HsnCode == "3003" && x.GstRate == 5m);
        Assert.Equal(storedCgst, b5.Cgst);
        Assert.Equal(storedSgst, b5.Sgst);
        Assert.Equal(storedCgst, h5.Cgst);
        Assert.Equal(storedSgst, h5.Sgst);

        // (b) ... and NOT the aggregate re-derivation (so a re-derive regression fails here).
        Assert.NotEqual(aggregateReDerive, b5.Cgst);
        Assert.NotEqual(aggregateReDerive, h5.Cgst);

        // (c) Totals reconcile to the sales-report gross for that bill (cash sale, single bill).
        var monthStart = new DateTime(now.Year, now.Month, 1);
        DateTime monthEnd = monthStart.AddMonths(1).AddDays(-1);
        SalesReport sales = await _sut.GetSalesReportAsync(monthStart, monthEnd);
        SalesReportRow saleRow = sales.Rows.Single(x => x.BillNo == billNo);
        Assert.Equal(saleRow.Subtotal, g.Totals.Taxable);
        Assert.Equal(saleRow.Cgst, g.Totals.Cgst);
        Assert.Equal(saleRow.Sgst, g.Totals.Sgst);
    }

    [Fact]
    public async Task Gstr1_HsnRow_CarriesUqcAndTotalQty()
    {
        var p = AddProduct("P", gstRate: 12m, hsn: "3004");
        // Give the product a unit so UQC is not the "OTH" fallback.
        var db0 = _fixture.NewContext();
        Product prod = await db0.Products.SingleAsync(x => x.ProductId == p.ProductId);
        prod.Unit = "NOS";
        await db0.SaveChangesAsync();

        AddBatch(p.ProductId, "B1", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(1));
        var r = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 6m) }), UserRole.Owner, _userId);
        Assert.True(r.Succeeded);

        DateTime now = DateTime.UtcNow;
        Gstr1Report g = await _sut.GetGstr1Async(now.Year, now.Month, "WB");

        Gstr1HsnRow row = g.Hsn.Single(x => x.HsnCode == "3004" && x.GstRate == 12m);
        Assert.Equal("NOS", row.Uqc);
        Assert.Equal(6m, row.TotalQty);
    }

    [Fact]
    public async Task Gstr1_CreditNotes_AreSeparateAndDoNotChangeGross()
    {
        var p = AddProduct("P", gstRate: 12m, hsn: "3004");
        AddBatch(p.ProductId, "B1", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(1));

        var r = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 10m) }), UserRole.Owner, _userId);
        Assert.True(r.Succeeded);
        string billNo = r.Value!.BillNo;

        // Gross BEFORE any return.
        DateTime now = DateTime.UtcNow;
        Gstr1Report before = await _sut.GetGstr1Async(now.Year, now.Month, "WB");

        // Return 4 of the 10 sold. 10×100 = 1000 taxable, 12% → 120 GST (60/60). Returning 4/10
        // reverses 400 taxable, 24/24 CGST/SGST.
        int saleItemId = await FirstSaleItemId(billNo);
        var ret = await _returns.ProcessSaleReturnAsync(
            new SaleReturnInput { BillNo = billNo, Lines = { new SaleReturnLineInput { SaleItemId = saleItemId, Qty = 4m } } },
            _userId, UserRole.Owner);
        Assert.True(ret.Succeeded);

        Gstr1Report after = await _sut.GetGstr1Async(now.Year, now.Month, "WB");

        // The credit-notes section captures the return, grouped by rate.
        Gstr1CreditNoteRow cn = Assert.Single(after.CreditNotes);
        Assert.Equal(12m, cn.GstRate);
        Assert.Equal(400m, cn.Taxable);
        Assert.Equal(24m, cn.Cgst);
        Assert.Equal(24m, cn.Sgst);

        // The outward gross (B2CS/HSN/Totals) is UNCHANGED by the return — kept separate.
        Assert.Empty(before.CreditNotes);
        Assert.Equal(before.Totals.Taxable, after.Totals.Taxable);
        Assert.Equal(before.Totals.Cgst, after.Totals.Cgst);
        Assert.Equal(before.Totals.Sgst, after.Totals.Sgst);
        Assert.Equal(1000m, after.Totals.Taxable);
    }

    [Fact]
    public async Task Gstr1_EmptyMonth_IsValidAndEmpty()
    {
        // No sales at all in a far-past month.
        Gstr1Report g = await _sut.GetGstr1Async(2000, 1, "WB");

        Assert.Empty(g.B2cs);
        Assert.Empty(g.Hsn);
        Assert.Empty(g.CreditNotes);
        Assert.Equal(0, g.Docs.Count);
        Assert.Equal(string.Empty, g.Docs.FromBillNo);
        Assert.Equal(string.Empty, g.Docs.ToBillNo);
        Assert.Equal(0m, g.Totals.Taxable);
        Assert.Equal(0, g.Totals.BillCount);
    }

    [Fact]
    public async Task Gstr1_DocsIssued_FirstLastBillAndCount()
    {
        var p = AddProduct("P", gstRate: 12m, hsn: "3004");
        AddBatch(p.ProductId, "B1", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(2));

        var r1 = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 1m) }), UserRole.Owner, _userId);
        var r2 = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 1m) }), UserRole.Owner, _userId);
        var r3 = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 1m) }), UserRole.Owner, _userId);
        Assert.True(r1.Succeeded && r2.Succeeded && r3.Succeeded);

        // Force the STORED BillNos out of insertion order so the docs-issued Ordinal sort is
        // actually exercised (a no-sort / wrong-sort implementation would report the wrong
        // first/last here). Insertion order becomes INV-000003, INV-000001, INV-000002.
        // Stage through temporary names first so the UNIQUE index on BillNo never transiently
        // collides with a value still held by another row mid-rename.
        await SetBillNo(r1.Value!.BillNo, "TMP-1");
        await SetBillNo(r2.Value!.BillNo, "TMP-2");
        await SetBillNo(r3.Value!.BillNo, "TMP-3");
        await SetBillNo("TMP-1", "INV-000003");
        await SetBillNo("TMP-2", "INV-000001");
        await SetBillNo("TMP-3", "INV-000002");

        DateTime now = DateTime.UtcNow;
        Gstr1Report g = await _sut.GetGstr1Async(now.Year, now.Month, "WB");

        Assert.Equal(3, g.Docs.Count);
        Assert.Equal("INV-000001", g.Docs.FromBillNo); // Ordinal-min, not first-inserted
        Assert.Equal("INV-000003", g.Docs.ToBillNo);   // Ordinal-max, not last-inserted
        Assert.Equal(0, g.Docs.Cancelled);
    }

    [Fact]
    public async Task Gstr1_MonthBoundary_IncludesEdges_ExcludesAdjacentMonths()
    {
        var p = AddProduct("P", gstRate: 12m, hsn: "3004");
        AddBatch(p.ProductId, "B1", 1000m, 100m, 60m, DateTime.UtcNow.Date.AddYears(3));

        // Four sales — we'll pin their BillDate to explicit UTC instants around June 2026.
        var s1 = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 1m) }), UserRole.Owner, _userId);
        var s2 = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 1m) }), UserRole.Owner, _userId);
        var s3 = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 1m) }), UserRole.Owner, _userId);
        var s4 = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 1m) }), UserRole.Owner, _userId);
        Assert.True(s1.Succeeded && s2.Succeeded && s3.Succeeded && s4.Succeeded);

        // In-month edges: 1st 00:00 UTC and last-day 23:59 UTC.
        await SetBillDateExact(s1.Value!.BillNo, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await SetBillDateExact(s2.Value!.BillNo, new DateTime(2026, 6, 30, 23, 59, 0, DateTimeKind.Utc));
        // Out-of-month: prior-month last instant and next-month first instant.
        await SetBillDateExact(s3.Value!.BillNo, new DateTime(2026, 5, 31, 23, 59, 0, DateTimeKind.Utc));
        await SetBillDateExact(s4.Value!.BillNo, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        Gstr1Report g = await _sut.GetGstr1Async(2026, 6, "WB");

        // Exactly the two in-month bills.
        Assert.Equal(2, g.Totals.BillCount);
        Assert.Equal(2, g.Docs.Count);
        Assert.Equal(200m, g.Totals.Taxable); // 2 × (1×100)
    }

    [Fact]
    public async Task Gstr1_CreditNote_MonthBoundary_UsesReturnDate()
    {
        var p = AddProduct("P", gstRate: 12m, hsn: "3004");
        AddBatch(p.ProductId, "B1", 100m, 100m, 60m, DateTime.UtcNow.Date.AddYears(3));

        var r = await _billing.CreateSaleAsync(Sale(PaymentMode.Cash, new[] { Line(p.ProductId, 5m) }), UserRole.Owner, _userId);
        Assert.True(r.Succeeded);
        string billNo = r.Value!.BillNo;
        await SetBillDateExact(billNo, new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc));

        int saleItemId = await FirstSaleItemId(billNo);
        var ret = await _returns.ProcessSaleReturnAsync(
            new SaleReturnInput { BillNo = billNo, Lines = { new SaleReturnLineInput { SaleItemId = saleItemId, Qty = 2m } } },
            _userId, UserRole.Owner);
        Assert.True(ret.Succeeded);

        // Pin the return into JULY — it must appear in July's credit notes, not June's.
        await SetReturnDates(new DateTime(2026, 7, 5, 9, 0, 0, DateTimeKind.Utc));

        Gstr1Report june = await _sut.GetGstr1Async(2026, 6, "WB");
        Gstr1Report july = await _sut.GetGstr1Async(2026, 7, "WB");

        Assert.Empty(june.CreditNotes);            // return is not in June
        Gstr1CreditNoteRow cn = Assert.Single(july.CreditNotes);
        Assert.Equal(12m, cn.GstRate);
        Assert.Equal(200m, cn.Taxable);            // 2×100 returned

        // July has NO sales — only the return. Prove the credit note doesn't leak into the
        // outward (gross) sections: B2CS/HSN empty and the gross totals are zero.
        Assert.Empty(july.B2cs);
        Assert.Empty(july.Hsn);
        Assert.Equal(0m, july.Totals.Taxable);
        Assert.Equal(0, july.Totals.BillCount);
    }
}
