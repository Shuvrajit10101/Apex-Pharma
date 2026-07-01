using Microsoft.EntityFrameworkCore.Storage;

namespace ApexPharma.Data.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUnitOfWork"/>. Caches one repository per
/// entity type and wraps <see cref="ApexPharmaDbContext"/>'s change tracker so a
/// whole sale/return/adjustment commits atomically (plan.md §6.2).
/// </summary>
public class UnitOfWork : IUnitOfWork
{
    private readonly ApexPharmaDbContext _context;
    private readonly Dictionary<Type, object> _repositories = new();
    private IDbContextTransaction? _transaction;
    private bool _disposed;

    public UnitOfWork(ApexPharmaDbContext context) => _context = context;

    public IRepository<T> Repository<T>() where T : class
    {
        if (_repositories.TryGetValue(typeof(T), out var existing))
        {
            return (IRepository<T>)existing;
        }

        var repository = new Repository<T>(_context);
        _repositories[typeof(T)] = repository;
        return repository;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => _context.SaveChangesAsync(cancellationToken);

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
        => _transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
        {
            return;
        }

        await _transaction.CommitAsync(cancellationToken);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
        {
            return;
        }

        await _transaction.RollbackAsync(cancellationToken);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _transaction?.Dispose();
        _context.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
