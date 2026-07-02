using ApexPharma.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Tests;

/// <summary>
/// Builds an <see cref="ApexPharmaDbContext"/> over an <b>in-memory SQLite</b>
/// connection (<c>Data Source=:memory:</c>). Real SQLite (not the EF in-memory
/// provider) is used so the tests exercise the actual schema, keys, and relational
/// behaviour that ship to production. The single open connection keeps the database
/// alive for the lifetime of the instance; disposing it drops the database.
/// </summary>
public sealed class SqliteInMemoryContext : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteInMemoryContext()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApexPharmaDbContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new ApexPharmaDbContext(options);
        Context.Database.EnsureCreated();
    }

    /// <summary>The live context bound to the in-memory database.</summary>
    public ApexPharmaDbContext Context { get; }

    /// <summary>Options bound to the same in-memory database — for a custom (e.g. fault-injecting) context.</summary>
    public DbContextOptions<ApexPharmaDbContext> Options =>
        new DbContextOptionsBuilder<ApexPharmaDbContext>().UseSqlite(_connection).Options;

    /// <summary>Opens a second context over the same in-memory database (fresh change tracker).</summary>
    public ApexPharmaDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ApexPharmaDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new ApexPharmaDbContext(options);
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
