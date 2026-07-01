using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Data.Repositories;

/// <summary>
/// Default EF Core implementation of <see cref="IRepository{T}"/>. Change tracking
/// and persistence are owned by the <see cref="IUnitOfWork"/>: this type only
/// stages changes; <c>SaveChangesAsync</c> commits them in one transaction.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public class Repository<T> : IRepository<T> where T : class
{
    protected readonly ApexPharmaDbContext Context;
    protected readonly DbSet<T> Set;

    public Repository(ApexPharmaDbContext context)
    {
        Context = context;
        Set = context.Set<T>();
    }

    public async Task<T?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        => await Set.FindAsync(new object?[] { id }, cancellationToken);

    public async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken cancellationToken = default)
        => await Set.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<T>> FindAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
        => await Set.AsNoTracking().Where(predicate).ToListAsync(cancellationToken);

    public async Task AddAsync(T entity, CancellationToken cancellationToken = default)
        => await Set.AddAsync(entity, cancellationToken);

    public void Update(T entity) => Set.Update(entity);

    public void Remove(T entity) => Set.Remove(entity);
}
