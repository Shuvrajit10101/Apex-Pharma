using ApexPharma.Domain.Entities;

namespace ApexPharma.Application.Services;

/// <summary>
/// Authentication and RBAC (plan.md §4, §6.1). Passwords are verified against a
/// PBKDF2/bcrypt hash — never plaintext (plan.md §14).
/// </summary>
public interface IAuthService
{
    /// <summary>Validates credentials and returns the user on success, else null.</summary>
    Task<User?> LoginAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>Creates a user with a hashed password (Owner-only operation).</summary>
    Task<User> CreateUserAsync(string username, string password, string fullName, int roleId, CancellationToken cancellationToken = default);

    /// <summary>Changes a user's password (rehashes; e.g. forced first-login change).</summary>
    Task ChangePasswordAsync(int userId, string newPassword, CancellationToken cancellationToken = default);
}
