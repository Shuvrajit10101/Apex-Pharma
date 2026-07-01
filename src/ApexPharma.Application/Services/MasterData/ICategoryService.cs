using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// CRUD for product categories (plan.md §6.1 masters). Name is required and unique
/// (case-insensitive). Mutations are gated on <see cref="Permission.ManageProducts"/>
/// via the acting user's <see cref="UserRole"/> (plan.md §4).
/// </summary>
public interface ICategoryService
{
    Task<MasterResult<Category>> CreateAsync(string name, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<MasterResult> RenameAsync(int categoryId, string newName, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<MasterResult> DeactivateAsync(int categoryId, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Category>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);
}
