namespace ApexPharma.Domain.Entities;

/// <summary>
/// Product grouping, e.g. Medication, Vitamins, Health Products (plan.md §7.2).
/// Normalized to its own table so a category rename touches one row, not every
/// product — fixing the name-duplication flaw in the Hari-Om source system.
/// </summary>
public class Category
{
    public int CategoryId { get; set; }

    public string Name { get; set; } = string.Empty;

    public ICollection<Product> Products { get; set; } = new List<Product>();
}
