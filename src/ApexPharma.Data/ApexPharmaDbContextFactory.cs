using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ApexPharma.Data;

/// <summary>
/// Design-time factory used by the EF Core tools (<c>dotnet ef migrations</c>).
/// The runtime app configures the context through DI, but the CLI needs a way to
/// build one without the full host — this provides it against the local SQLite file
/// (plan.md §8; see Migrations/README.md for the commands).
/// </summary>
public class ApexPharmaDbContextFactory : IDesignTimeDbContextFactory<ApexPharmaDbContext>
{
    public ApexPharmaDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApexPharmaDbContext>();
        optionsBuilder.UseSqlite("Data Source=apexpharma.db");
        return new ApexPharmaDbContext(optionsBuilder.Options);
    }
}
