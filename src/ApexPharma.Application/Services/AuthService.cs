using ApexPharma.Domain.Entities;

namespace ApexPharma.Application.Services;

/// <summary>
/// Skeleton <see cref="IAuthService"/>. Implemented in Phase 0/1 (hashing, RBAC,
/// audit) under Security &amp; Compliance review (plan.md §14).
/// </summary>
public class AuthService : IAuthService
{
    public Task<User?> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<User> CreateUserAsync(string username, string password, string fullName, int roleId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task ChangePasswordAsync(int userId, string newPassword, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
