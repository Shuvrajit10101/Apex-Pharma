using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// CRUD + search for customers (plan.md §6.1, §7.2) so credit (khata) billing can pick or
/// quick-add a customer. Name is required; credit limit must be non-negative. Mutations are
/// gated on <see cref="Permission.DoBilling"/> — a biller can add/manage a customer inline
/// while taking a sale (plan.md §4/§6.1). The running balance is maintained by billing, never
/// edited through this service.
/// </summary>
public interface ICustomerService
{
    Task<MasterResult<Customer>> CreateAsync(CustomerInput input, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<MasterResult> UpdateAsync(int customerId, CustomerInput input, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Customer>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive search by name or phone (blank term returns the full list).</summary>
    Task<IReadOnlyList<Customer>> SearchAsync(string term, CancellationToken cancellationToken = default);

    /// <summary>Loads a single customer by id (for showing the current khata balance).</summary>
    Task<Customer?> GetAsync(int customerId, CancellationToken cancellationToken = default);
}
