namespace ApexPharma.Domain.Entities;

/// <summary>
/// A person who signs in to the application (plan.md §7.2). Credentials are stored
/// as a salted hash only — never plaintext — which fixes the #1 flaw in all three
/// studied source systems (plan.md §14, NFR security §6.2).
/// </summary>
public class User
{
    public int UserId { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// PBKDF2/bcrypt hash of the password (with embedded salt/iterations). Plaintext
    /// passwords are never persisted anywhere in the system.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    /// <summary>FK to the assigned <see cref="Role"/>.</summary>
    public int RoleId { get; set; }
    public Role? Role { get; set; }

    /// <summary>Soft-disable a user without deleting history (audit trail must survive).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Last successful sign-in — surfaced for the audit trail and session UX.</summary>
    public DateTime? LastLogin { get; set; }

    // Transactional history created by this user (delete-restricted to preserve records).
    public ICollection<Sale> Sales { get; set; } = new List<Sale>();
    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();
    public ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();
    public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
}
