using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.Navigation;

/// <summary>
/// The signed-in session (plan.md §4). A DI singleton set once at login so module
/// view-models resolved per-visit can attribute their mutations to the acting user —
/// e.g. the Purchase header's <c>CreatedBy</c> / audit trail — without threading the user
/// through every navigation call. The role also drives permission-gated UI.
/// </summary>
public interface ISessionContext
{
    /// <summary>The signed-in user's id (0 until login sets it).</summary>
    int UserId { get; }

    /// <summary>The signed-in user's role tier.</summary>
    UserRole Role { get; }

    /// <summary>Records the authenticated user for the session (called once after login).</summary>
    void SetUser(int userId, UserRole role);
}

/// <summary>Default mutable <see cref="ISessionContext"/> — a simple holder set at login.</summary>
public sealed class SessionContext : ISessionContext
{
    public int UserId { get; private set; }

    public UserRole Role { get; private set; }

    public void SetUser(int userId, UserRole role)
    {
        UserId = userId;
        Role = role;
    }
}
