using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// CRUD for manufacturers (plan.md §6.1 masters). Name is required and unique
/// (case-insensitive). Mutations are gated on <see cref="Permission.ManageProducts"/>.
/// </summary>
public interface IManufacturerService
{
    Task<MasterResult<Manufacturer>> CreateAsync(string name, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<MasterResult> RenameAsync(int manufacturerId, string newName, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<MasterResult> DeactivateAsync(int manufacturerId, UserRole actingRole, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Manufacturer>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);
}
