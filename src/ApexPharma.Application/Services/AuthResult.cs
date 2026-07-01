using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services;

/// <summary>
/// Outcome of a <see cref="IAuthService.LoginAsync"/> attempt (plan.md §6.1). On
/// success it carries the authenticated <see cref="User"/> and the resolved
/// <see cref="UserRole"/>; on failure it carries only a generic error — the reason
/// (unknown user vs wrong password vs inactive) is deliberately <b>not</b> revealed,
/// to avoid leaking which usernames exist (plan.md §14 security).
/// </summary>
public sealed class AuthResult
{
    private AuthResult(bool succeeded, User? user, UserRole role, string? error)
    {
        Succeeded = succeeded;
        User = user;
        Role = role;
        Error = error;
    }

    /// <summary>True when the credentials were valid and the account is active.</summary>
    public bool Succeeded { get; }

    /// <summary>The signed-in user (with <see cref="User.Role"/> loaded) when <see cref="Succeeded"/>.</summary>
    public User? User { get; }

    /// <summary>The authenticated user's role tier; only meaningful when <see cref="Succeeded"/>.</summary>
    public UserRole Role { get; }

    /// <summary>Generic, non-revealing message shown on failure (null on success).</summary>
    public string? Error { get; }

    internal static AuthResult Success(User user, UserRole role) => new(true, user, role, null);

    /// <summary>Generic failure — same message for every rejection reason on purpose.</summary>
    internal static AuthResult Failure() =>
        new(false, null, default, "Invalid username or password.");
}
