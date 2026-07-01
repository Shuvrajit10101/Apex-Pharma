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
/// CustomerService tests (plan.md §6.1, §7.2). Cover create/update/list/search, required-name
/// and non-negative-credit-limit validation, and RBAC (DoBilling can manage; a role without it
/// is refused). The khata balance is billing's job, so it is never editable here.
/// </summary>
public class CustomerServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly CustomerService _sut;

    public CustomerServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        _sut = new CustomerService(_fixture.Context, auth);
    }

    public void Dispose() => _fixture.Dispose();

    private static CustomerInput Input(string name = "Ravi Kumar", string? phone = "9998887777", decimal creditLimit = 500m)
        => new() { Name = name, Phone = phone, CreditLimit = creditLimit };

    [Fact]
    public async Task Create_Succeeds_WithZeroBalance()
    {
        var result = await _sut.CreateAsync(Input(), UserRole.Cashier);

        Assert.True(result.Succeeded);
        Assert.Equal("Ravi Kumar", result.Value!.Name);
        Assert.Equal(0m, result.Value.Balance);
        Assert.Equal(500m, result.Value.CreditLimit);
    }

    [Fact]
    public async Task Create_RequiresName()
    {
        var result = await _sut.CreateAsync(Input(name: "   "), UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("name is required", result.Error!);
    }

    [Fact]
    public async Task Create_RejectsNegativeCreditLimit()
    {
        var result = await _sut.CreateAsync(Input(creditLimit: -1m), UserRole.Owner);

        Assert.False(result.Succeeded);
        Assert.Contains("Credit limit", result.Error!);
    }

    [Fact]
    public async Task Create_WithoutDoBilling_IsRefused()
    {
        var result = await _sut.CreateAsync(Input(), (UserRole)999);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
        Assert.Equal(0, await _fixture.NewContext().Customers.CountAsync());
    }

    [Fact]
    public async Task Update_ChangesFields_ButNotBalance()
    {
        var created = (await _sut.CreateAsync(Input(), UserRole.Owner)).Value!;
        // Simulate an outstanding khata balance accrued by billing.
        var db = _fixture.Context;
        var tracked = await db.Customers.SingleAsync(c => c.CustomerId == created.CustomerId);
        tracked.Balance = 320m;
        await db.SaveChangesAsync();

        var result = await _sut.UpdateAsync(created.CustomerId, Input(name: "Ravi K", creditLimit: 900m), UserRole.Owner);

        Assert.True(result.Succeeded);
        var reloaded = await _fixture.NewContext().Customers.SingleAsync(c => c.CustomerId == created.CustomerId);
        Assert.Equal("Ravi K", reloaded.Name);
        Assert.Equal(900m, reloaded.CreditLimit);
        Assert.Equal(320m, reloaded.Balance); // balance untouched by the update
    }

    [Fact]
    public async Task Search_MatchesNameOrPhone()
    {
        await _sut.CreateAsync(Input(name: "Ravi Kumar", phone: "9990001111"), UserRole.Owner);
        await _sut.CreateAsync(Input(name: "Sita Devi", phone: "8887776666"), UserRole.Owner);

        var byName = await _sut.SearchAsync("ravi");
        Assert.Single(byName);
        Assert.Equal("Ravi Kumar", byName[0].Name);

        var byPhone = await _sut.SearchAsync("8887");
        Assert.Single(byPhone);
        Assert.Equal("Sita Devi", byPhone[0].Name);

        var all = await _sut.SearchAsync("   ");
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Get_ReturnsCustomerWithBalance()
    {
        var created = (await _sut.CreateAsync(Input(), UserRole.Owner)).Value!;
        var fetched = await _sut.GetAsync(created.CustomerId);

        Assert.NotNull(fetched);
        Assert.Equal(created.CustomerId, fetched!.CustomerId);
    }
}
