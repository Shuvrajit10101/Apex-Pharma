namespace ApexPharma.Data.Repositories;

/// <summary>
/// Coordinates repositories and commits their changes in a single ACID transaction
/// (plan.md §6.2, §12). Billing, returns, and stock movements must all-or-nothing —
/// this is where that guarantee is enforced.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    /// <summary>Gets a repository for the given entity type.</summary>
    IRepository<T> Repository<T>() where T : class;

    /// <summary>Persists all staged changes; returns the number of rows affected.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Begins an explicit transaction for multi-step money/stock operations.</summary>
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>Commits the current explicit transaction.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Rolls back the current explicit transaction.</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
