namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// Data carried into <see cref="ISupplierService"/> create/update (plan.md §7.2 supplier
/// fields). A DTO keeps the presentation layer from constructing entities directly and
/// gives the service one place to validate.
/// </summary>
public sealed class SupplierInput
{
    public string Name { get; set; } = string.Empty;
    public string? Gstin { get; set; }
    public string? DlNumber { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? StateCode { get; set; }
    public decimal OpeningBalance { get; set; }
}
