using System;
using System.Linq;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Invoicing;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// InvoiceService tests (plan.md §11, §14): the assembled <see cref="InvoiceModel"/> carries the
/// correct GSTIN / DL / bill-no / line totals / CGST-SGST breakup / grand total (asserted on the
/// model, since PDF pixels aren't unit-testable), and <see cref="IInvoiceService.GenerateReceiptPdf"/>
/// renders a non-empty PDF without throwing — a smoke test proving QuestPDF + the Community license
/// work end to end.
/// </summary>
public class InvoiceServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly BillingService _billing;
    private readonly SettingsService _settings;
    private readonly InvoiceService _sut;
    private int _userId;
    private int _supplierId;
    private int _catId;
    private int _manId;

    public InvoiceServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        var gst = new GstService();
        _billing = new BillingService(_fixture.Context, auth, gst, TestTz.IstProvider());
        _settings = new SettingsService(_fixture.Context, auth);
        _sut = new InvoiceService(_fixture.Context, _settings, TestTz.IstProvider());
        Seed();
    }

    public void Dispose() => _fixture.Dispose();

    private void Seed()
    {
        var db = _fixture.Context;

        var role = new Role { Name = "Owner" };
        db.Roles.Add(role);
        db.SaveChanges();

        var user = new User { Username = "owner", PasswordHash = "x", FullName = "Rakesh Sharma", RoleId = role.RoleId };
        var cat = new Category { Name = "Medication" };
        var man = new Manufacturer { Name = "Cipla" };
        var supplier = new Supplier { Name = "MediDist", IsActive = true };
        db.Users.Add(user);
        db.Categories.Add(cat);
        db.Manufacturers.Add(man);
        db.Suppliers.Add(supplier);
        db.SaveChanges();

        _userId = user.UserId;
        _supplierId = supplier.SupplierId;
        _catId = cat.CategoryId;
        _manId = man.ManufacturerId;
    }

    private Product AddProduct(string name, decimal gstRate = 12m, DrugSchedule schedule = DrugSchedule.None, string? hsn = "3004")
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
            ReorderLevel = 0,
        };
        db.Products.Add(p);
        db.SaveChanges();
        return p;
    }

    private void AddBatch(int productId, string batchNo, decimal qty, decimal salePrice, DateTime expiry, decimal mrp = 0m)
    {
        var db = _fixture.Context;
        db.Batches.Add(new Batch
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
        });
        db.SaveChanges();
    }

    private static SaleInput Sale(PaymentMode mode, params SaleLineInput[] lines)
        => new() { PaymentMode = mode, Lines = lines.ToList() };

    private static SaleLineInput Line(int productId, decimal qty, decimal lineDiscount = 0m)
        => new() { ProductId = productId, Qty = qty, LineDiscount = lineDiscount };

    private async Task<int> CreateSaleAsync(SaleInput input)
    {
        MasterResult<SaleReceipt> result = await _billing.CreateSaleAsync(input, UserRole.Owner, _userId);
        Assert.True(result.Succeeded, result.Error);
        return result.Value.SaleId;
    }

    private async Task ConfigureProfileAsync()
    {
        var profile = new PharmacyProfile
        {
            PharmacyName = "Apex Retail Chemists",
            AddressLine = "12 MG Road",
            City = "Kolkata",
            State = "West Bengal",
            Gstin = "19AABCU9603R1ZM",
            DlNumber = "WB-20B-1234 / WB-21B-1234",
            Phone = "9800000000",
            InvoiceFooter = "No returns on medicines.",
            NearExpiryDays = 90,
            TaxRoundingMode = TaxRoundingMode.NearestRupee,
        };
        MasterResult saved = await _settings.SaveProfileAsync(profile, UserRole.Owner);
        Assert.True(saved.Succeeded, saved.Error);
    }

    [Fact]
    public async Task BuildInvoice_CarriesGstinDlBillNoAndTotals()
    {
        await ConfigureProfileAsync();
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));

        // 10 @ 20 = 200 base; 12% => 24 (12 CGST + 12 SGST); total 224 (whole).
        int saleId = await CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 10m)));

        MasterResult<InvoiceModel> result = await _sut.BuildInvoiceAsync(saleId);
        Assert.True(result.Succeeded, result.Error);
        InvoiceModel m = result.Value!;

        // Pharmacy header (compliance).
        Assert.Equal("Apex Retail Chemists", m.PharmacyName);
        Assert.Equal("19AABCU9603R1ZM", m.Gstin);
        Assert.Equal("WB-20B-1234 / WB-21B-1234", m.DlNumber);
        Assert.Equal("Kolkata, West Bengal", m.CityState);
        Assert.Equal("Rakesh Sharma", m.CashierName);

        // Bill header + money roll-up.
        Assert.Equal("INV-000001", m.BillNo);
        Assert.Equal(200m, m.Subtotal);
        Assert.Equal(12m, m.Cgst);
        Assert.Equal(12m, m.Sgst);
        Assert.Equal(224m, m.Total);

        // Line.
        InvoiceLine line = Assert.Single(m.Lines);
        Assert.Equal("Paracetamol", line.ProductName);
        Assert.Equal("B1", line.BatchNo);
        Assert.Equal(10m, line.Qty);
        Assert.Equal(20m, line.Rate);
        Assert.Equal("3004", line.HsnCode);

        // Line amounts foot to the header total.
        Assert.Equal(m.Total, m.Lines.Sum(l => l.Amount));
    }

    // ---- IST-stamped printed invoice date (Phase 2g — IST-stamping) ----

    [Fact]
    public async Task BuildInvoice_BillDate_IsShownInIst_NotUtc()
    {
        // The stored Sale.BillDate is a UTC instant; the printed invoice date must be the pharmacy's
        // LOCAL (IST) calendar day/time. Store a BillDate that is a DIFFERENT calendar day in IST vs
        // UTC — 2026-06-30 20:00:00Z == 2026-07-01 01:30 IST — and assert the model carries the IST
        // date (2026-07-01), not the UTC date (2026-06-30). Host-timezone-independent (IST provider
        // injected into the sut). Sale.BillDate stays UTC in the DB — only the DISPLAY is localized.
        await ConfigureProfileAsync();
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));
        int saleId = await CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 1m)));

        // Override the stored timestamp to the explicit boundary UTC instant.
        DateTime billDateUtc = new(2026, 6, 30, 20, 0, 0, DateTimeKind.Utc);
        {
            var writeDb = _fixture.NewContext();
            Sale stored = await writeDb.Sales.SingleAsync(s => s.SaleId == saleId);
            stored.BillDate = billDateUtc;
            await writeDb.SaveChangesAsync();
        }

        MasterResult<InvoiceModel> result = await _sut.BuildInvoiceAsync(saleId);
        Assert.True(result.Succeeded, result.Error);
        InvoiceModel m = result.Value!;

        // Printed date is the IST wall-clock: 2026-07-01 01:30 (NOT 2026-06-30 20:00 UTC).
        Assert.Equal(new DateTime(2026, 7, 1), m.BillDate.Date);
        Assert.Equal(new DateTime(2026, 7, 1, 1, 30, 0), m.BillDate);
        Assert.NotEqual(billDateUtc.Date, m.BillDate.Date);

        // The stored Sale.BillDate remains the UTC instant — only the display was localized.
        var checkDb = _fixture.NewContext();
        Sale reread = await checkDb.Sales.SingleAsync(s => s.SaleId == saleId);
        Assert.Equal(billDateUtc, DateTime.SpecifyKind(reread.BillDate, DateTimeKind.Utc));
    }

    [Fact]
    public async Task BuildInvoice_TaxSummary_GroupsByRate_AndFootsToHeader()
    {
        await ConfigureProfileAsync();
        var pa = AddProduct("Amoxicillin", gstRate: 12m, hsn: "3004");
        var pb = AddProduct("Cough Syrup", gstRate: 5m, hsn: "3004");
        AddBatch(pa.ProductId, "A1", qty: 100m, salePrice: 10m, expiry: DateTime.UtcNow.Date.AddYears(1)); // 10@10 = 100 @12%
        AddBatch(pb.ProductId, "B1", qty: 100m, salePrice: 50m, expiry: DateTime.UtcNow.Date.AddYears(1)); // 2@50 = 100 @5%

        int saleId = await CreateSaleAsync(Sale(PaymentMode.Cash, Line(pa.ProductId, 10m), Line(pb.ProductId, 2m)));

        MasterResult<InvoiceModel> result = await _sut.BuildInvoiceAsync(saleId);
        Assert.True(result.Succeeded, result.Error);
        InvoiceModel m = result.Value!;

        // Two GST bands (12% and 5%).
        Assert.Equal(2, m.TaxSummary.Count);
        InvoiceTaxSummaryRow band12 = m.TaxSummary.Single(t => t.GstRate == 12m);
        InvoiceTaxSummaryRow band5 = m.TaxSummary.Single(t => t.GstRate == 5m);
        Assert.Equal(100m, band12.Taxable);
        Assert.Equal(100m, band5.Taxable);
        Assert.Equal(6m, band12.Cgst);   // 100 * 6%
        Assert.Equal(2.50m, band5.Cgst); // 100 * 2.5%

        // The summary CGST/SGST reconcile to the header.
        Assert.Equal(m.Cgst, m.TaxSummary.Sum(t => t.Cgst));
        Assert.Equal(m.Sgst, m.TaxSummary.Sum(t => t.Sgst));
    }

    [Fact]
    public async Task BuildInvoice_TaxSummary_FootsToSubtotal_OnBillDiscountSale()
    {
        // A whole-bill discount is apportioned across the lines and folded into each
        // SaleItem.Discount. The tax-summary Taxable must be the POST-bill-discount net, so the
        // block foots to the header: Σ(Taxable) == Subtotal and each band's CGST/SGST == header.
        // Uses two GST bands + line AND bill discounts + fractional prices to exercise apportionment.
        await ConfigureProfileAsync();
        var pa = AddProduct("Amoxicillin", gstRate: 12m, hsn: "3004");
        var pb = AddProduct("Cough Syrup", gstRate: 5m, hsn: "3005");
        AddBatch(pa.ProductId, "A1", qty: 100m, salePrice: 33.33m, expiry: DateTime.UtcNow.Date.AddYears(1));
        AddBatch(pb.ProductId, "B1", qty: 100m, salePrice: 17.77m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(
            PaymentMode.Cash,
            Line(pa.ProductId, 3m, lineDiscount: 1m),
            Line(pb.ProductId, 4m, lineDiscount: 0.5m));
        input.BillDiscount = 7m; // whole-bill discount, apportioned across both bands
        int saleId = await CreateSaleAsync(input);

        // The persisted header we must foot to.
        Sale sale = _fixture.Context.Sales.AsNoTracking().Single(s => s.SaleId == saleId);

        MasterResult<InvoiceModel> result = await _sut.BuildInvoiceAsync(saleId);
        Assert.True(result.Succeeded, result.Error);
        InvoiceModel m = result.Value!;

        // Sanity: the discount actually landed and both bands are present.
        Assert.True(sale.Discount > 0m);
        Assert.Equal(2, m.TaxSummary.Count);

        // The tax summary foots to the header exactly (this is the LOW finding being fixed).
        Assert.Equal(sale.Subtotal, m.TaxSummary.Sum(t => t.Taxable));
        Assert.Equal(sale.Cgst, m.TaxSummary.Sum(t => t.Cgst));
        Assert.Equal(sale.Sgst, m.TaxSummary.Sum(t => t.Sgst));

        // And the model mirrors the header (belt-and-suspenders).
        Assert.Equal(m.Subtotal, m.TaxSummary.Sum(t => t.Taxable));
    }

    [Fact]
    public async Task BuildInvoice_ScheduledDrug_CarriesDoctorRxAndFlag()
    {
        await ConfigureProfileAsync();
        var p = AddProduct("Antibiotic", gstRate: 12m, schedule: DrugSchedule.H1);
        AddBatch(p.ProductId, "H1B", qty: 10m, salePrice: 30m, expiry: DateTime.UtcNow.Date.AddYears(1));

        var input = Sale(PaymentMode.Cash, Line(p.ProductId, 1m));
        input.DoctorName = "Dr. Sen";
        input.PrescriptionRef = "RX-777";
        int saleId = await CreateSaleAsync(input);

        MasterResult<InvoiceModel> result = await _sut.BuildInvoiceAsync(saleId);
        InvoiceModel m = result.Value!;
        Assert.True(m.HasScheduledDrug);
        Assert.Equal("Dr. Sen", m.DoctorName);
        Assert.Equal("RX-777", m.PrescriptionRef);
    }

    [Fact]
    public async Task GenerateReceiptPdf_ReturnsNonEmptyPdf()
    {
        await ConfigureProfileAsync();
        var p = AddProduct("Paracetamol", gstRate: 12m);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 20m, expiry: DateTime.UtcNow.Date.AddYears(1));
        int saleId = await CreateSaleAsync(Sale(PaymentMode.Cash, Line(p.ProductId, 10m)));

        MasterResult<byte[]> result = await _sut.GenerateReceiptPdf(saleId);

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Value);
        Assert.True(result.Value!.Length > 0, "PDF byte array should be non-empty");
        // A well-formed PDF starts with the "%PDF" magic bytes.
        Assert.Equal((byte)'%', result.Value[0]);
        Assert.Equal((byte)'P', result.Value[1]);
        Assert.Equal((byte)'D', result.Value[2]);
        Assert.Equal((byte)'F', result.Value[3]);
    }

    [Fact]
    public async Task GenerateReceiptPdf_WithScheduledDrugAndDiscounts_Renders()
    {
        // Exercise the schedule note, tax summary, and discount rows all in one render.
        await ConfigureProfileAsync();
        var p = AddProduct("Antibiotic", gstRate: 12m, schedule: DrugSchedule.H);
        AddBatch(p.ProductId, "B1", qty: 100m, salePrice: 40m, expiry: DateTime.UtcNow.Date.AddMonths(8));

        var input = Sale(PaymentMode.Credit, Line(p.ProductId, 5m, lineDiscount: 10m));
        input.DoctorName = "Dr. Roy";
        input.PrescriptionRef = "RX-9";
        input.BillDiscount = 5m;
        input.CustomerId = AddCustomer();
        int saleId = await CreateSaleAsync(input);

        MasterResult<byte[]> result = await _sut.GenerateReceiptPdf(saleId);
        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.Value!.Length > 0);
    }

    [Fact]
    public async Task GenerateReceiptPdf_UnknownSale_Fails()
    {
        MasterResult<byte[]> result = await _sut.GenerateReceiptPdf(99999);
        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.Error!);
    }

    private int AddCustomer(string name = "Ravi")
    {
        var db = _fixture.Context;
        var c = new Customer { Name = name, CreditLimit = 10000m, Balance = 0m };
        db.Customers.Add(c);
        db.SaveChanges();
        return c.CustomerId;
    }
}
