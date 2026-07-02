using System;
using System.Threading;
using System.Threading.Tasks;
using ApexPharma.Data;
using ApexPharma.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ApexPharma.Tests;

/// <summary>
/// An <see cref="ApexPharmaDbContext"/> that, on its FIRST <c>SaveChangesAsync</c>, sneaks in a second
/// <see cref="DayEndClose"/> for the same <see cref="DuplicateBusinessDate"/> before delegating to the
/// base save. Both rows then hit the DB in one write and collide on the UNIQUE <c>BusinessDate</c>
/// index — reproducing the one-close-per-day RACE (a concurrent close landing AFTER
/// <c>DayEndService.CloseDayAsync</c>'s <c>AnyAsync</c> pre-check passed). Exercises the service's
/// <see cref="DbUpdateException"/> catch, which must surface the clean "already closed" failure. The
/// injection fires once, so any later save is normal.
/// </summary>
public sealed class DuplicateOnSaveDbContext : ApexPharmaDbContext
{
    private bool _injected;

    public DuplicateOnSaveDbContext(DbContextOptions<ApexPharmaDbContext> options, DateTime duplicateBusinessDate, int createdBy)
        : base(options)
    {
        DuplicateBusinessDate = duplicateBusinessDate;
        CreatedBy = createdBy;
    }

    public DateTime DuplicateBusinessDate { get; }
    public int CreatedBy { get; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!_injected)
        {
            _injected = true;
            // Add a conflicting close in the SAME unit of work; when the service's own AddAsync +
            // SaveChanges run, the two identical BusinessDate rows trip the UNIQUE index.
            DayEndCloses.Add(new DayEndClose
            {
                BusinessDate = DuplicateBusinessDate.Date,
                OpeningFloat = 0m,
                CashSales = 0m,
                CashReceipts = 0m,
                CashRefunds = 0m,
                CashSupplierPayments = 0m,
                ExpectedCash = 0m,
                CountedCash = 0m,
                Variance = 0m,
                ClosingCarryForward = 0m,
                Note = "concurrent close (test)",
                ClosedAt = DateTime.UtcNow,
                CreatedBy = CreatedBy,
            });
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
