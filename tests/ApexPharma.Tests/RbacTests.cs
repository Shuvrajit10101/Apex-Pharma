using System;
using System.Linq;
using ApexPharma.Application.Services;
using ApexPharma.Domain.Enums;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// Full RBAC permission-matrix tests (plan.md §4). Every role is checked against every
/// permission so a change to the matrix can never silently widen or narrow access:
/// Owner = all; Pharmacist = billing/purchases/adjust/returns/reports/stock/day-end/
/// products but NOT prices/users/settings/backup; Cashier = billing/view-stock/day-end
/// only.
/// </summary>
public class RbacTests
{
    private readonly IAuthService _sut;

    public RbacTests()
    {
        // HasPermission is pure logic; a throwaway context satisfies the ctor.
        _sut = new AuthService(new SqliteInMemoryContext().Context);
    }

    [Fact]
    public void Owner_HasEveryPermission()
    {
        foreach (Permission p in Enum.GetValues<Permission>())
        {
            Assert.True(_sut.HasPermission(UserRole.Owner, p), $"Owner should have {p}");
        }
    }

    [Theory]
    // Pharmacist — GRANTED (plan.md §4)
    [InlineData(Permission.DoBilling, true)]
    [InlineData(Permission.DoPurchases, true)]
    [InlineData(Permission.AdjustStock, true)]
    [InlineData(Permission.DoReturns, true)]
    [InlineData(Permission.ViewReports, true)]
    [InlineData(Permission.ViewStock, true)]
    [InlineData(Permission.DayEnd, true)]
    [InlineData(Permission.ManageProducts, true)]
    // Pharmacist — DENIED (plan.md §4: not prices, users, settings, backup)
    [InlineData(Permission.EditPrices, false)]
    [InlineData(Permission.ManageUsers, false)]
    [InlineData(Permission.ManageSettings, false)]
    [InlineData(Permission.Backup, false)]
    public void Pharmacist_MatrixIsEnforced(Permission permission, bool expected)
    {
        Assert.Equal(expected, _sut.HasPermission(UserRole.Pharmacist, permission));
    }

    [Theory]
    // Cashier — GRANTED (plan.md §4: billing, view stock/price, day-end only)
    [InlineData(Permission.DoBilling, true)]
    [InlineData(Permission.ViewStock, true)]
    [InlineData(Permission.DayEnd, true)]
    // Cashier — DENIED (everything else)
    [InlineData(Permission.DoPurchases, false)]
    [InlineData(Permission.AdjustStock, false)]
    [InlineData(Permission.DoReturns, false)]
    [InlineData(Permission.ViewReports, false)]
    [InlineData(Permission.ManageProducts, false)]
    [InlineData(Permission.EditPrices, false)]
    [InlineData(Permission.ManageUsers, false)]
    [InlineData(Permission.ManageSettings, false)]
    [InlineData(Permission.Backup, false)]
    public void Cashier_MatrixIsEnforced(Permission permission, bool expected)
    {
        Assert.Equal(expected, _sut.HasPermission(UserRole.Cashier, permission));
    }

    [Fact]
    public void Cashier_HasStrictlyFewerPermissions_ThanPharmacist_ThanOwner()
    {
        int owner = CountGranted(UserRole.Owner);
        int pharmacist = CountGranted(UserRole.Pharmacist);
        int cashier = CountGranted(UserRole.Cashier);

        Assert.True(cashier < pharmacist, "Cashier must have fewer permissions than Pharmacist");
        Assert.True(pharmacist < owner, "Pharmacist must have fewer permissions than Owner");
        Assert.Equal(Enum.GetValues<Permission>().Length, owner);
    }

    private int CountGranted(UserRole role) =>
        Enum.GetValues<Permission>().Count(p => _sut.HasPermission(role, p));
}
