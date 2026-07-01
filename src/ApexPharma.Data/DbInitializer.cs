using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Data;

/// <summary>
/// Idempotent database seeding (plan.md §4, §5 — Phase 0/1 setup). Ensures the three
/// RBAC roles exist and, on a fresh install with no users, creates a single Owner
/// account so the pharmacy can sign in and configure everything else.
/// <para>
/// The default Owner password (<see cref="DefaultOwnerPassword"/>) is a well-known
/// bootstrap credential that MUST be changed on first login — it is surfaced to the
/// operator, never treated as a real secret. Password hashing is injected as a
/// delegate so this data-layer type stays free of a reference to the application
/// layer's <c>AuthService</c> (which references Data, not the other way round).
/// </para>
/// </summary>
public static class DbInitializer
{
    /// <summary>The three RBAC roles from plan.md §4, in privilege order.</summary>
    private static readonly string[] RoleNames =
    {
        nameof(UserRole.Owner),
        nameof(UserRole.Pharmacist),
        nameof(UserRole.Cashier)
    };

    /// <summary>Default bootstrap Owner username created on a fresh install.</summary>
    public const string DefaultOwnerUsername = "admin";

    /// <summary>
    /// Default bootstrap Owner password. Reported to the operator and to be changed
    /// immediately on first login — it is not a secret, just a first-run convenience.
    /// </summary>
    public const string DefaultOwnerPassword = "Admin@123";

    /// <summary>
    /// Ensures roles exist and seeds one Owner if there are no users at all. Safe to
    /// call on every startup — running it twice does not duplicate rows.
    /// </summary>
    /// <param name="db">The context to seed.</param>
    /// <param name="passwordHasher">Hashes the default password (e.g. AuthService.HashPassword).</param>
    /// <param name="cancellationToken">Cancels the seeding operation.</param>
    public static async Task SeedAsync(
        ApexPharmaDbContext db,
        Func<string, string> passwordHasher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(passwordHasher);

        // 1) Ensure the three roles exist (idempotent — only add the missing ones).
        var existingRoleNames = await db.Roles
            .Select(r => r.Name)
            .ToListAsync(cancellationToken);

        var missing = RoleNames
            .Where(name => !existingRoleNames.Contains(name))
            .Select(name => new Role { Name = name })
            .ToList();

        if (missing.Count > 0)
        {
            await db.Roles.AddRangeAsync(missing, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        // 2) Only bootstrap an Owner when the install is truly empty (no users).
        bool anyUsers = await db.Users.AnyAsync(cancellationToken);
        if (anyUsers)
        {
            return;
        }

        Role ownerRole = await db.Roles
            .FirstAsync(r => r.Name == nameof(UserRole.Owner), cancellationToken);

        var owner = new User
        {
            Username = DefaultOwnerUsername,
            PasswordHash = passwordHasher(DefaultOwnerPassword),
            FullName = "Pharmacy Owner",
            RoleId = ownerRole.RoleId,
            IsActive = true
        };

        await db.Users.AddAsync(owner, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
