using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Invoicing;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Desktop.Services;
using ApexPharma.Desktop.ViewModels.Billing;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// Verifies the deferred Phase-1d nit fix (plan.md §6.1): the Billing screen's LIVE preview
/// recomputes CGST/SGST/Total on the bill-discounted NET, so the on-screen figures match the
/// authoritative printed receipt from <see cref="BillingService"/> instead of overstating GST when
/// a bill discount is applied.
/// </summary>
public class BillingViewModelPreviewTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly BillingViewModel _vm;
    private readonly BillingService _billing;
    private int _userId;
    private int _productId;
    private int _scheduleHProductId;
    private int _scheduleXProductId;

    public BillingViewModelPreviewTests()
    {
        var auth = new AuthService(_fixture.Context);
        var gst = new GstService();
        _billing = new BillingService(_fixture.Context, auth, gst, TestTz.IstProvider());
        var settings = new SettingsService(_fixture.Context, auth);
        var invoices = new InvoiceService(_fixture.Context, settings, TestTz.IstProvider());
        var session = new SessionContext();

        var products = new ProductService(_fixture.Context, auth);
        var customers = new CustomerService(_fixture.Context, auth);
        var inventory = new InventoryService(_fixture.Context);

        _vm = new BillingViewModel(_billing, products, customers, inventory, gst, invoices, new StubPrinter(), session, auth);
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
        var cat = new Category { Name = "Medication" };
        var man = new Manufacturer { Name = "Cipla" };
        var supplier = new Supplier { Name = "MediDist", IsActive = true };
        db.Users.Add(user);
        db.Categories.Add(cat);
        db.Manufacturers.Add(man);
        db.Suppliers.Add(supplier);
        db.SaveChanges();
        _userId = user.UserId;

        var p = new Product { Name = "Paracetamol", CategoryId = cat.CategoryId, ManufacturerId = man.ManufacturerId, GstRate = 12m, IsActive = true, Barcode = "8901234567890" };
        db.Products.Add(p);
        db.SaveChanges();
        _productId = p.ProductId;

        db.Batches.Add(new Batch
        {
            ProductId = p.ProductId,
            BatchNo = "B1",
            ExpiryDate = DateTime.UtcNow.Date.AddYears(1),
            Mrp = 20m,
            PurchasePrice = 15m,
            SalePrice = 20m,
            QtyOnHand = 100m,
            SupplierId = supplier.SupplierId,
            ReceivedDate = DateTime.UtcNow,
        });
        db.SaveChanges();

        // A Schedule-H product with its own barcode + batch, so a scan of it must flip
        // RequiresPrescription — proving scan == manual add for the schedule flags too.
        var scheduledProduct = new Product
        {
            Name = "Alprazolam",
            CategoryId = cat.CategoryId,
            ManufacturerId = man.ManufacturerId,
            GstRate = 12m,
            IsActive = true,
            Barcode = "8907654321098",
            Schedule = DrugSchedule.H,
        };
        db.Products.Add(scheduledProduct);
        db.SaveChanges();
        _scheduleHProductId = scheduledProduct.ProductId;

        db.Batches.Add(new Batch
        {
            ProductId = scheduledProduct.ProductId,
            BatchNo = "H1",
            ExpiryDate = DateTime.UtcNow.Date.AddYears(1),
            Mrp = 50m,
            PurchasePrice = 30m,
            SalePrice = 50m,
            QtyOnHand = 40m,
            SupplierId = supplier.SupplierId,
            ReceivedDate = DateTime.UtcNow,
        });
        db.SaveChanges();

        // A Schedule-X product with its own barcode + batch, to drive the pharmacist-required guard.
        var xProduct = new Product
        {
            Name = "Morphine",
            CategoryId = cat.CategoryId,
            ManufacturerId = man.ManufacturerId,
            GstRate = 12m,
            IsActive = true,
            Barcode = "8901111111119",
            Schedule = DrugSchedule.X,
        };
        db.Products.Add(xProduct);
        db.SaveChanges();
        _scheduleXProductId = xProduct.ProductId;

        db.Batches.Add(new Batch
        {
            ProductId = xProduct.ProductId,
            BatchNo = "X1",
            ExpiryDate = DateTime.UtcNow.Date.AddYears(1),
            Mrp = 80m,
            PurchasePrice = 40m,
            SalePrice = 80m,
            QtyOnHand = 30m,
            SupplierId = supplier.SupplierId,
            ReceivedDate = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Preview_WithBillDiscount_MatchesServiceGstOnNet()
    {
        await _vm.InitializeAsync(UserRole.Owner);
        _vm.SelectedProduct = _vm.AvailableProducts.Single(p => p.ProductId == _productId);
        _vm.NewLineQty = 10m;
        _vm.AddLineCommand.Execute(null);

        // 10 @ 20 = 200 gross; bill discount 20 => net 180; 12% => 21.60 (10.80 + 10.80); total 202.
        _vm.BillDiscount = 20m;

        // The preview must show GST on the NET 180, not the pre-discount 200.
        Assert.Equal(180m, _vm.Subtotal);
        Assert.Equal(10.80m, _vm.TotalCgst);
        Assert.Equal(10.80m, _vm.TotalSgst);
        Assert.Equal(202m, _vm.GrandTotal);

        // And it agrees with the authoritative service.
        var result = await _billing.CreateSaleAsync(
            new SaleInput
            {
                PaymentMode = PaymentMode.Cash,
                BillDiscount = 20m,
                Lines = new() { new SaleLineInput { ProductId = _productId, Qty = 10m } },
            }, UserRole.Owner, _userId);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(result.Value.Cgst, _vm.TotalCgst);
        Assert.Equal(result.Value.Sgst, _vm.TotalSgst);
        Assert.Equal(result.Value.Total, _vm.GrandTotal);
    }

    [Fact]
    public async Task Preview_WithoutBillDiscount_IsUnchanged()
    {
        await _vm.InitializeAsync(UserRole.Owner);
        _vm.SelectedProduct = _vm.AvailableProducts.Single(p => p.ProductId == _productId);
        _vm.NewLineQty = 10m;
        _vm.AddLineCommand.Execute(null);

        // No bill discount: 10 @ 20 = 200; 12% => 24; total 224.
        Assert.Equal(200m, _vm.Subtotal);
        Assert.Equal(12m, _vm.TotalCgst);
        Assert.Equal(12m, _vm.TotalSgst);
        Assert.Equal(224m, _vm.GrandTotal);
    }

    [Fact]
    public async Task ScanBarcode_KnownCode_AddsOneLineAndClearsBarcode()
    {
        await _vm.InitializeAsync(UserRole.Owner);

        _vm.BarcodeText = "8901234567890";
        // Await the same entry point the Enter-key command fires (deterministic in the test).
        await _vm.ScanBarcodeAsync();

        Assert.Single(_vm.Lines);
        Assert.Equal(_productId, _vm.Lines[0].SelectedProduct!.ProductId);
        Assert.Equal(string.Empty, _vm.BarcodeText);
        Assert.False(_vm.IsError);
    }

    [Fact]
    public async Task ScanBarcode_UnknownCode_SetsErrorAndAddsNoLine()
    {
        await _vm.InitializeAsync(UserRole.Owner);

        _vm.BarcodeText = "0000000000000";
        await _vm.ScanBarcodeAsync();

        Assert.Empty(_vm.Lines);
        Assert.True(_vm.IsError);
        Assert.Contains("0000000000000", _vm.StatusMessage!);
        // The typed code is kept for correction / re-scan.
        Assert.Equal("0000000000000", _vm.BarcodeText);
    }

    [Fact]
    public async Task ScanBarcode_KnownCode_InheritsFefoPreview()
    {
        await _vm.InitializeAsync(UserRole.Owner);

        _vm.BarcodeText = "8901234567890";
        await _vm.ScanBarcodeAsync();

        // The scanned line must inherit the SAME FEFO preview a manual add would — batch B1 @ 20,
        // 100 on hand — proving scan and manual add share the one AddProductLine path.
        var line = Assert.Single(_vm.Lines);
        Assert.Equal(_productId, line.SelectedProduct!.ProductId);
        Assert.Equal("B1", line.BatchDisplay);
        Assert.Equal(20m, line.Rate);
        Assert.Equal(100m, line.AvailableQty);
    }

    [Fact]
    public async Task ScanBarcode_ScheduleHProduct_FlipsRequiresPrescription()
    {
        await _vm.InitializeAsync(UserRole.Owner);
        Assert.False(_vm.RequiresPrescription);

        _vm.BarcodeText = "8907654321098";
        await _vm.ScanBarcodeAsync();

        // Scanning a Schedule-H product must raise the Rx prompt exactly as a manual add would.
        var line = Assert.Single(_vm.Lines);
        Assert.Equal(_scheduleHProductId, line.SelectedProduct!.ProductId);
        Assert.Equal(DrugSchedule.H, line.Schedule);
        Assert.True(_vm.RequiresPrescription);
    }

    [Fact]
    public async Task ResetForNextSale_ClearsBarcodeText()
    {
        await _vm.InitializeAsync(UserRole.Owner);

        // Scan-add a line, and leave a leftover barcode in the box (as if a stray scan followed).
        _vm.BarcodeText = "8901234567890";
        await _vm.ScanBarcodeAsync();
        _vm.BarcodeText = "residue-code";

        // NewSaleCommand -> ResetForNextSale -> ClearForm (the same reset Complete Sale runs).
        _vm.NewSaleCommand.Execute(null);

        // ClearForm() must wipe the scan box so no barcode leaks into the next customer's bill.
        Assert.Equal(string.Empty, _vm.BarcodeText);
    }

    [Fact]
    public async Task ScanBarcode_TwoOverlappingScans_SameCode_AddsExactlyOneLine()
    {
        // A gated product service whose FindByBarcodeAsync blocks until released, so we can start a
        // second scan while the first is still awaiting the (shared) DbContext — reproducing the
        // scanner-CR+LF / Enter-auto-repeat / double-press race.
        var gate = new TaskCompletionSource();
        var realProducts = new ProductService(_fixture.Context, new AuthService(_fixture.Context));
        var gatedProducts = new GatedProductService(realProducts, gate.Task);

        var gst = new GstService();
        var settings = new SettingsService(_fixture.Context, new AuthService(_fixture.Context));
        var vm = new BillingViewModel(
            _billing,
            gatedProducts,
            new CustomerService(_fixture.Context, new AuthService(_fixture.Context)),
            new InventoryService(_fixture.Context),
            gst,
            new InvoiceService(_fixture.Context, settings, TestTz.IstProvider()),
            new StubPrinter(),
            new SessionContext(),
            new AuthService(_fixture.Context));

        await vm.InitializeAsync(UserRole.Owner);
        vm.BarcodeText = "8901234567890";

        // Fire two scans without awaiting the first — the re-entrancy guard must make the second a
        // no-op, so only ONE line is ever added and no DbContext exception escapes.
        Task first = vm.ScanBarcodeAsync();
        Task second = vm.ScanBarcodeAsync();

        // Release the gated FindByBarcodeAsync and let both calls unwind.
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(vm.Lines);
        Assert.Equal(_productId, vm.Lines[0].SelectedProduct!.ProductId);
        // Exactly one lookup reached the shared context — the second scan short-circuited.
        Assert.Equal(1, gatedProducts.FindCallCount);
    }

    [Fact]
    public async Task ScheduleX_AsCashier_BlocksCompleteSale_WithPharmacistMessage()
    {
        // A Cashier activates the screen, then scan-adds a Schedule-X line.
        await _vm.InitializeAsync(UserRole.Cashier);
        Assert.False(_vm.CanDispenseScheduleX);

        _vm.BarcodeText = "8901111111119";
        await _vm.ScanBarcodeAsync();

        Assert.Equal(DrugSchedule.X, _vm.Lines.Single().Schedule);
        Assert.True(_vm.RequiresScheduleX);
        Assert.True(_vm.ScheduleXBlocked); // the pharmacist-required warning is shown

        // Complete Sale is refused up front with the friendly message; nothing is persisted.
        _vm.CompleteSaleCommand.Execute(null);
        await Task.Yield();

        Assert.True(_vm.IsError);
        Assert.Contains("pharmacist", _vm.StatusMessage!);
        Assert.Equal(0, await _fixture.NewContext().Sales.CountAsync());
    }

    [Fact]
    public async Task ScheduleX_AsPharmacist_IsNotBlocked()
    {
        await _vm.InitializeAsync(UserRole.Pharmacist);
        Assert.True(_vm.CanDispenseScheduleX);

        _vm.BarcodeText = "8901111111119";
        await _vm.ScanBarcodeAsync();

        Assert.True(_vm.RequiresScheduleX);
        Assert.False(_vm.ScheduleXBlocked); // a Pharmacist may dispense — no block, capture panel enabled
    }

    private sealed class StubPrinter : IReceiptPrinter
    {
        public Task<string> PreviewAsync(byte[] pdfBytes, string billNo) => Task.FromResult(string.Empty);
    }

    /// <summary>
    /// Wraps a real <see cref="IProductService"/> but makes <see cref="FindByBarcodeAsync"/> block
    /// on a gate — letting a test overlap two scans and prove the re-entrancy guard collapses them
    /// to a single line + single lookup on the shared DbContext.
    /// </summary>
    private sealed class GatedProductService : IProductService
    {
        private readonly IProductService _inner;
        private readonly Task _gate;
        private int _findCallCount;

        public GatedProductService(IProductService inner, Task gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public int FindCallCount => _findCallCount;

        public async Task<Product?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _findCallCount);
            await _gate;
            return await _inner.FindByBarcodeAsync(barcode, cancellationToken);
        }

        public Task<MasterResult<Product>> CreateAsync(ProductInput input, UserRole actingRole, CancellationToken cancellationToken = default)
            => _inner.CreateAsync(input, actingRole, cancellationToken);

        public Task<MasterResult> UpdateAsync(int productId, ProductInput input, UserRole actingRole, CancellationToken cancellationToken = default)
            => _inner.UpdateAsync(productId, input, actingRole, cancellationToken);

        public Task<MasterResult> DeactivateAsync(int productId, UserRole actingRole, CancellationToken cancellationToken = default)
            => _inner.DeactivateAsync(productId, actingRole, cancellationToken);

        public Task<IReadOnlyList<Product>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
            => _inner.ListAsync(includeInactive, cancellationToken);

        public Task<IReadOnlyList<Product>> SearchAsync(string term, CancellationToken cancellationToken = default)
            => _inner.SearchAsync(term, cancellationToken);
    }
}
