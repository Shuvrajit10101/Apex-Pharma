using System;
using System.Threading;
using System.Threading.Tasks;
using ApexPharma.Data;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Tests;

/// <summary>
/// An <see cref="ApexPharmaDbContext"/> that throws on its FIRST <c>SaveChangesAsync</c> call, to
/// exercise a GENUINE post-mutation rollback: the service under test has already reduced the party
/// balance and added the audit row in the change tracker, and the throw during persist forces the
/// surrounding ACID transaction to roll back. On a fresh context nothing must have been committed.
/// The throw fires only once so any later save (in the same test, on a different context) is normal.
/// </summary>
public sealed class ThrowOnSaveDbContext : ApexPharmaDbContext
{
    private bool _thrown;

    public ThrowOnSaveDbContext(DbContextOptions<ApexPharmaDbContext> options)
        : base(options)
    {
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!_thrown)
        {
            _thrown = true;
            throw new InvalidOperationException("Injected persistence failure (post-mutation rollback test).");
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
