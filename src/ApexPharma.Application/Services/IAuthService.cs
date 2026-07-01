using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services;

/// <summary>
/// Authentication and RBAC (plan.md §4, §6.1). Passwords are verified against a
/// PBKDF2 hash — never plaintext (plan.md §14). Callers gate sensitive actions on
/// <see cref="HasPermission"/> rather than on the role name directly.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Validates credentials. Returns a successful <see cref="AuthResult"/> carrying the
    /// user + role on success; otherwise a generic failure that does not reveal whether
    /// the username was unknown, inactive, or the password wrong (plan.md §14). Updates
    /// <see cref="User.LastLogin"/> on success.
    /// </summary>
    Task<AuthResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>Creates a user with a hashed password (Owner-only operation).</summary>
    Task<User> CreateUserAsync(string username, string password, string fullName, int roleId, CancellationToken cancellationToken = default);

    /// <summary>Changes a user's password (rehashes; e.g. forced first-login change).</summary>
    Task ChangePasswordAsync(int userId, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>Hashes a plaintext password into a self-describing PBKDF2 string.</summary>
    string HashPassword(string password);

    /// <summary>
    /// Verifies a plaintext password against a stored PBKDF2 string using a
    /// fixed-time comparison. Returns false for malformed stored values.
    /// </summary>
    bool VerifyPassword(string password, string storedHash);

    /// <summary>True if the given role is granted the given permission (plan.md §4 matrix).</summary>
    bool HasPermission(UserRole role, Permission permission);
}
