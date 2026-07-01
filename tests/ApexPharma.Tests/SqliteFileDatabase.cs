using System.IO;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Tests;

/// <summary>
/// Creates a real on-disk Apex-Pharma SQLite database under a temp directory, seeded with known
/// data, for backup/restore tests. Backup uses <c>VACUUM INTO</c> and file-level atomic moves, so
/// these tests must run against a genuine file (not <c>:memory:</c>). Disposing removes the temp
/// directory and all its files.
/// </summary>
public sealed class SqliteFileDatabase : IDisposable
{
    public string Directory { get; }
    public string DbPath { get; }

    public SqliteFileDatabase(int roleCount = 3, int productCount = 5)
    {
        Directory = Path.Combine(Path.GetTempPath(), "apex-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        DbPath = Path.Combine(Directory, "apexpharma.db");

        using ApexPharmaDbContext db = NewContext();
        db.Database.EnsureCreated();

        for (int i = 0; i < roleCount; i++)
        {
            db.Roles.Add(new Role { Name = $"Role{i}", PermissionsJson = "[]" });
        }

        db.Categories.Add(new Category { Name = "Medication", IsActive = true });
        db.Manufacturers.Add(new Manufacturer { Name = "Acme Pharma", IsActive = true });
        db.SaveChanges();

        int categoryId = db.Categories.First().CategoryId;
        int manufacturerId = db.Manufacturers.First().ManufacturerId;

        for (int i = 0; i < productCount; i++)
        {
            db.Products.Add(new Product
            {
                Name = $"Medicine {i}",
                CategoryId = categoryId,
                ManufacturerId = manufacturerId,
                HsnCode = "3004",
                GstRate = 12m,
                IsActive = true,
            });
        }

        db.Settings.Add(new Setting { Key = "Test.Marker", Value = "known-value" });
        db.SaveChanges();
    }

    public ApexPharmaDbContext NewContext()
    {
        // Pooling disabled so each disposed context releases the file handle immediately — the tests
        // move/replace the DB file directly, which a lingering pooled handle would block.
        var options = new DbContextOptionsBuilder<ApexPharmaDbContext>()
            .UseSqlite($"Data Source={DbPath};Pooling=False")
            .Options;
        return new ApexPharmaDbContext(options);
    }

    public int ProductCount()
    {
        using ApexPharmaDbContext db = NewContext();
        return db.Products.Count();
    }

    public void Dispose()
    {
        try
        {
            // Release any SQLite handles/pools before deleting the temp files.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
