namespace ApexPharma.Desktop.ViewModels.Reports;

/// <summary>
/// The report the hub is currently showing (plan.md §11). Drives which grid is visible, which
/// query runs, and which export actions are offered.
/// </summary>
public enum ReportType
{
    Sales,
    LowStock,
    Expiry,
    ScheduleRegister,
    ScheduleXRegister,
    HsnSummary,
    Gstr1
}
