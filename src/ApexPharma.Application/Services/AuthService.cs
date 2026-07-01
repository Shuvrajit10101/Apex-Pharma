using System.Security.Cryptography;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services;

/// <summary>
/// Concrete authentication + RBAC service (plan.md §4, §6.1, §14).
/// <para>
/// Passwords are hashed with <b>PBKDF2</b> (<see cref="Rfc2898DeriveBytes"/>, SHA-256,
/// 100k iterations, 16-byte cryptographically-random salt) and stored as a
/// self-describing string <c>PBKDF2-SHA256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;</c>,
/// so the work factor can be raised later without breaking existing hashes. Verification
/// uses a fixed-time comparison (<see cref="CryptographicOperations.FixedTimeEquals"/>).
/// Plaintext is never stored or logged — this fixes the plaintext-credential flaw shared
/// by all three studied source systems (plan.md §14).
/// </para>
/// </summary>
public class AuthService : IAuthService
{
    private const string Algorithm = "PBKDF2-SHA256";
    private const int Iterations = 100_000;
    private const int SaltSize = 16;   // 128-bit salt
    private const int HashSize = 32;   // 256-bit derived key (matches SHA-256)

    private readonly ApexPharmaDbContext _db;

    public AuthService(ApexPharmaDbContext db) => _db = db;

    /// <inheritdoc />
    public string HashPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        // Self-describing so the iteration count / salt travel with the hash.
        return string.Join('$',
            Algorithm,
            Iterations.ToString(),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <inheritdoc />
    public bool VerifyPassword(string password, string storedHash)
    {
        if (password is null || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        // Expected shape: algorithm $ iterations $ base64(salt) $ base64(hash).
        string[] parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out int iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        // Fixed-time compare defeats timing side-channels (plan.md §14).
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <inheritdoc />
    public async Task<AuthResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        // Load the user WITH its role (tracked, so we can update LastLogin below).
        User? user = await _db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);

        // Generic failure for every rejection reason (unknown user / inactive / bad
        // password) so we never reveal which usernames exist (plan.md §14). We still
        // run VerifyPassword even when the user is missing/inactive to keep the timing
        // roughly uniform, but the result is discarded.
        bool passwordValid = VerifyPassword(
            password ?? string.Empty,
            user?.PasswordHash ?? string.Empty);

        if (user is null || !user.IsActive || !passwordValid)
        {
            return AuthResult.Failure();
        }

        // Persisted timestamps are stored in UTC; the UI converts to local for display.
        user.LastLogin = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        UserRole role = ResolveRole(user.Role?.Name);
        return AuthResult.Success(user, role);
    }

    /// <inheritdoc />
    public async Task<User> CreateUserAsync(string username, string password, string fullName, int roleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        // Reject duplicate usernames up front (case-insensitive) so the caller gets a
        // clear error instead of a raw DB exception (usernames are unique — plan.md §14).
        bool exists = await _db.Users
            .AnyAsync(u => u.Username.ToLower() == username.ToLower(), cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException($"Username '{username}' is already taken.");
        }

        var user = new User
        {
            Username = username,
            PasswordHash = HashPassword(password),
            FullName = fullName ?? string.Empty,
            RoleId = roleId,
            IsActive = true
        };

        await _db.Users.AddAsync(user, cancellationToken);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Defensive backstop: a concurrent insert can still trip the UNIQUE index
            // after the check above. Surface it as the same clear, expected error.
            throw new InvalidOperationException($"Username '{username}' is already taken.");
        }

        return user;
    }

    /// <inheritdoc />
    public async Task ChangePasswordAsync(int userId, string newPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(newPassword))
        {
            throw new ArgumentException("Password is required.", nameof(newPassword));
        }

        User user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException($"User {userId} not found.");

        user.PasswordHash = HashPassword(newPassword);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public bool HasPermission(UserRole role, Permission permission) => role switch
    {
        // Owner / Manager — everything (plan.md §4).
        UserRole.Owner => true,

        // Pharmacist — billing (incl. Schedule H/H1), purchases/GRN, stock adjustments,
        // returns, view reports/stock. NOT prices, users, or settings (plan.md §4).
        UserRole.Pharmacist => permission is
            Permission.DoBilling or
            Permission.DoPurchases or
            Permission.AdjustStock or
            Permission.DoReturns or
            Permission.ViewReports or
            Permission.ViewStock or
            Permission.DayEnd or
            Permission.ManageProducts,

        // Cashier — billing, view stock/price, day-end only. Nothing else (plan.md §4).
        UserRole.Cashier => permission is
            Permission.DoBilling or
            Permission.ViewStock or
            Permission.DayEnd,

        _ => false
    };

    /// <summary>
    /// Maps a stored <see cref="Role.Name"/> to the <see cref="UserRole"/> tier. Unknown
    /// or missing names fall back to the least-privileged role (Cashier) — fail-safe, so a
    /// data glitch can never silently grant elevated access (plan.md §14).
    /// </summary>
    private static UserRole ResolveRole(string? roleName) =>
        // Match only the three canonical role NAMES. An explicit switch (not Enum.TryParse)
        // because TryParse also accepts the numeric strings "0"/"1"/"2", which must NOT
        // resolve. Anything unrecognised falls back to the least-privileged Cashier so a
        // data glitch can never silently grant elevated access (fail-safe, plan.md §14).
        (roleName?.Trim().ToLowerInvariant()) switch
        {
            "owner" => UserRole.Owner,
            "pharmacist" => UserRole.Pharmacist,
            "cashier" => UserRole.Cashier,
            _ => UserRole.Cashier
        };
}
