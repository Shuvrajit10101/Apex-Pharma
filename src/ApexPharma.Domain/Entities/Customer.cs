using System.ComponentModel.DataAnnotations.Schema;

namespace ApexPharma.Domain.Entities;

/// <summary>
/// A patient/customer (plan.md §7.2). Optional on a sale (walk-in cash sales need
/// no customer). Captured when needed for credit (khata), returns, or refill
/// reminders. <see cref="Balance"/> tracks outstanding credit.
/// </summary>
public class Customer
{
    public int CustomerId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Phone { get; set; }
    public string? Address { get; set; }

    /// <summary>Maximum credit the store will extend to this customer.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal CreditLimit { get; set; }

    /// <summary>Current outstanding balance owed by the customer (khata).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Balance { get; set; }

    public ICollection<Sale> Sales { get; set; } = new List<Sale>();
}
