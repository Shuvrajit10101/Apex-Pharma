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
/// CategoryService tests (plan.md §6.1). Happy paths (create/rename/list/deactivate) plus
/// validation (blank + duplicate name, case-insensitive) and RBAC rejection.
/// </summary>
public class CategoryServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly ICategoryService _sut;

    public CategoryServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        _sut = new CategoryService(_fixture.Context, auth);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Create_Valid_Succeeds()
    {
        var result = await _sut.CreateAsync("Vitamins", UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Equal("Vitamins", result.Value!.Name);
        Assert.True(result.Value.IsActive);
    }

    [Fact]
    public async Task Create_TrimsWhitespace()
    {
        var result = await _sut.CreateAsync("  Health Products  ", UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Equal("Health Products", result.Value!.Name);
    }

    [Fact]
    public async Task Create_BlankName_Fails()
    {
        var result = await _sut.CreateAsync("   ", UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("required", result.Error!);
    }

    [Fact]
    public async Task Create_DuplicateName_CaseInsensitive_Fails()
    {
        await _sut.CreateAsync("Medication", UserRole.Owner);

        var dupe = await _sut.CreateAsync("medication", UserRole.Owner);

        Assert.False(dupe.Succeeded);
        Assert.Contains("already exists", dupe.Error!);
        Assert.Equal(1, await _fixture.Context.Categories.CountAsync());
    }

    [Fact]
    public async Task Rename_Valid_Succeeds()
    {
        var created = await _sut.CreateAsync("Old", UserRole.Owner);

        var renamed = await _sut.RenameAsync(created.Value!.CategoryId, "New", UserRole.Owner);

        Assert.True(renamed.Succeeded);
        var reloaded = await _fixture.NewContext().Categories.FirstAsync(c => c.CategoryId == created.Value.CategoryId);
        Assert.Equal("New", reloaded.Name);
    }

    [Fact]
    public async Task Rename_ToExistingName_Fails()
    {
        await _sut.CreateAsync("Alpha", UserRole.Owner);
        var beta = await _sut.CreateAsync("Beta", UserRole.Owner);

        var result = await _sut.RenameAsync(beta.Value!.CategoryId, "alpha", UserRole.Owner);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Rename_SameName_SucceedsForSelf()
    {
        var created = await _sut.CreateAsync("Same", UserRole.Owner);

        // Renaming a row to its own current name must not trip the duplicate check.
        var result = await _sut.RenameAsync(created.Value!.CategoryId, "Same", UserRole.Owner);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Deactivate_RemovesFromDefaultList()
    {
        var created = await _sut.CreateAsync("Temp", UserRole.Owner);

        var result = await _sut.DeactivateAsync(created.Value!.CategoryId, UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Empty(await _sut.ListAsync());
        Assert.Single(await _sut.ListAsync(includeInactive: true));
    }

    [Fact]
    public async Task List_ReturnsActiveSortedByName()
    {
        await _sut.CreateAsync("Zeta", UserRole.Owner);
        await _sut.CreateAsync("Alpha", UserRole.Owner);

        var list = await _sut.ListAsync();

        Assert.Equal(new[] { "Alpha", "Zeta" }, list.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task Deactivate_Blocked_WhenActiveProductReferencesCategory()
    {
        var created = await _sut.CreateAsync("InUse", UserRole.Owner);
        AddProduct(created.Value!.CategoryId, isActive: true);

        var result = await _sut.DeactivateAsync(created.Value.CategoryId, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("active product", result.Error!);
        // Still active — the guard must not have flipped the flag.
        Assert.Single(await _sut.ListAsync());
    }

    [Fact]
    public async Task Deactivate_Allowed_WhenOnlyInactiveProductsReferenceCategory()
    {
        var created = await _sut.CreateAsync("Freed", UserRole.Owner);
        AddProduct(created.Value!.CategoryId, isActive: false);

        var result = await _sut.DeactivateAsync(created.Value.CategoryId, UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Empty(await _sut.ListAsync());
    }

    /// <summary>Inserts a product referencing the given category (and a fresh manufacturer).</summary>
    private void AddProduct(int categoryId, bool isActive)
    {
        var db = _fixture.Context;
        var man = new Manufacturer { Name = $"M-{System.Guid.NewGuid():N}" };
        db.Manufacturers.Add(man);
        db.SaveChanges();
        db.Products.Add(new Product
        {
            Name = $"P-{System.Guid.NewGuid():N}",
            CategoryId = categoryId,
            ManufacturerId = man.ManufacturerId,
            IsActive = isActive
        });
        db.SaveChanges();
    }

    [Theory]
    [InlineData(UserRole.Cashier)]
    public async Task Create_WithoutManageProducts_IsRejected(UserRole role)
    {
        var result = await _sut.CreateAsync("Blocked", role);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
        Assert.Equal(0, await _fixture.Context.Categories.CountAsync());
    }

    [Fact]
    public async Task Pharmacist_CanManageCategories()
    {
        var result = await _sut.CreateAsync("PharmOk", UserRole.Pharmacist);

        Assert.True(result.Succeeded);
    }
}
