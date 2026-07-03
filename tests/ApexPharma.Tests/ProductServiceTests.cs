using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// ProductService tests (plan.md §6.1, §6.2, §7.2). Covers create/update/list/search/
/// deactivate happy paths and every validation branch: bad GST rate, negative reorder,
/// duplicate barcode, missing FKs — plus RBAC rejection.
/// </summary>
public class ProductServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly IProductService _sut;
    private int _categoryId;
    private int _manufacturerId;

    public ProductServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        _sut = new ProductService(_fixture.Context, auth);
        Seed();
    }

    public void Dispose() => _fixture.Dispose();

    private void Seed()
    {
        var db = _fixture.Context;
        var cat = new Category { Name = "Medication" };
        var man = new Manufacturer { Name = "Cipla" };
        db.Categories.Add(cat);
        db.Manufacturers.Add(man);
        db.SaveChanges();
        _categoryId = cat.CategoryId;
        _manufacturerId = man.ManufacturerId;
    }

    private ProductInput Valid(string name = "Paracetamol 500", string? barcode = "8901234567890") => new()
    {
        Name = name,
        GenericName = "Paracetamol",
        CategoryId = _categoryId,
        ManufacturerId = _manufacturerId,
        HsnCode = "3004",
        GstRate = 12m,
        Schedule = DrugSchedule.H,
        DosageForm = "Tablet",
        Strength = "500mg",
        PackSize = "10x10",
        Unit = "Strip",
        RackLocation = "A3",
        ReorderLevel = 20,
        Barcode = barcode
    };

    [Fact]
    public async Task Create_Valid_PersistsAllFields()
    {
        var result = await _sut.CreateAsync(Valid(), UserRole.Owner);

        Assert.True(result.Succeeded);
        var p = result.Value!;
        Assert.Equal("Paracetamol 500", p.Name);
        Assert.Equal(12m, p.GstRate);
        Assert.Equal(DrugSchedule.H, p.Schedule);
        Assert.Equal("8901234567890", p.Barcode);
        Assert.True(p.IsActive);
    }

    [Fact]
    public async Task Create_BlankName_Fails()
    {
        var input = Valid();
        input.Name = "  ";

        var result = await _sut.CreateAsync(input, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("name is required", result.Error!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(18)]
    [InlineData(28)]
    public async Task Create_ValidGstSlabs_Succeed(int rate)
    {
        var input = Valid(barcode: null);
        input.GstRate = rate;

        var result = await _sut.CreateAsync(input, UserRole.Owner);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(-5)]
    public async Task Create_InvalidGstRate_Fails(int rate)
    {
        var input = Valid();
        input.GstRate = rate;

        var result = await _sut.CreateAsync(input, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("GST rate", result.Error!);
    }

    [Fact]
    public async Task Create_NegativeReorderLevel_Fails()
    {
        var input = Valid();
        input.ReorderLevel = -1;

        var result = await _sut.CreateAsync(input, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("Reorder level", result.Error!);
    }

    [Fact]
    public async Task Create_MissingCategory_Fails()
    {
        var input = Valid();
        input.CategoryId = 9999;

        var result = await _sut.CreateAsync(input, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("category", result.Error!);
    }

    [Fact]
    public async Task Create_MissingManufacturer_Fails()
    {
        var input = Valid();
        input.ManufacturerId = 9999;

        var result = await _sut.CreateAsync(input, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("manufacturer", result.Error!);
    }

    [Fact]
    public async Task Create_DuplicateBarcode_Fails()
    {
        await _sut.CreateAsync(Valid("First", "9990001112223"), UserRole.Owner);

        var dupe = await _sut.CreateAsync(Valid("Second", "9990001112223"), UserRole.Owner);

        Assert.False(dupe.Succeeded);
        Assert.Contains("Barcode", dupe.Error!);
    }

    [Fact]
    public async Task Create_MultipleWithoutBarcode_Succeed()
    {
        // Nullable barcode ⇒ many products may have none (filtered unique index).
        var a = await _sut.CreateAsync(Valid("NoBarcodeA", barcode: null), UserRole.Owner);
        var b = await _sut.CreateAsync(Valid("NoBarcodeB", barcode: null), UserRole.Owner);

        Assert.True(a.Succeeded);
        Assert.True(b.Succeeded);
    }

    [Fact]
    public async Task Update_ChangesFields()
    {
        var created = await _sut.CreateAsync(Valid(), UserRole.Owner);
        var edit = Valid("Renamed", "8901234567890"); // same barcode as itself — allowed
        edit.GstRate = 5m;

        var result = await _sut.UpdateAsync(created.Value!.ProductId, edit, UserRole.Owner);

        Assert.True(result.Succeeded);
        var reloaded = await _fixture.NewContext().Products.FirstAsync(p => p.ProductId == created.Value.ProductId);
        Assert.Equal("Renamed", reloaded.Name);
        Assert.Equal(5m, reloaded.GstRate);
    }

    [Fact]
    public async Task Update_ToAnotherProductsBarcode_Fails()
    {
        await _sut.CreateAsync(Valid("Owns", "1111111111111"), UserRole.Owner);
        var second = await _sut.CreateAsync(Valid("Other", "2222222222222"), UserRole.Owner);

        var edit = Valid("Other", "1111111111111"); // steal the first product's barcode
        var result = await _sut.UpdateAsync(second.Value!.ProductId, edit, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("Barcode", result.Error!);
    }

    [Fact]
    public async Task Search_ByName_ReturnsMatch()
    {
        await _sut.CreateAsync(Valid("Amoxicillin", "3333333333333"), UserRole.Owner);
        await _sut.CreateAsync(Valid("Ibuprofen", "4444444444444"), UserRole.Owner);

        var results = await _sut.SearchAsync("amox");

        Assert.Single(results);
        Assert.Equal("Amoxicillin", results[0].Name);
    }

    [Fact]
    public async Task Search_ByBarcode_ReturnsMatch()
    {
        await _sut.CreateAsync(Valid("ByCode", "5555555555555"), UserRole.Owner);

        var results = await _sut.SearchAsync("5555555555555");

        Assert.Single(results);
        Assert.Equal("ByCode", results[0].Name);
    }

    [Fact]
    public async Task FindByBarcode_ExactMatch_ReturnsProduct()
    {
        await _sut.CreateAsync(Valid("Scanned", "6006006006006"), UserRole.Owner);

        var found = await _sut.FindByBarcodeAsync("6006006006006");

        Assert.NotNull(found);
        Assert.Equal("Scanned", found!.Name);
    }

    [Fact]
    public async Task FindByBarcode_TrimsInput()
    {
        await _sut.CreateAsync(Valid("Trimmed", "6116116116116"), UserRole.Owner);

        var found = await _sut.FindByBarcodeAsync("  6116116116116  ");

        Assert.NotNull(found);
        Assert.Equal("Trimmed", found!.Name);
    }

    [Fact]
    public async Task FindByBarcode_UnknownCode_ReturnsNull()
    {
        await _sut.CreateAsync(Valid("Known", "6226226226226"), UserRole.Owner);

        Assert.Null(await _sut.FindByBarcodeAsync("0000000000000"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindByBarcode_BlankOrWhitespace_ReturnsNull(string code)
    {
        await _sut.CreateAsync(Valid("Anything", "6336336336336"), UserRole.Owner);

        Assert.Null(await _sut.FindByBarcodeAsync(code));
    }

    [Fact]
    public async Task FindByBarcode_DoesNotMatchByName()
    {
        // Name is NOT a barcode: searching the product's name must not resolve via FindByBarcode.
        await _sut.CreateAsync(Valid("Aspirin", "6446446446446"), UserRole.Owner);

        Assert.Null(await _sut.FindByBarcodeAsync("Aspirin"));
    }

    [Fact]
    public async Task FindByBarcode_InactiveProduct_ReturnsNull()
    {
        var created = await _sut.CreateAsync(Valid("Retired", "6556556556556"), UserRole.Owner);
        await _sut.DeactivateAsync(created.Value!.ProductId, UserRole.Owner);

        // Active-only, consistent with SearchAsync.
        Assert.Null(await _sut.FindByBarcodeAsync("6556556556556"));
    }

    [Fact]
    public async Task Deactivate_HidesFromDefaultList()
    {
        var created = await _sut.CreateAsync(Valid(), UserRole.Owner);

        await _sut.DeactivateAsync(created.Value!.ProductId, UserRole.Owner);

        Assert.Empty(await _sut.ListAsync());
        Assert.Single(await _sut.ListAsync(includeInactive: true));
    }

    [Fact]
    public async Task Create_AsCashier_IsRejected()
    {
        var result = await _sut.CreateAsync(Valid(), UserRole.Cashier);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
        Assert.Equal(0, await _fixture.Context.Products.CountAsync());
    }

    [Fact]
    public async Task Pharmacist_CanManageProducts()
    {
        var result = await _sut.CreateAsync(Valid("PharmProduct", "7777777777777"), UserRole.Pharmacist);

        Assert.True(result.Succeeded);
    }
}
