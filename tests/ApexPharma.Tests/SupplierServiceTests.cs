using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// SupplierService tests (plan.md §6.1, §7.2). Create/update/list/search/deactivate happy
/// paths plus validation (name required, GSTIN format) and RBAC rejection.
/// </summary>
public class SupplierServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly ISupplierService _sut;

    public SupplierServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        _sut = new SupplierService(_fixture.Context, auth);
    }

    public void Dispose() => _fixture.Dispose();

    private static SupplierInput Valid(string name = "MediDistributors") => new()
    {
        Name = name,
        Gstin = "27AAPFU0939F1ZV",
        DlNumber = "20B-1234",
        Phone = "9876543210",
        Email = "sales@medi.example",
        Address = "12 Market Rd",
        StateCode = "27",
        OpeningBalance = 1500.50m
    };

    [Fact]
    public async Task Create_Valid_PersistsAllFields()
    {
        var result = await _sut.CreateAsync(Valid(), UserRole.Owner);

        Assert.True(result.Succeeded);
        var s = result.Value!;
        Assert.Equal("MediDistributors", s.Name);
        Assert.Equal("27AAPFU0939F1ZV", s.Gstin);
        Assert.Equal("20B-1234", s.DlNumber);
        Assert.Equal(1500.50m, s.OpeningBalance);
        Assert.True(s.IsActive);
    }

    [Fact]
    public async Task Create_WithoutGstin_Succeeds()
    {
        var input = Valid();
        input.Gstin = null;

        var result = await _sut.CreateAsync(input, UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Null(result.Value!.Gstin);
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
    [InlineData("NOTAGSTIN")]
    [InlineData("27AAPFU0939F1Z")]     // 14 chars — too short
    [InlineData("27AAPFU0939F1ZVX")]   // 16 chars — too long
    [InlineData("2XAAPFU0939F1ZV")]    // non-digit state code
    public async Task Create_InvalidGstin_Fails(string gstin)
    {
        var input = Valid();
        input.Gstin = gstin;

        var result = await _sut.CreateAsync(input, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("GSTIN", result.Error!);
    }

    [Fact]
    public async Task Create_WithoutStateCode_Succeeds()
    {
        var input = Valid();
        input.StateCode = null;

        var result = await _sut.CreateAsync(input, UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Null(result.Value!.StateCode);
    }

    [Fact]
    public async Task Create_ValidStateCode_Succeeds()
    {
        var input = Valid();
        input.StateCode = "27"; // Maharashtra

        var result = await _sut.CreateAsync(input, UserRole.Owner);

        Assert.True(result.Succeeded);
        Assert.Equal("27", result.Value!.StateCode);
    }

    [Theory]
    [InlineData("99")]  // out of the 01–37 range
    [InlineData("00")]  // below the valid range
    [InlineData("1")]   // single digit, not two
    [InlineData("AB")]  // non-numeric
    public async Task Create_InvalidStateCode_Fails(string stateCode)
    {
        var input = Valid();
        input.StateCode = stateCode;

        var result = await _sut.CreateAsync(input, UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("State code", result.Error!);
    }

    [Fact]
    public async Task Update_ChangesFields()
    {
        var created = await _sut.CreateAsync(Valid(), UserRole.Owner);
        var edit = Valid("Renamed Supplier");
        edit.Phone = "1112223334";

        var result = await _sut.UpdateAsync(created.Value!.SupplierId, edit, UserRole.Owner);

        Assert.True(result.Succeeded);
        var reloaded = await _fixture.NewContext().Suppliers.FirstAsync(x => x.SupplierId == created.Value.SupplierId);
        Assert.Equal("Renamed Supplier", reloaded.Name);
        Assert.Equal("1112223334", reloaded.Phone);
    }

    [Fact]
    public async Task Update_MissingId_Fails()
    {
        var result = await _sut.UpdateAsync(999, Valid(), UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.Error!);
    }

    [Fact]
    public async Task Search_ByNameFragment_ReturnsMatches()
    {
        await _sut.CreateAsync(Valid("Apex Traders"), UserRole.Owner);
        await _sut.CreateAsync(Valid("Zenith Meds"), UserRole.Owner);

        var results = await _sut.SearchAsync("apex");

        Assert.Single(results);
        Assert.Equal("Apex Traders", results[0].Name);
    }

    [Fact]
    public async Task Deactivate_HidesFromListAndSearch()
    {
        var created = await _sut.CreateAsync(Valid("ToDeactivate"), UserRole.Owner);

        await _sut.DeactivateAsync(created.Value!.SupplierId, UserRole.Owner);

        Assert.Empty(await _sut.ListAsync());
        Assert.Empty(await _sut.SearchAsync("ToDeactivate"));
    }

    [Fact]
    public async Task Create_AsCashier_IsRejected()
    {
        var result = await _sut.CreateAsync(Valid(), UserRole.Cashier);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
    }

    [Fact]
    public async Task Pharmacist_CanManageSuppliers()
    {
        var result = await _sut.CreateAsync(Valid("PharmSupplier"), UserRole.Pharmacist);

        Assert.True(result.Succeeded);
    }
}
