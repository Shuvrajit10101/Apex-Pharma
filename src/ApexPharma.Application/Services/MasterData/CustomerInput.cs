namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// Data carried into <see cref="ICustomerService"/> create/update (plan.md §7.2 customer
/// fields). A DTO keeps the presentation layer from constructing entities directly and gives
/// the service one place to validate. Name is required; phone/address are optional; credit
/// limit must be non-negative. The running <see cref="Domain.Entities.Customer.Balance"/> is
/// NOT set here — it is maintained by billing/khata, never edited directly.
/// </summary>
public sealed class CustomerInput
{
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public decimal CreditLimit { get; set; }
}
