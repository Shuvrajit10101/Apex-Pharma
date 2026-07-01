namespace ApexPharma.Domain.Entities;

/// <summary>
/// Immutable record of a sensitive action — every price change, stock adjustment,
/// and deletion (who/what/when) (plan.md §4, §14). Stores before/after snapshots as
/// JSON so any entity type can be audited without a schema change.
/// </summary>
public class AuditLog
{
    public int LogId { get; set; }

    /// <summary>FK to the acting <see cref="User"/>.</summary>
    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>What was done, e.g. "Update", "Delete", "PriceChange".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The entity type affected, e.g. "Product", "Batch".</summary>
    public string Entity { get; set; } = string.Empty;

    /// <summary>The affected entity's primary key.</summary>
    public int EntityId { get; set; }

    /// <summary>State before the change, serialized as JSON (nullable for creates).</summary>
    public string? BeforeJson { get; set; }

    /// <summary>State after the change, serialized as JSON (nullable for deletes).</summary>
    public string? AfterJson { get; set; }

    public DateTime Timestamp { get; set; }
}
