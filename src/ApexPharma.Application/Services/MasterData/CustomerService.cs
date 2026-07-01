using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// Concrete <see cref="ICustomerService"/> (plan.md §8 layering). Owns customer validation so
/// the UI holds no rules: required name, non-negative credit limit. Mutations are gated on
/// <see cref="Permission.DoBilling"/> so a biller can add/edit a customer inline while taking a
/// credit sale (plan.md §4). The khata <see cref="Customer.Balance"/> is maintained by
/// <see cref="IBillingService"/> and never edited here.
/// </summary>
public class CustomerService : ICustomerService
{
    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;

    public CustomerService(ApexPharmaDbContext db, IAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <inheritdoc />
    public async Task<MasterResult<Customer>> CreateAsync(CustomerInput input, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.DoBilling))
        {
            return MasterResult<Customer>.Fail("You do not have permission to manage customers.");
        }

        string? error = Validate(input);
        if (error is not null)
        {
            return MasterResult<Customer>.Fail(error);
        }

        var customer = new Customer { Balance = 0m };
        Apply(customer, input);

        await _db.Customers.AddAsync(customer, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult<Customer>.Ok(customer);
    }

    /// <inheritdoc />
    public async Task<MasterResult> UpdateAsync(int customerId, CustomerInput input, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.DoBilling))
        {
            return MasterResult.Fail("You do not have permission to manage customers.");
        }

        Customer? customer = await _db.Customers.FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
        if (customer is null)
        {
            return MasterResult.Fail("Customer not found.");
        }

        string? error = Validate(input);
        if (error is not null)
        {
            return MasterResult.Fail(error);
        }

        Apply(customer, input);
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult.Ok();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Customer>> ListAsync(CancellationToken cancellationToken = default)
        => await _db.Customers.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Customer>> SearchAsync(string term, CancellationToken cancellationToken = default)
    {
        term = term?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            return await ListAsync(cancellationToken);
        }

        // NOTE: ToLower() (not ToLowerInvariant) — the SQLite EF provider only translates
        // ToLower() to the server-side lower() function; ToLowerInvariant() has no translation
        // and throws. The comparison runs in SQLite, so .NET culture is not involved.
        string lowered = term.ToLower();
        return await _db.Customers.AsNoTracking()
            .Where(c => c.Name.ToLower().Contains(lowered)
                        || (c.Phone != null && c.Phone.ToLower().Contains(lowered)))
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Customer?> GetAsync(int customerId, CancellationToken cancellationToken = default)
        => await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);

    /// <summary>Field validation shared by create/update (plan.md §6.2).</summary>
    private static string? Validate(CustomerInput input)
    {
        if (input is null)
        {
            return "Customer details are required.";
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Customer name is required.";
        }

        if (input.CreditLimit < 0)
        {
            return "Credit limit cannot be negative.";
        }

        return null;
    }

    /// <summary>Copies validated input onto the entity (trims text, nullifies blanks).</summary>
    private static void Apply(Customer customer, CustomerInput input)
    {
        customer.Name = input.Name.Trim();
        customer.Phone = Nullify(input.Phone);
        customer.Address = Nullify(input.Address);
        customer.CreditLimit = input.CreditLimit;
    }

    private static string? Nullify(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
