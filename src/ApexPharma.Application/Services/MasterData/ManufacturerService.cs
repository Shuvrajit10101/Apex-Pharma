using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// Concrete <see cref="IManufacturerService"/> (plan.md §8 layering). Required +
/// case-insensitively-unique names; mutations gated on <see cref="Permission.ManageProducts"/>.
/// </summary>
public class ManufacturerService : IManufacturerService
{
    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;

    public ManufacturerService(ApexPharmaDbContext db, IAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <inheritdoc />
    public async Task<MasterResult<Manufacturer>> CreateAsync(string name, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageProducts))
        {
            return MasterResult<Manufacturer>.Fail("You do not have permission to manage manufacturers.");
        }

        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return MasterResult<Manufacturer>.Fail("Manufacturer name is required.");
        }

        if (await NameExistsAsync(name, excludeId: null, cancellationToken))
        {
            return MasterResult<Manufacturer>.Fail($"A manufacturer named '{name}' already exists.");
        }

        var manufacturer = new Manufacturer { Name = name, IsActive = true };
        await _db.Manufacturers.AddAsync(manufacturer, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult<Manufacturer>.Ok(manufacturer);
    }

    /// <inheritdoc />
    public async Task<MasterResult> RenameAsync(int manufacturerId, string newName, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageProducts))
        {
            return MasterResult.Fail("You do not have permission to manage manufacturers.");
        }

        newName = newName?.Trim() ?? string.Empty;
        if (newName.Length == 0)
        {
            return MasterResult.Fail("Manufacturer name is required.");
        }

        Manufacturer? manufacturer = await _db.Manufacturers.FirstOrDefaultAsync(m => m.ManufacturerId == manufacturerId, cancellationToken);
        if (manufacturer is null)
        {
            return MasterResult.Fail("Manufacturer not found.");
        }

        if (await NameExistsAsync(newName, excludeId: manufacturerId, cancellationToken))
        {
            return MasterResult.Fail($"A manufacturer named '{newName}' already exists.");
        }

        manufacturer.Name = newName;
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult.Ok();
    }

    /// <inheritdoc />
    public async Task<MasterResult> DeactivateAsync(int manufacturerId, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageProducts))
        {
            return MasterResult.Fail("You do not have permission to manage manufacturers.");
        }

        Manufacturer? manufacturer = await _db.Manufacturers.FirstOrDefaultAsync(m => m.ManufacturerId == manufacturerId, cancellationToken);
        if (manufacturer is null)
        {
            return MasterResult.Fail("Manufacturer not found.");
        }

        manufacturer.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult.Ok();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Manufacturer>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        IQueryable<Manufacturer> query = _db.Manufacturers.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(m => m.IsActive);
        }

        return await query.OrderBy(m => m.Name).ToListAsync(cancellationToken);
    }

    private Task<bool> NameExistsAsync(string name, int? excludeId, CancellationToken cancellationToken)
    {
        string lowered = name.ToLower();
        return _db.Manufacturers.AnyAsync(
            m => m.Name.ToLower() == lowered && (excludeId == null || m.ManufacturerId != excludeId),
            cancellationToken);
    }
}
