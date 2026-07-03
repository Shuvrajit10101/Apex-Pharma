using System;
using System.Linq;
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

    public BillingViewModelPreviewTests()
    {
        var auth = new AuthService(_fixture.Context);
        var gst = new GstService();
        _billing = new BillingService(_fixture.Context, auth, gst);
        var settings = new SettingsService(_fixture.Context, auth);
        var invoices = new InvoiceService(_fixture.Context, settings);
        var session = new SessionContext();

        var products = new ProductService(_fixture.Context, auth);
        var customers = new CustomerService(_fixture.Context, auth);
        var inventory = new InventoryService(_fixture.Context);

        _vm = new BillingViewModel(_billing, products, customers, inventory, gst, invoices, new StubPrinter(), session);
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

    private sealed class StubPrinter : IReceiptPrinter
    {
        public Task<string> PreviewAsync(byte[] pdfBytes, string billNo) => Task.FromResult(string.Empty);
    }
}
