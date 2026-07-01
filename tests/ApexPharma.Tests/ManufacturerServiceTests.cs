using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// ManufacturerService tests (plan.md §6.1). Create/rename/list/deactivate happy paths
/// plus duplicate-name (case-insensitive) validation and RBAC rejection.
/// </summary>
public class ManufacturerServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly IManufacturerService _sut;

    public ManufacturerServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        _sut = new ManufacturerService(_fixture.Context, auth);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Create_Valid_Succeeds()
    {
        var result = await _sut.CreateAsync("Cipla", UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Equal("Cipla", result.Value!.Name);
    }

    [Fact]
    public async Task Create_BlankName_Fails()
    {
        var result = await _sut.CreateAsync("", UserRole.Owner);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Create_DuplicateName_CaseInsensitive_Fails()
    {
        await _sut.CreateAsync("Sun Pharma", UserRole.Owner);

        var dupe = await _sut.CreateAsync("SUN PHARMA", UserRole.Owner);

        Assert.False(dupe.Succeeded);
        Assert.Equal(1, await _fixture.Context.Manufacturers.CountAsync());
    }

    [Fact]
    public async Task Rename_Valid_Succeeds()
    {
        var created = await _sut.CreateAsync("Old Co", UserRole.Owner);

        var result = await _sut.RenameAsync(created.Value!.ManufacturerId, "New Co", UserRole.Owner);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Rename_MissingId_Fails()
    {
        var result = await _sut.RenameAsync(999, "Nope", UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.Error!);
    }

    [Fact]
    public async Task Deactivate_HidesFromDefaultList()
    {
        var created = await _sut.CreateAsync("Temp", UserRole.Owner);

        await _sut.DeactivateAsync(created.Value!.ManufacturerId, UserRole.Owner);

        Assert.Empty(await _sut.ListAsync());
    }

    [Fact]
    public async Task Deactivate_Blocked_WhenActiveProductReferencesManufacturer()
    {
        var created = await _sut.CreateAsync("InUse", UserRole.Owner);
        AddProduct(created.Value!.ManufacturerId, isActive: true);

        var result = await _sut.DeactivateAsync(created.Value.ManufacturerId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("active product", result.Error!);
        Assert.Single(await _sut.ListAsync());
    }

    [Fact]
    public async Task Deactivate_Allowed_WhenOnlyInactiveProductsReferenceManufacturer()
    {
        var created = await _sut.CreateAsync("Freed", UserRole.Owner);
        AddProduct(created.Value!.ManufacturerId, isActive: false);

        var result = await _sut.DeactivateAsync(created.Value.ManufacturerId, UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Empty(await _sut.ListAsync());
    }

    [Fact]
    public async Task Create_AsCashier_IsRejected()
    {
        var result = await _sut.CreateAsync("Blocked", UserRole.Cashier);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
    }

    /// <summary>Inserts a product referencing the given manufacturer (and a fresh category).</summary>
    private void AddProduct(int manufacturerId, bool isActive)
    {
        var db = _fixture.Context;
        var cat = new Category { Name = $"C-{System.Guid.NewGuid():N}" };
        db.Categories.Add(cat);
        db.SaveChanges();
        db.Products.Add(new Product
        {
            Name = $"P-{System.Guid.NewGuid():N}",
            CategoryId = cat.CategoryId,
            ManufacturerId = manufacturerId,
            IsActive = isActive
        });
        db.SaveChanges();
    }
}
