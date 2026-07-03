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
/// transaction atomicity (a mid-sale failure persists NOTHING), and RBAC — including the
/// owner-approved rule that only a pharmacist (Owner/Pharmacist) may dispense a Schedule-X drug
/// (a Cashier is refused even with a full capture), while H/H1 billing is unaffected.
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

    /// <summary>A fully-populated, valid Schedule-X capture (retained copy checked).</summary>
    private static ScheduleXCapture FullXCapture() => new()
    {
        PatientName = "Anil Kumar",
        PatientAddress = "12 MG Road, Kolkata",
        PatientPhone = "9876543210",
        PrescriberName = "Dr. Sen",
        PrescriberAddress = "Apollo Clinic, Salt Lake",
        PrescriberRegNo = "WBMC-12345",
        PrescriptionNumber = "RX-X-777",
        PrescriptionDate = DateTime.Today,
        PrescriptionRetained = true,
    };

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

    [Fact]
    public async Task ScheduleX_WithoutDoctorOrRx_IsRejected()
    {
        var p = AddProduct("Morphine", schedule: DrugSchedule.X);
        AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 1m)); // no doctor/Rx
        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.False(result.Succeeded);
        Assert.Contains("Schedule", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().Sales.CountAsync());
    }

    [Fact]
    public async Task ScheduleX_WithDoctorAndRx_Succeeds()
    {
        var p = AddProduct("Morphine", schedule: DrugSchedule.X);
        AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 1m));
        input.DoctorName = "Dr. Bose";
        input.PrescriptionRef = "RX-X-1";
        input.ScheduleX = FullXCapture(); // Schedule X now also needs the strict capture (Phase 2f)

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        var sale = await db.Sales.SingleAsync();
        Assert.Equal("Dr. Bose", sale.DoctorName);
        Assert.Equal("RX-X-1", sale.PrescriptionRef);
    }

    // ---- Schedule X strict dual-Rx capture (Phase 2f) ----

    [Fact]
    public async Task ScheduleX_WithoutCapture_IsRejected_PersistsNothing()
    {
        var p = AddProduct("Morphine", schedule: DrugSchedule.X);
        var b = AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        // No ScheduleX capture at all → rejected; no sale, no stock decrement, no dispense row.
        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 3m));
        input.ScheduleX = null;

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.False(result.Succeeded);
        Assert.Contains("Schedule X", result.Error!);
        var db = _fixture.NewContext();
        Assert.Equal(0, await db.Sales.CountAsync());
        Assert.Equal(0, await db.SaleItems.CountAsync());
        Assert.Equal(0, await db.ScheduleXDispenses.CountAsync());
        Assert.Equal(10m, (await db.Batches.SingleAsync(x => x.BatchId == b.BatchId)).QtyOnHand); // untouched
    }

    [Fact]
    public async Task ScheduleX_RetainedNotChecked_IsRejected()
    {
        var p = AddProduct("Morphine", schedule: DrugSchedule.X);
        AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 1m));
        ScheduleXCapture cap = FullXCapture();
        cap.PrescriptionRetained = false; // duplicate copy NOT retained → reject
        input.ScheduleX = cap;

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.False(result.Succeeded);
        Assert.Equal(0, await _fixture.NewContext().Sales.CountAsync());
        Assert.Equal(0, await _fixture.NewContext().ScheduleXDispenses.CountAsync());
    }

    /// <summary>
    /// Every independently-required Schedule-X field, blanked ONE at a time, must reject the whole
    /// sale and persist nothing — pinning each clause of the enforcement gate
    /// (<c>BillingService.CreateSaleAsync</c>) so no field can silently drop out of the legal
    /// register. Strings use whitespace/empty; the prescription date uses <c>default</c>.
    /// </summary>
    public static TheoryData<string, Action<ScheduleXCapture>> MissingXFieldMutators() => new()
    {
        { "PatientName (whitespace)",       cap => cap.PatientName = "   " },
        { "PatientAddress (whitespace)",    cap => cap.PatientAddress = "   " },
        { "PrescriberName (empty)",         cap => cap.PrescriberName = "" },
        { "PrescriberAddress (null)",       cap => cap.PrescriberAddress = null! },
        { "PrescriberRegNo (whitespace)",   cap => cap.PrescriberRegNo = "   " },
        { "PrescriptionNumber (empty)",     cap => cap.PrescriptionNumber = "" },
        { "PrescriptionDate (default)",     cap => cap.PrescriptionDate = default },
    };

    [Theory]
    [MemberData(nameof(MissingXFieldMutators))]
    public async Task ScheduleX_MissingRequiredField_IsRejected_PersistsNothing(
        string _, Action<ScheduleXCapture> blankOneField)
    {
        var p = AddProduct("Morphine", schedule: DrugSchedule.X);
        var b = AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 3m));
        ScheduleXCapture cap = FullXCapture();
        blankOneField(cap); // exactly ONE required field blanked → reject
        input.ScheduleX = cap;

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.False(result.Succeeded);
        var db = _fixture.NewContext();
        Assert.Equal(0, await db.Sales.CountAsync());
        Assert.Equal(0, await db.SaleItems.CountAsync());
        Assert.Equal(0, await db.ScheduleXDispenses.CountAsync());
        Assert.Equal(10m, (await db.Batches.SingleAsync(x => x.BatchId == b.BatchId)).QtyOnHand); // untouched
    }

    [Fact]
    public async Task ScheduleX_DispensedAt_EqualsSaleBillDate()
    {
        var x1 = AddProduct("Morphine", schedule: DrugSchedule.X);
        var x2 = AddProduct("Fentanyl", schedule: DrugSchedule.X);
        AddBatch(x1.ProductId, "M1", qty: 10m, salePrice: 40m, expiry: DateTime.UtcNow.Date.AddYears(1));
        AddBatch(x2.ProductId, "F1", qty: 10m, salePrice: 60m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(x1.ProductId, 1m), Line(x2.ProductId, 2m));
        input.ScheduleX = FullXCapture();

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        Sale sale = await db.Sales.SingleAsync();

        // Every X dispense row is stamped at the SAME instant as the sale so the Issued leg and the
        // dispense-detail row bucket into the same narcotic-register window (register reconciles).
        var dispenses = await db.ScheduleXDispenses.ToListAsync();
        Assert.Equal(2, dispenses.Count);
        Assert.All(dispenses, d => Assert.Equal(sale.BillDate, d.DispensedAt));
    }

    [Fact]
    public async Task ScheduleX_WithFullCapture_Succeeds_WritesOneDispensePerXLine()
    {
        var x = AddProduct("Morphine", schedule: DrugSchedule.X);
        var nonX = AddProduct("Paracetamol", schedule: DrugSchedule.None);
        var xBatch = AddBatch(x.ProductId, "MX1", qty: 10m, salePrice: 40m, expiry: DateTime.UtcNow.Date.AddYears(1));
        AddBatch(nonX.ProductId, "P1", qty: 10m, salePrice: 5m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(x.ProductId, 3m), Line(nonX.ProductId, 2m));
        input.ScheduleX = FullXCapture();

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();

        // Exactly ONE dispense row — for the X line only, not the non-scheduled line.
        ScheduleXDispense d = await db.ScheduleXDispenses.SingleAsync();
        Assert.Equal(x.ProductId, d.ProductId);
        Assert.Equal(xBatch.BatchId, d.BatchId);
        Assert.Equal(3m, d.Qty);
        Assert.Equal("Anil Kumar", d.PatientName);
        Assert.Equal("12 MG Road, Kolkata", d.PatientAddress);
        Assert.Equal("Dr. Sen", d.PrescriberName);
        Assert.Equal("WBMC-12345", d.PrescriberRegNo);
        Assert.Equal("RX-X-777", d.PrescriptionNumber);
        Assert.True(d.PrescriptionRetained);
        Assert.Equal(_userId, d.CreatedBy);

        // The dispense row links to the actual X SaleItem.
        SaleItem xItem = await db.SaleItems.SingleAsync(i => i.ProductId == x.ProductId);
        Assert.Equal(xItem.SaleItemId, d.SaleItemId);

        // The sale still records doctor/Rx (backfilled from the capture) for the combined register.
        Sale sale = await db.Sales.SingleAsync();
        Assert.Equal("Dr. Sen", sale.DoctorName);
        Assert.Equal("RX-X-777", sale.PrescriptionRef);
    }

    [Fact]
    public async Task ScheduleX_ExplicitDoctorRx_NotOverwrittenByCapture()
    {
        var x = AddProduct("Morphine", schedule: DrugSchedule.X);
        AddBatch(x.ProductId, "MX1", qty: 10m, salePrice: 40m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(x.ProductId, 1m));
        input.DoctorName = "Dr. Explicit";   // supplied on the header
        input.PrescriptionRef = "RX-EXPLICIT";
        input.ScheduleX = FullXCapture();

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        Sale sale = await _fixture.NewContext().Sales.SingleAsync();
        Assert.Equal("Dr. Explicit", sale.DoctorName);   // header value kept, not backfilled over
        Assert.Equal("RX-EXPLICIT", sale.PrescriptionRef);
    }

    [Fact]
    public async Task ScheduleX_MultipleXLines_WritesOneDispenseEach()
    {
        var x1 = AddProduct("Morphine", schedule: DrugSchedule.X);
        var x2 = AddProduct("Fentanyl", schedule: DrugSchedule.X);
        AddBatch(x1.ProductId, "M1", qty: 10m, salePrice: 40m, expiry: DateTime.UtcNow.Date.AddYears(1));
        AddBatch(x2.ProductId, "F1", qty: 10m, salePrice: 60m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(x1.ProductId, 1m), Line(x2.ProductId, 2m));
        input.ScheduleX = FullXCapture();

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        Assert.Equal(2, await db.ScheduleXDispenses.CountAsync());
        Assert.Contains(await db.ScheduleXDispenses.ToListAsync(), d => d.ProductId == x1.ProductId && d.Qty == 1m);
        Assert.Contains(await db.ScheduleXDispenses.ToListAsync(), d => d.ProductId == x2.ProductId && d.Qty == 2m);
    }

    [Fact]
    public async Task ScheduleX_AsPharmacist_WithFullCapture_Succeeds()
    {
        var x = AddProduct("Morphine", schedule: DrugSchedule.X);
        AddBatch(x.ProductId, "M1", qty: 10m, salePrice: 40m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(x.ProductId, 1m));
        input.ScheduleX = FullXCapture();

        // A Pharmacist has DispenseScheduleX → succeeds with the capture.
        var result = await _sut.CreateSaleAsync(input, UserRole.Pharmacist, _userId);

        Assert.True(result.Succeeded);
        Assert.Equal(1, await _fixture.NewContext().ScheduleXDispenses.CountAsync());
    }

    // ---- Schedule X RBAC: dispensing requires a pharmacist (owner-approved — plan.md §4) ----

    [Fact]
    public async Task ScheduleX_AsCashier_IsRefused_EvenWithFullCapture_PersistsNothing()
    {
        var x = AddProduct("Morphine", schedule: DrugSchedule.X);
        var b = AddBatch(x.ProductId, "M1", qty: 10m, salePrice: 40m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(x.ProductId, 3m));
        input.ScheduleX = FullXCapture(); // full, valid capture — but a Cashier still can't dispense X

        var result = await _sut.CreateSaleAsync(input, UserRole.Cashier, _userId);

        Assert.False(result.Succeeded);
        Assert.Contains("pharmacist", result.Error!);
        var db = _fixture.NewContext();
        Assert.Equal(0, await db.Sales.CountAsync());
        Assert.Equal(0, await db.SaleItems.CountAsync());
        Assert.Equal(0, await db.ScheduleXDispenses.CountAsync());
        Assert.Equal(10m, (await db.Batches.SingleAsync(z => z.BatchId == b.BatchId)).QtyOnHand); // untouched
    }

    [Fact]
    public async Task ScheduleX_AsOwner_WithFullCapture_Succeeds_WritesDispense()
    {
        var x = AddProduct("Morphine", schedule: DrugSchedule.X);
        AddBatch(x.ProductId, "M1", qty: 10m, salePrice: 40m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(x.ProductId, 2m));
        input.ScheduleX = FullXCapture();

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        Assert.Equal(1, await db.Sales.CountAsync());
        Assert.Equal(1, await db.ScheduleXDispenses.CountAsync());
    }

    [Fact]
    public async Task ScheduleH1_AsCashier_StillSucceeds_UnaffectedByXPermission()
    {
        // The X pharmacist gate must NOT touch H/H1: a Cashier can still bill an H1 line
        // with only doctor + Rx (H/H1 need DoBilling, not DispenseScheduleX).
        var p = AddProduct("Azithromycin", schedule: DrugSchedule.H1);
        AddBatch(p.ProductId, "AZ1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 2m));
        input.DoctorName = "Dr. Rao";
        input.PrescriptionRef = "RX-200";

        var result = await _sut.CreateSaleAsync(input, UserRole.Cashier, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        Assert.Equal(1, await db.Sales.CountAsync());
        Assert.Equal(0, await db.ScheduleXDispenses.CountAsync());
    }

    [Fact]
    public async Task ScheduleH1_UnaffectedByScheduleX_SucceedsWithoutCapture()
    {
        // An H1 sale still needs only doctor + Rx — no Schedule-X capture required.
        var p = AddProduct("Azithromycin", schedule: DrugSchedule.H1);
        AddBatch(p.ProductId, "AZ1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 2m));
        input.DoctorName = "Dr. Rao";
        input.PrescriptionRef = "RX-100";
        input.ScheduleX = null; // no X capture

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var db = _fixture.NewContext();
        Assert.Equal(1, await db.Sales.CountAsync());
        Assert.Equal(0, await db.ScheduleXDispenses.CountAsync()); // no X dispense for an H1 sale
    }

    // ---- Bill-level discount (GST on the net; line/header reconciliation) ----

    [Fact]
    public async Task BillDiscount_ReducesSubtotal_GstOnNet_AndRoundOff()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));

        // 10 @ 20 = 200 gross; bill discount 20 => net 180; 12% => 21.60 (10.80 + 10.80).
        // preRound 201.60 -> total 202, round-off 0.40.
        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 10m));
        input.BillDiscount = 20m;

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var r = result.Value;
        Assert.Equal(180m, r.Subtotal);   // taxable after bill discount
        Assert.Equal(20m, r.Discount);    // header discount = bill discount
        Assert.Equal(10.80m, r.Cgst);     // GST computed on the NET (180), not 200
        Assert.Equal(10.80m, r.Sgst);
        Assert.Equal(202m, r.Total);
        Assert.Equal(0.40m, r.RoundOff);
    }

    [Fact]
    public async Task BillDiscount_LineItemsFootToHeader()
    {
        // Two products in one bill; bill discount apportioned across both lines.
        var pa = AddProduct("Amoxicillin", gstRate: 12m);
        var pb = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(pa.ProductId, "A1", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1)); // 10@10 = 100
        AddBatch(pb.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1)); // 15@20 = 300

        var input = Sale(PaymentMode.Cash, Line(pa.ProductId, 10m), Line(pb.ProductId, 15m));
        input.BillDiscount = 40m; // 100/400 -> 10 to A, 300/400 -> 30 to B

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var r = result.Value;
        Assert.Equal(360m, r.Subtotal); // (100-10) + (300-30)
        Assert.Equal(40m, r.Discount);

        var db = _fixture.NewContext();
        var items = await db.SaleItems.ToListAsync();

        // Line/header reconciliation: Σ(SaleItem.LineTotal) == Sale.Total, Σ discount == header.
        Assert.Equal(r.Total, items.Sum(i => i.LineTotal));
        Assert.Equal(r.Discount, items.Sum(i => i.Discount));
        Assert.Equal(r.Cgst, items.Sum(i => i.Cgst));
        Assert.Equal(r.Sgst, items.Sum(i => i.Sgst));
    }

    [Fact]
    public async Task BillDiscount_CreditSale_BalanceEqualsDiscountedTotal()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));
        int customerId = AddCustomer(balance: 0m);

        // 10 @ 20 = 200; bill discount 20 => net 180; +21.60 GST => 201.60 -> 202.
        var input = Sale(PaymentMode.Credit, Line(p.ProductId, 10m));
        input.BillDiscount = 20m;
        input.CustomerId = customerId;

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        Assert.Equal(202m, result.Value.Total);
        var customer = await _fixture.NewContext().Customers.SingleAsync(c => c.CustomerId == customerId);
        Assert.Equal(202m, customer.Balance); // khata increment == discounted total
    }

    [Fact]
    public async Task BillDiscount_WithLineDiscount_BothFootToHeader()
    {
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));

        // 10 @ 20 = 200 gross; line discount 20 => 180; bill discount 30 => 150 net.
        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 10m, lineDiscount: 20m));
        input.BillDiscount = 30m;

        var result = await _sut.CreateSaleAsync(input, UserRole.Owner, _userId);

        Assert.True(result.Succeeded);
        var r = result.Value;
        Assert.Equal(150m, r.Subtotal);   // 200 - 20 - 30
        Assert.Equal(50m, r.Discount);    // line 20 + bill 30
        Assert.Equal(9.00m, r.Cgst);      // 150 * 6%
        Assert.Equal(9.00m, r.Sgst);

        var db = _fixture.NewContext();
        var items = await db.SaleItems.ToListAsync();
        Assert.Equal(r.Total, items.Sum(i => i.LineTotal));
        Assert.Equal(r.Discount, items.Sum(i => i.Discount));
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

    // ---- Reprint lookup ----

    [Fact]
    public async Task FindSaleIdByBillNo_ReturnsSaleId()
    {
        var p = AddProduct("Paracetamol");
        AddBatch(p.ProductId, "B1", qty: 10m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1));
        var sale = await _sut.CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 1m)), UserRole.Owner, _userId);
        Assert.True(sale.Succeeded);

        var found = await _sut.FindSaleIdByBillNoAsync("INV-000001");
        Assert.True(found.Succeeded);
        Assert.Equal(sale.Value.SaleId, found.Value);
    }

    [Fact]
    public async Task FindSaleIdByBillNo_UnknownBill_Fails()
    {
        var found = await _sut.FindSaleIdByBillNoAsync("INV-999999");
        Assert.False(found.Succeeded);
        Assert.Contains("No bill found", found.Error!);
    }
}
