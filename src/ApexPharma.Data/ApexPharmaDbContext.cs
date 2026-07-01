using ApexPharma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Data;

/// <summary>
/// The EF Core unit of work for the local SQLite database (plan.md §8). Owns every
/// entity set and configures keys, relationships, decimal precision, and the
/// indexes that keep product search and add-to-bill under 300 ms (plan.md §6.2).
/// </summary>
public class ApexPharmaDbContext : DbContext
{
    public ApexPharmaDbContext(DbContextOptions<ApexPharmaDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleItem> SaleItems => Set<SaleItem>();
    public DbSet<SaleReturn> SaleReturns => Set<SaleReturn>();
    public DbSet<PurchaseReturn> PurchaseReturns => Set<PurchaseReturn>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureKeys(modelBuilder);
        ConfigureRelationships(modelBuilder);
        ConfigureIndexes(modelBuilder);
    }

    /// <summary>Primary keys that don't follow the &lt;Entity&gt;Id convention.</summary>
    private static void ConfigureKeys(ModelBuilder modelBuilder)
    {
        // Setting is a key/value store keyed by the string Key.
        modelBuilder.Entity<Setting>().HasKey(s => s.Key);
        modelBuilder.Entity<Setting>().Property(s => s.Key).HasMaxLength(200);

        // These PKs use plan.md §7.2 names (log_id, return_id, adjustment_id) that
        // don't match EF's Id / <TypeName>Id convention, so declare them explicitly.
        modelBuilder.Entity<AuditLog>().HasKey(a => a.LogId);
        modelBuilder.Entity<SaleReturn>().HasKey(r => r.ReturnId);
        modelBuilder.Entity<PurchaseReturn>().HasKey(r => r.ReturnId);
        modelBuilder.Entity<StockAdjustment>().HasKey(a => a.AdjustmentId);
    }

    /// <summary>
    /// FK relationships. Transactional links use <see cref="DeleteBehavior.Restrict"/>
    /// so historical records (sales, purchases, stock movements, audit) can never be
    /// silently lost by deleting a parent — data integrity is a hard requirement
    /// (plan.md §6.2, §12).
    /// </summary>
    private static void ConfigureRelationships(ModelBuilder modelBuilder)
    {
        // User ← Role
        modelBuilder.Entity<User>()
            .HasOne(u => u.Role)
            .WithMany(r => r.Users)
            .HasForeignKey(u => u.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        // Product ← Category, Manufacturer
        modelBuilder.Entity<Product>()
            .HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Product>()
            .HasOne(p => p.Manufacturer)
            .WithMany(m => m.Products)
            .HasForeignKey(p => p.ManufacturerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Batch ← Product, Supplier
        modelBuilder.Entity<Batch>()
            .HasOne(b => b.Product)
            .WithMany(p => p.Batches)
            .HasForeignKey(b => b.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Batch>()
            .HasOne(b => b.Supplier)
            .WithMany(s => s.Batches)
            .HasForeignKey(b => b.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        // Purchase ← Supplier, User
        modelBuilder.Entity<Purchase>()
            .HasOne(p => p.Supplier)
            .WithMany(s => s.Purchases)
            .HasForeignKey(p => p.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Purchase>()
            .HasOne(p => p.CreatedByUser)
            .WithMany(u => u.Purchases)
            .HasForeignKey(p => p.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        // PurchaseItem ← Purchase (cascade: items belong to their purchase), Product
        modelBuilder.Entity<PurchaseItem>()
            .HasOne(pi => pi.Purchase)
            .WithMany(p => p.Items)
            .HasForeignKey(pi => pi.PurchaseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PurchaseItem>()
            .HasOne(pi => pi.Product)
            .WithMany(p => p.PurchaseItems)
            .HasForeignKey(pi => pi.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // Sale ← Customer (optional), User
        modelBuilder.Entity<Sale>()
            .HasOne(s => s.Customer)
            .WithMany(c => c.Sales)
            .HasForeignKey(s => s.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Sale>()
            .HasOne(s => s.CreatedByUser)
            .WithMany(u => u.Sales)
            .HasForeignKey(s => s.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        // SaleItem ← Sale (cascade: lines belong to their bill), Batch, Product
        modelBuilder.Entity<SaleItem>()
            .HasOne(si => si.Sale)
            .WithMany(s => s.Items)
            .HasForeignKey(si => si.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SaleItem>()
            .HasOne(si => si.Batch)
            .WithMany(b => b.SaleItems)
            .HasForeignKey(si => si.BatchId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SaleItem>()
            .HasOne(si => si.Product)
            .WithMany(p => p.SaleItems)
            .HasForeignKey(si => si.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // SaleReturn ← Sale, Batch, User
        modelBuilder.Entity<SaleReturn>()
            .HasOne(sr => sr.Sale)
            .WithMany(s => s.Returns)
            .HasForeignKey(sr => sr.SaleId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SaleReturn>()
            .HasOne(sr => sr.Batch)
            .WithMany(b => b.SaleReturns)
            .HasForeignKey(sr => sr.BatchId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SaleReturn>()
            .HasOne(sr => sr.CreatedByUser)
            .WithMany()
            .HasForeignKey(sr => sr.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        // PurchaseReturn ← Purchase, Batch, User
        modelBuilder.Entity<PurchaseReturn>()
            .HasOne(pr => pr.Purchase)
            .WithMany(p => p.PurchaseReturns)
            .HasForeignKey(pr => pr.PurchaseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PurchaseReturn>()
            .HasOne(pr => pr.Batch)
            .WithMany(b => b.PurchaseReturns)
            .HasForeignKey(pr => pr.BatchId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PurchaseReturn>()
            .HasOne(pr => pr.CreatedByUser)
            .WithMany()
            .HasForeignKey(pr => pr.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        // StockAdjustment ← Batch, Product, User
        modelBuilder.Entity<StockAdjustment>()
            .HasOne(sa => sa.Batch)
            .WithMany(b => b.StockAdjustments)
            .HasForeignKey(sa => sa.BatchId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StockAdjustment>()
            .HasOne(sa => sa.Product)
            .WithMany(p => p.StockAdjustments)
            .HasForeignKey(sa => sa.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StockAdjustment>()
            .HasOne(sa => sa.CreatedByUser)
            .WithMany(u => u.StockAdjustments)
            .HasForeignKey(sa => sa.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        // AuditLog ← User
        modelBuilder.Entity<AuditLog>()
            .HasOne(a => a.User)
            .WithMany(u => u.AuditLogs)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    /// <summary>
    /// Unique/lookup indexes. Bill numbers are UNIQUE (plan.md §6.2); product name +
    /// barcode and batch expiry are indexed for fast search, barcode scan, and
    /// near-expiry/FEFO queries (plan.md §6.1, §6.2, §7).
    /// </summary>
    private static void ConfigureIndexes(ModelBuilder modelBuilder)
    {
        // Usernames must be unique so credentials map to exactly one account (plan.md §14).
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.BillNo)
            .IsUnique();

        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Name);

        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Barcode);

        modelBuilder.Entity<Batch>()
            .HasIndex(b => b.ExpiryDate);
    }
}
