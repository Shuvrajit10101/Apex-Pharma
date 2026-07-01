using ApexPharma.Domain.Entities;

namespace ApexPharma.Application.Services;

/// <summary>
/// Reporting for the owner and accountant (plan.md §6.1, §11): sales/profit, stock
/// valuation, near-expiry/expired, low-stock, and the Schedule H/H1 register.
/// </summary>
public interface IReportService
{
    /// <summary>Sales within a date range (for the sales/profit report).</summary>
    Task<IReadOnlyList<Sale>> GetSalesAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// The Schedule H/H1 register — sale lines of scheduled drugs with doctor and Rx
    /// reference (a legal requirement, derived by query — plan.md §7.2 note).
    /// </summary>
    Task<IReadOnlyList<SaleItem>> GetScheduleRegisterAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
}
