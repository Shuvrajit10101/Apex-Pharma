using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// CRUD + search for the product catalog (plan.md §6.1, §7.2). Mutations are gated on
/// <see cref="Permission.ManageProducts"/>; validation enforces required name, a GST
/// rate within the Indian slabs {0,5,12,18,28}, non-negative reorder level, a unique
/// barcode when supplied, and existing category + manufacturer FKs (plan.md §6.2).
/// </summary>
public interface IProductService
{
    Task<MasterResult<Product>> CreateAsync(ProductInput input, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<MasterResult> UpdateAsync(int productId, ProductInput input, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<MasterResult> DeactivateAsync(int productId, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Product>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive search by name or exact barcode (blank term returns the active list).</summary>
    Task<IReadOnlyList<Product>> SearchAsync(string term, CancellationToken cancellationToken = default);
}
