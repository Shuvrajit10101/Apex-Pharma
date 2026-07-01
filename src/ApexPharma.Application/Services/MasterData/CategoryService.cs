using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// Concrete <see cref="ICategoryService"/> over the shared <see cref="ApexPharmaDbContext"/>
/// (plan.md §8 layering). Enforces required + case-insensitively-unique names and gates
/// mutations on <see cref="Permission.ManageProducts"/> (plan.md §4).
/// </summary>
public class CategoryService : ICategoryService
{
    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;

    public CategoryService(ApexPharmaDbContext db, IAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <inheritdoc />
    public async Task<MasterResult<Category>> CreateAsync(string name, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageProducts))
        {
            return MasterResult<Category>.Fail("You do not have permission to manage categories.");
        }

        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return MasterResult<Category>.Fail("Category name is required.");
        }

        if (await NameExistsAsync(name, excludeId: null, cancellationToken))
        {
            return MasterResult<Category>.Fail($"A category named '{name}' already exists.");
        }

        var category = new Category { Name = name, IsActive = true };
        await _db.Categories.AddAsync(category, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult<Category>.Ok(category);
    }

    /// <inheritdoc />
    public async Task<MasterResult> RenameAsync(int categoryId, string newName, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageProducts))
        {
            return MasterResult.Fail("You do not have permission to manage categories.");
        }

        newName = newName?.Trim() ?? string.Empty;
        if (newName.Length == 0)
        {
            return MasterResult.Fail("Category name is required.");
        }

        Category? category = await _db.Categories.FirstOrDefaultAsync(c => c.CategoryId == categoryId, cancellationToken);
        if (category is null)
        {
            return MasterResult.Fail("Category not found.");
        }

        if (await NameExistsAsync(newName, excludeId: categoryId, cancellationToken))
        {
            return MasterResult.Fail($"A category named '{newName}' already exists.");
        }

        category.Name = newName;
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult.Ok();
    }

    /// <inheritdoc />
    public async Task<MasterResult> DeactivateAsync(int categoryId, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageProducts))
        {
            return MasterResult.Fail("You do not have permission to manage categories.");
        }

        Category? category = await _db.Categories.FirstOrDefaultAsync(c => c.CategoryId == categoryId, cancellationToken);
        if (category is null)
        {
            return MasterResult.Fail("Category not found.");
        }

        category.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult.Ok();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Category>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        IQueryable<Category> query = _db.Categories.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        return await query.OrderBy(c => c.Name).ToListAsync(cancellationToken);
    }

    /// <summary>Case-insensitive duplicate-name check, optionally excluding one id (for rename).</summary>
    private Task<bool> NameExistsAsync(string name, int? excludeId, CancellationToken cancellationToken)
    {
        string lowered = name.ToLower();
        return _db.Categories.AnyAsync(
            c => c.Name.ToLower() == lowered && (excludeId == null || c.CategoryId != excludeId),
            cancellationToken);
    }
}
