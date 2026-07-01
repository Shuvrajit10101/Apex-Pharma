using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// CRUD + search for suppliers (plan.md §6.1, §7.2). Name is required; GSTIN, when
/// provided, must match the 15-char format. Mutations are gated on
/// <see cref="Permission.ManageSuppliers"/> (plan.md §4).
/// </summary>
public interface ISupplierService
{
    Task<MasterResult<Supplier>> CreateAsync(SupplierInput input, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<MasterResult> UpdateAsync(int supplierId, SupplierInput input, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<MasterResult> DeactivateAsync(int supplierId, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Supplier>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive search by supplier name (blank term returns the active list).</summary>
    Task<IReadOnlyList<Supplier>> SearchAsync(string term, CancellationToken cancellationToken = default);
}
