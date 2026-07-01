namespace ApexPharma.Domain.Entities;

/// <summary>
/// A named permission set assigned to users (plan.md §7.2). Roles are stored (not
/// only the <see cref="Enums.UserRole"/> enum) so an Owner can, in future, tune
/// per-role permissions without a code change — the granular permissions live in
/// <see cref="PermissionsJson"/>.
/// </summary>
public class Role
{
    public int RoleId { get; set; }

    /// <summary>Display name, e.g. "Owner", "Pharmacist", "Cashier".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// JSON blob describing the fine-grained permissions granted to this role.
    /// Kept as JSON so the permission surface can evolve without schema changes
    /// (RBAC is a hard requirement — plan.md §4, §14).
    /// </summary>
    public string? PermissionsJson { get; set; }

    /// <summary>Users assigned to this role.</summary>
    public ICollection<User> Users { get; set; } = new List<User>();
}
