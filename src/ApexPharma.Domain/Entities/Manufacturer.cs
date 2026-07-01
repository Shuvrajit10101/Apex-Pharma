namespace ApexPharma.Domain.Entities;

/// <summary>
/// The company that makes a product (plan.md §7.2). Referenced by ID from
/// <see cref="Product"/> to keep the schema normalized.
/// </summary>
public class Manufacturer
{
    public int ManufacturerId { get; set; }

    public string Name { get; set; } = string.Empty;

    public ICollection<Product> Products { get; set; } = new List<Product>();
}
