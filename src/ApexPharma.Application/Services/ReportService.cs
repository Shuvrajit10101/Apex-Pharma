using ApexPharma.Domain.Entities;

namespace ApexPharma.Application.Services;

/// <summary>
/// Skeleton <see cref="IReportService"/>. Implemented in Phase 1/2 (sales, stock,
/// expiry, H1 register) (plan.md §11).
/// </summary>
public class ReportService : IReportService
{
    public Task<IReadOnlyList<Sale>> GetSalesAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<SaleItem>> GetScheduleRegisterAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
