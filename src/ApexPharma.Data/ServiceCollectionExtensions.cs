using ApexPharma.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPharma.Data;

/// <summary>
/// Data-layer DI wiring. Keeps the SQLite/EF Core registration detail inside the Data
/// layer (plan.md §8 layering) so callers just ask for "the data services" and never
/// hand-roll the DbContext options themselves.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="ApexPharmaDbContext"/> (SQLite at <paramref name="dbPath"/>)
    /// plus the <see cref="IUnitOfWork"/>/repository stack.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="dbPath">Absolute path to the SQLite database file.</param>
    public static IServiceCollection AddApexPharmaData(this IServiceCollection services, string dbPath)
    {
        services.AddDbContext<ApexPharmaDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }
}
