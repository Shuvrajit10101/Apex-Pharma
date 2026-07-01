using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// Concrete <see cref="ISupplierService"/> (plan.md §8 layering). Validates the required
/// name and the optional GSTIN format, and gates mutations on
/// <see cref="Permission.ManageSuppliers"/> (plan.md §4).
/// </summary>
public class SupplierService : ISupplierService
{
    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;

    public SupplierService(ApexPharmaDbContext db, IAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <inheritdoc />
    public async Task<MasterResult<Supplier>> CreateAsync(SupplierInput input, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageSuppliers))
        {
            return MasterResult<Supplier>.Fail("You do not have permission to manage suppliers.");
        }

        string? error = Validate(input);
        if (error is not null)
        {
            return MasterResult<Supplier>.Fail(error);
        }

        var supplier = new Supplier { IsActive = true };
        Apply(supplier, input);

        await _db.Suppliers.AddAsync(supplier, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult<Supplier>.Ok(supplier);
    }

    /// <inheritdoc />
    public async Task<MasterResult> UpdateAsync(int supplierId, SupplierInput input, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageSuppliers))
        {
            return MasterResult.Fail("You do not have permission to manage suppliers.");
        }

        string? error = Validate(input);
        if (error is not null)
        {
            return MasterResult.Fail(error);
        }

        Supplier? supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.SupplierId == supplierId, cancellationToken);
        if (supplier is null)
        {
            return MasterResult.Fail("Supplier not found.");
        }

        Apply(supplier, input);
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult.Ok();
    }

    /// <inheritdoc />
    public async Task<MasterResult> DeactivateAsync(int supplierId, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageSuppliers))
        {
            return MasterResult.Fail("You do not have permission to manage suppliers.");
        }

        Supplier? supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.SupplierId == supplierId, cancellationToken);
        if (supplier is null)
        {
            return MasterResult.Fail("Supplier not found.");
        }

        supplier.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult.Ok();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Supplier>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        IQueryable<Supplier> query = _db.Suppliers.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(s => s.IsActive);
        }

        return await query.OrderBy(s => s.Name).ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Supplier>> SearchAsync(string term, CancellationToken cancellationToken = default)
    {
        term = term?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            return await ListAsync(includeInactive: false, cancellationToken);
        }

        string lowered = term.ToLower();
        return await _db.Suppliers.AsNoTracking()
            .Where(s => s.IsActive && s.Name.ToLower().Contains(lowered))
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Field validation shared by create/update. Returns null when valid.</summary>
    private static string? Validate(SupplierInput input)
    {
        if (input is null)
        {
            return "Supplier details are required.";
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Supplier name is required.";
        }

        // GSTIN is optional, but when supplied it must be well-formed (plan.md §14).
        if (!string.IsNullOrWhiteSpace(input.Gstin) && !GstinValidator.IsValid(input.Gstin))
        {
            return "GSTIN must be a valid 15-character GST number.";
        }

        return null;
    }

    /// <summary>Copies validated input onto the entity (trims text, normalises GSTIN).</summary>
    private static void Apply(Supplier supplier, SupplierInput input)
    {
        supplier.Name = input.Name.Trim();
        supplier.Gstin = string.IsNullOrWhiteSpace(input.Gstin) ? null : input.Gstin.Trim().ToUpperInvariant();
        supplier.DlNumber = Nullify(input.DlNumber);
        supplier.Phone = Nullify(input.Phone);
        supplier.Email = Nullify(input.Email);
        supplier.Address = Nullify(input.Address);
        supplier.StateCode = Nullify(input.StateCode);
        supplier.OpeningBalance = input.OpeningBalance;
    }

    private static string? Nullify(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
