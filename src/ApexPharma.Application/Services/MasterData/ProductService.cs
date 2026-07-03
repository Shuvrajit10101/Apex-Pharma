using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// Concrete <see cref="IProductService"/> (plan.md §8 layering). Owns catalog validation
/// so no stock/price/tax rule leaks into the UI (plan.md §8, coding standards): required
/// name, GST rate within the Indian slabs, non-negative reorder level, unique barcode,
/// and existing FKs. Mutations are gated on <see cref="Permission.ManageProducts"/>.
/// </summary>
public class ProductService : IProductService
{
    /// <summary>The legal Indian GST slabs a product's default rate may take (plan.md §6.1).</summary>
    private static readonly decimal[] ValidGstRates = { 0m, 5m, 12m, 18m, 28m };

    private readonly ApexPharmaDbContext _db;
    private readonly IAuthService _auth;

    public ProductService(ApexPharmaDbContext db, IAuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <inheritdoc />
    public async Task<MasterResult<Product>> CreateAsync(ProductInput input, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageProducts))
        {
            return MasterResult<Product>.Fail("You do not have permission to manage products.");
        }

        string? error = await ValidateAsync(input, excludeId: null, cancellationToken);
        if (error is not null)
        {
            return MasterResult<Product>.Fail(error);
        }

        var product = new Product { IsActive = true };
        Apply(product, input);

        await _db.Products.AddAsync(product, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult<Product>.Ok(product);
    }

    /// <inheritdoc />
    public async Task<MasterResult> UpdateAsync(int productId, ProductInput input, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageProducts))
        {
            return MasterResult.Fail("You do not have permission to manage products.");
        }

        Product? product = await _db.Products.FirstOrDefaultAsync(p => p.ProductId == productId, cancellationToken);
        if (product is null)
        {
            return MasterResult.Fail("Product not found.");
        }

        string? error = await ValidateAsync(input, excludeId: productId, cancellationToken);
        if (error is not null)
        {
            return MasterResult.Fail(error);
        }

        Apply(product, input);
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult.Ok();
    }

    /// <inheritdoc />
    public async Task<MasterResult> DeactivateAsync(int productId, UserRole actingRole, CancellationToken cancellationToken = default)
    {
        if (!_auth.HasPermission(actingRole, Permission.ManageProducts))
        {
            return MasterResult.Fail("You do not have permission to manage products.");
        }

        Product? product = await _db.Products.FirstOrDefaultAsync(p => p.ProductId == productId, cancellationToken);
        if (product is null)
        {
            return MasterResult.Fail("Product not found.");
        }

        product.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);
        return MasterResult.Ok();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Product>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        IQueryable<Product> query = _db.Products.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        return await query.OrderBy(p => p.Name).ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Product>> SearchAsync(string term, CancellationToken cancellationToken = default)
    {
        term = term?.Trim() ?? string.Empty;
        if (term.Length == 0)
        {
            return await ListAsync(includeInactive: false, cancellationToken);
        }

        // NOTE: ToLower() (not ToLowerInvariant) — the SQLite EF provider only translates
        // ToLower() to the server-side lower() function; ToLowerInvariant() has no
        // translation and throws. The comparison runs in SQLite, so .NET culture is not
        // involved and this matches the column's NOCASE backstop.
        string lowered = term.ToLower();
        return await _db.Products.AsNoTracking()
            .Where(p => p.IsActive &&
                        (p.Name.ToLower().Contains(lowered) ||
                         (p.Barcode != null && p.Barcode == term)))
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Product?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken = default)
    {
        barcode = barcode?.Trim() ?? string.Empty;
        if (barcode.Length == 0)
        {
            return null;
        }

        // Exact barcode match, active-only — mirrors the barcode predicate in SearchAsync so a
        // scan resolves to exactly the one product a manual search would (barcodes are unique).
        return await _db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.IsActive && p.Barcode != null && p.Barcode == barcode, cancellationToken);
    }

    /// <summary>
    /// All field validation shared by create/update (plan.md §6.2). Returns null when
    /// valid, otherwise a clear message. <paramref name="excludeId"/> lets an update skip
    /// its own row for the barcode-uniqueness check.
    /// </summary>
    private async Task<string?> ValidateAsync(ProductInput input, int? excludeId, CancellationToken cancellationToken)
    {
        if (input is null)
        {
            return "Product details are required.";
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Product name is required.";
        }

        if (!ValidGstRates.Contains(input.GstRate))
        {
            return "GST rate must be one of the Indian slabs: 0, 5, 12, 18, or 28%.";
        }

        if (input.ReorderLevel < 0)
        {
            return "Reorder level cannot be negative.";
        }

        // Referenced category + manufacturer must exist (plan.md §7 normalized FKs).
        bool categoryExists = await _db.Categories.AnyAsync(c => c.CategoryId == input.CategoryId, cancellationToken);
        if (!categoryExists)
        {
            return "The selected category does not exist.";
        }

        bool manufacturerExists = await _db.Manufacturers.AnyAsync(m => m.ManufacturerId == input.ManufacturerId, cancellationToken);
        if (!manufacturerExists)
        {
            return "The selected manufacturer does not exist.";
        }

        // Barcode is optional, but unique when present so a scan maps to exactly one product.
        if (!string.IsNullOrWhiteSpace(input.Barcode))
        {
            string barcode = input.Barcode.Trim();
            bool duplicate = await _db.Products.AnyAsync(
                p => p.Barcode == barcode && (excludeId == null || p.ProductId != excludeId),
                cancellationToken);
            if (duplicate)
            {
                return $"Barcode '{barcode}' is already assigned to another product.";
            }
        }

        return null;
    }

    /// <summary>Copies validated input onto the entity (trims text, nullifies blanks).</summary>
    private static void Apply(Product product, ProductInput input)
    {
        product.Name = input.Name.Trim();
        product.GenericName = Nullify(input.GenericName);
        product.ManufacturerId = input.ManufacturerId;
        product.CategoryId = input.CategoryId;
        product.HsnCode = Nullify(input.HsnCode);
        product.GstRate = input.GstRate;
        product.Schedule = input.Schedule;
        product.DosageForm = Nullify(input.DosageForm);
        product.Strength = Nullify(input.Strength);
        product.PackSize = Nullify(input.PackSize);
        product.Unit = Nullify(input.Unit);
        product.RackLocation = Nullify(input.RackLocation);
        product.ReorderLevel = input.ReorderLevel;
        product.Barcode = Nullify(input.Barcode);
    }

    private static string? Nullify(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
