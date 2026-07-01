using System.Linq;
using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Data;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// Seeding tests (plan.md §4, §5). The seeder must be idempotent — running it on every
/// startup can never duplicate roles or users — and on a truly fresh install it must
/// create the three RBAC roles plus exactly one Owner that can actually sign in.
/// </summary>
public class DbInitializerTests
{
    private static AuthService NewAuth(ApexPharmaDbContext db) => new(db);

    [Fact]
    public async Task Seed_CreatesThreeRoles_AndOneOwner()
    {
        using var fixture = new SqliteInMemoryContext();
        var db = fixture.Context;
        var auth = NewAuth(db);

        await DbInitializer.SeedAsync(db, auth.HashPassword);

        var roleNames = await db.Roles.Select(r => r.Name).OrderBy(n => n).ToListAsync();
        Assert.Equal(
            new[] { nameof(UserRole.Cashier), nameof(UserRole.Owner), nameof(UserRole.Pharmacist) },
            roleNames);

        Assert.Equal(1, await db.Users.CountAsync());
        var owner = await db.Users.Include(u => u.Role).SingleAsync();
        Assert.Equal(DbInitializer.DefaultOwnerUsername, owner.Username);
        Assert.Equal(nameof(UserRole.Owner), owner.Role!.Name);
        Assert.True(owner.IsActive);
    }

    [Fact]
    public async Task Seed_OwnerCanLoginWithDefaultPassword_AndHashIsNotPlaintext()
    {
        using var fixture = new SqliteInMemoryContext();
        var db = fixture.Context;
        var auth = NewAuth(db);

        await DbInitializer.SeedAsync(db, auth.HashPassword);

        var owner = await db.Users.SingleAsync();
        Assert.NotEqual(DbInitializer.DefaultOwnerPassword, owner.PasswordHash);
        Assert.DoesNotContain(DbInitializer.DefaultOwnerPassword, owner.PasswordHash);

        AuthResult login = await auth.LoginAsync(
            DbInitializer.DefaultOwnerUsername,
            DbInitializer.DefaultOwnerPassword);
        Assert.True(login.Succeeded);
        Assert.Equal(UserRole.Owner, login.Role);
    }

    [Fact]
    public async Task Seed_RunTwice_IsIdempotent()
    {
        using var fixture = new SqliteInMemoryContext();
        var db = fixture.Context;
        var auth = NewAuth(db);

        await DbInitializer.SeedAsync(db, auth.HashPassword);
        await DbInitializer.SeedAsync(db, auth.HashPassword);

        // No duplicate roles or users on a second run.
        Assert.Equal(3, await db.Roles.CountAsync());
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Seed_WhenUsersExist_DoesNotAddAnotherOwner()
    {
        using var fixture = new SqliteInMemoryContext();
        var db = fixture.Context;
        var auth = NewAuth(db);

        // First seed creates the bootstrap Owner.
        await DbInitializer.SeedAsync(db, auth.HashPassword);
        // The Owner then changes their password — simulate an established install.
        var owner = await db.Users.SingleAsync();
        await auth.ChangePasswordAsync(owner.UserId, "Rotated@456");

        // Re-seeding must not resurrect the default account or add users.
        await DbInitializer.SeedAsync(db, auth.HashPassword);

        Assert.Equal(1, await db.Users.CountAsync());
        AuthResult stillWorks = await auth.LoginAsync(DbInitializer.DefaultOwnerUsername, "Rotated@456");
        Assert.True(stillWorks.Succeeded);
        // The original default password no longer works (proves the row wasn't reset).
        AuthResult oldFails = await auth.LoginAsync(
            DbInitializer.DefaultOwnerUsername,
            DbInitializer.DefaultOwnerPassword);
        Assert.False(oldFails.Succeeded);
    }

    [Fact]
    public async Task Seed_WithPreExistingRoles_DoesNotDuplicateThem()
    {
        using var fixture = new SqliteInMemoryContext();
        var db = fixture.Context;
        var auth = NewAuth(db);

        // Owner role already present (e.g. from a prior partial run).
        db.Roles.Add(new Domain.Entities.Role { Name = nameof(UserRole.Owner) });
        await db.SaveChangesAsync();

        await DbInitializer.SeedAsync(db, auth.HashPassword);

        Assert.Equal(1, await db.Roles.CountAsync(r => r.Name == nameof(UserRole.Owner)));
        Assert.Equal(3, await db.Roles.CountAsync());
    }
}
