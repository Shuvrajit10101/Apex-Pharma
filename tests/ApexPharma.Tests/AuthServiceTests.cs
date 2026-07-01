using ApexPharma.Application.Services;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// Authentication + password-hashing tests (plan.md §6.1, §14). These guard the
/// security-critical behaviour: PBKDF2 hashing is salted and verifiable, verification
/// is correct, and login rejects unknown/inactive/bad-password attempts with a generic
/// failure while updating <c>last_login</c> on success.
/// </summary>
public class AuthServiceTests : IDisposable
{
    // The pure hashing/format tests need a context only to satisfy the ctor. The class
    // owns one disposable fixture (rather than leaking a new open connection per test)
    // and disposes it below.
    private readonly SqliteInMemoryContext _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // ---- Hashing ---------------------------------------------------------

    [Fact]
    public void HashPassword_ThenVerify_RoundTrips()
    {
        var sut = new AuthService(NullContext());
        string hash = sut.HashPassword("Admin@123");

        Assert.True(sut.VerifyPassword("Admin@123", hash));
    }

    [Fact]
    public void HashPassword_IsSelfDescribing_Pbkdf2Format()
    {
        var sut = new AuthService(NullContext());
        string hash = sut.HashPassword("secret");

        // algorithm $ iterations $ salt $ hash
        string[] parts = hash.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("PBKDF2-SHA256", parts[0]);
        Assert.Equal("100000", parts[1]);
        // Never store the plaintext anywhere in the hash string.
        Assert.DoesNotContain("secret", hash);
    }

    [Fact]
    public void HashPassword_SamePasswordTwice_ProducesDifferentHashes()
    {
        var sut = new AuthService(NullContext());

        string a = sut.HashPassword("samePassword");
        string b = sut.HashPassword("samePassword");

        // Random per-hash salt ⇒ different strings, yet both verify.
        Assert.NotEqual(a, b);
        Assert.True(sut.VerifyPassword("samePassword", a));
        Assert.True(sut.VerifyPassword("samePassword", b));
    }

    [Fact]
    public void VerifyPassword_WrongPassword_ReturnsFalse()
    {
        var sut = new AuthService(NullContext());
        string hash = sut.HashPassword("correct-horse");

        Assert.False(sut.VerifyPassword("wrong-horse", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-valid-format")]
    [InlineData("PBKDF2-SHA256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("BADALGO$100000$c2FsdA==$aGFzaA==")]
    [InlineData("PBKDF2-SHA256$100000$%%%$aGFzaA==")]
    public void VerifyPassword_MalformedStoredValue_ReturnsFalse(string stored)
    {
        var sut = new AuthService(NullContext());

        Assert.False(sut.VerifyPassword("anything", stored));
    }

    // ---- Login -----------------------------------------------------------

    [Fact]
    public async Task Login_ValidCredentials_SucceedsAndUpdatesLastLogin()
    {
        using var fixture = new SqliteInMemoryContext();
        var sut = new AuthService(fixture.Context);
        SeedRoles(fixture.Context);
        var owner = SeedUser(fixture.Context, sut, "admin", "Admin@123", UserRole.Owner);

        Assert.Null(owner.LastLogin);

        AuthResult result = await sut.LoginAsync("admin", "Admin@123");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.User);
        Assert.Equal(UserRole.Owner, result.Role);
        Assert.NotNull(result.User!.LastLogin);

        // Persisted, not just tracked in memory.
        var reloaded = await fixture.NewContext().Users.FirstAsync(u => u.Username == "admin");
        Assert.NotNull(reloaded.LastLogin);
    }

    [Fact]
    public async Task Login_WrongPassword_FailsGenerically()
    {
        using var fixture = new SqliteInMemoryContext();
        var sut = new AuthService(fixture.Context);
        SeedRoles(fixture.Context);
        SeedUser(fixture.Context, sut, "admin", "Admin@123", UserRole.Owner);

        AuthResult result = await sut.LoginAsync("admin", "wrong-password");

        Assert.False(result.Succeeded);
        Assert.Null(result.User);
        Assert.Equal("Invalid username or password.", result.Error);
    }

    [Fact]
    public async Task Login_UnknownUser_Fails()
    {
        using var fixture = new SqliteInMemoryContext();
        var sut = new AuthService(fixture.Context);
        SeedRoles(fixture.Context);

        AuthResult result = await sut.LoginAsync("ghost", "whatever");

        Assert.False(result.Succeeded);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task Login_InactiveUser_IsDenied()
    {
        using var fixture = new SqliteInMemoryContext();
        var sut = new AuthService(fixture.Context);
        SeedRoles(fixture.Context);
        var user = SeedUser(fixture.Context, sut, "ex-staff", "Right@123", UserRole.Cashier);
        user.IsActive = false;
        fixture.Context.SaveChanges();

        AuthResult result = await sut.LoginAsync("ex-staff", "Right@123");

        // Correct password, but the account is disabled ⇒ denied.
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Login_ResolvesRoleFromRoleName()
    {
        using var fixture = new SqliteInMemoryContext();
        var sut = new AuthService(fixture.Context);
        SeedRoles(fixture.Context);
        SeedUser(fixture.Context, sut, "cash", "Pass@123", UserRole.Cashier);

        AuthResult result = await sut.LoginAsync("cash", "Pass@123");

        Assert.True(result.Succeeded);
        Assert.Equal(UserRole.Cashier, result.Role);
    }

    // ---- CreateUser ------------------------------------------------------

    [Fact]
    public async Task CreateUser_DuplicateUsername_Fails()
    {
        using var fixture = new SqliteInMemoryContext();
        var sut = new AuthService(fixture.Context);
        SeedRoles(fixture.Context);
        int roleId = fixture.Context.Roles.First(r => r.Name == nameof(UserRole.Cashier)).RoleId;

        await sut.CreateUserAsync("dupe", "First@123", "First User", roleId);

        // A second user with the same username must be rejected (unique username, plan.md §14).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CreateUserAsync("dupe", "Second@123", "Second User", roleId));

        // And only the original row exists.
        Assert.Equal(1, await fixture.Context.Users.CountAsync(u => u.Username == "dupe"));
    }

    [Fact]
    public async Task CreateUser_DuplicateUsername_IsCaseInsensitive()
    {
        using var fixture = new SqliteInMemoryContext();
        var sut = new AuthService(fixture.Context);
        SeedRoles(fixture.Context);
        int roleId = fixture.Context.Roles.First(r => r.Name == nameof(UserRole.Cashier)).RoleId;

        await sut.CreateUserAsync("Staff", "First@123", "First", roleId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CreateUserAsync("staff", "Second@123", "Second", roleId));
    }

    // ---- Helpers ---------------------------------------------------------

    /// <summary>
    /// AuthService needs a context only for DB-backed calls; the pure hashing tests
    /// never touch it. Hands back the class-owned fixture's context so the ctor is
    /// satisfied without leaking a fresh open connection per call.
    /// </summary>
    private ApexPharmaDbContext NullContext() => _fixture.Context;

    private static void SeedRoles(ApexPharmaDbContext db)
    {
        db.Roles.AddRange(
            new Role { Name = nameof(UserRole.Owner) },
            new Role { Name = nameof(UserRole.Pharmacist) },
            new Role { Name = nameof(UserRole.Cashier) });
        db.SaveChanges();
    }

    private static User SeedUser(
        ApexPharmaDbContext db,
        IAuthService auth,
        string username,
        string password,
        UserRole role)
    {
        int roleId = db.Roles.First(r => r.Name == role.ToString()).RoleId;
        var user = new User
        {
            Username = username,
            PasswordHash = auth.HashPassword(password),
            FullName = username,
            RoleId = roleId,
            IsActive = true
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }
}
