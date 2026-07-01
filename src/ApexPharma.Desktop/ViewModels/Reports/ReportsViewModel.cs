using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Reporting;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Desktop.Services;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Reports;

/// <summary>
/// The Reports hub view-model (plan.md §11, §14). Read-only: a report-type selector + a
/// date-range picker drive one of five queries on <see cref="IReportService"/>, and the results
/// fill the matching grid with a summary line. Export writes CSV for any report (and a printable
/// PDF for the Schedule-H register and GST/HSN summary) via <see cref="IReportExporter"/> +
/// <see cref="IReportFileService"/>. The whole module is gated on <see cref="Permission.ViewReports"/>
/// (Owner + Pharmacist, plan.md §4); the navigation service also refuses to open it for a role
/// that lacks the permission. No data access or money/stock logic lives here (plan.md §8).
/// </summary>
public sealed class ReportsViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly IReportService _reports;
    private readonly IReportExporter _exporter;
    private readonly IReportFileService _files;
    private readonly ISettingsService _settings;

    private ReportType _selectedReportType = ReportType.Sales;
    private DateTime _fromDate = DateTime.Today.AddDays(-30);
    private DateTime _toDate = DateTime.Today;
    private int _nearExpiryDays = IInventoryService.DefaultNearExpiryDays;
    private string _summary = string.Empty;
    private string? _statusMessage;
    private bool _hasRun;
    private ReportHeader _header = new();

    public ReportsViewModel(
        IReportService reports,
        IReportExporter exporter,
        IReportFileService files,
        ISettingsService settings)
    {
        _reports = reports;
        _exporter = exporter;
        _files = files;
        _settings = settings;

        RunCommand = new RelayCommand(async () => await RunAsync());
        ExportCsvCommand = new RelayCommand(async () => await ExportCsvAsync(), () => _hasRun);
        ExportPdfCommand = new RelayCommand(async () => await ExportPdfAsync(), () => _hasRun && PdfAvailable);
    }

    /// <summary>The report types offered in the selector (bound to a ComboBox).</summary>
    public IReadOnlyList<ReportType> ReportTypes { get; } = new[]
    {
        ReportType.Sales,
        ReportType.LowStock,
        ReportType.Expiry,
        ReportType.ScheduleRegister,
        ReportType.HsnSummary,
    };

    public ReportType SelectedReportType
    {
        get => _selectedReportType;
        set
        {
            if (SetProperty(ref _selectedReportType, value))
            {
                // Switching report resets the "has run" state so exports don't act on stale rows.
                _hasRun = false;
                OnPropertyChanged(nameof(IsSales));
                OnPropertyChanged(nameof(IsLowStock));
                OnPropertyChanged(nameof(IsExpiry));
                OnPropertyChanged(nameof(IsScheduleRegister));
                OnPropertyChanged(nameof(IsHsnSummary));
                OnPropertyChanged(nameof(IsDateRangeReport));
                OnPropertyChanged(nameof(PdfAvailable));
                Summary = string.Empty;
                RaiseCommandStates();
            }
        }
    }

    public DateTime FromDate
    {
        get => _fromDate;
        set => SetProperty(ref _fromDate, value);
    }

    public DateTime ToDate
    {
        get => _toDate;
        set => SetProperty(ref _toDate, value);
    }

    /// <summary>Near-expiry window (days) for the expiry report; ignored by the other reports.</summary>
    public int NearExpiryDays
    {
        get => _nearExpiryDays;
        set => SetProperty(ref _nearExpiryDays, value);
    }

    /// <summary>Footing / count line shown under the grid.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Transient status/error banner (e.g. export path or a failure message).</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // One collection per report; only the active one is populated/visible.
    public ObservableCollection<SalesReportRow> SalesRows { get; } = new();
    public ObservableCollection<LowStockRow> LowStockRows { get; } = new();
    public ObservableCollection<ExpiryRow> ExpiryRows { get; } = new();
    public ObservableCollection<ScheduleRegisterRow> ScheduleRows { get; } = new();
    public ObservableCollection<HsnSummaryRow> HsnRows { get; } = new();

    public bool IsSales => SelectedReportType == ReportType.Sales;
    public bool IsLowStock => SelectedReportType == ReportType.LowStock;
    public bool IsExpiry => SelectedReportType == ReportType.Expiry;
    public bool IsScheduleRegister => SelectedReportType == ReportType.ScheduleRegister;
    public bool IsHsnSummary => SelectedReportType == ReportType.HsnSummary;

    /// <summary>True for the reports that take a date range (all but low-stock and expiry).</summary>
    public bool IsDateRangeReport => SelectedReportType is ReportType.Sales or ReportType.ScheduleRegister or ReportType.HsnSummary;

    /// <summary>True for the reports that offer a printable PDF (register + GST/HSN summary).</summary>
    public bool PdfAvailable => SelectedReportType is ReportType.ScheduleRegister or ReportType.HsnSummary;

    public ICommand RunCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportPdfCommand { get; }

    /// <inheritdoc />
    public async Task ActivateAsync(UserRole role)
    {
        // Build the printed-report header from the pharmacy profile once on entry.
        PharmacyProfile profile = await _settings.GetProfileAsync();
        _header = new ReportHeader
        {
            PharmacyName = string.IsNullOrWhiteSpace(profile.PharmacyName) ? "Apex-Pharma" : profile.PharmacyName,
            SubHeader = BuildSubHeader(profile),
        };

        await RunAsync();
    }

    /// <summary>Runs the selected report over the current parameters.</summary>
    public async Task RunAsync()
    {
        StatusMessage = null;
        try
        {
            switch (SelectedReportType)
            {
                case ReportType.Sales:
                    await LoadSalesAsync();
                    break;
                case ReportType.LowStock:
                    await LoadLowStockAsync();
                    break;
                case ReportType.Expiry:
                    await LoadExpiryAsync();
                    break;
                case ReportType.ScheduleRegister:
                    await LoadScheduleAsync();
                    break;
                case ReportType.HsnSummary:
                    await LoadHsnAsync();
                    break;
            }

            _hasRun = true;
        }
        catch (Exception ex)
        {
            _hasRun = false;
            StatusMessage = $"Couldn't run the report: {ex.Message}";
        }

        RaiseCommandStates();
    }

    private async Task LoadSalesAsync()
    {
        SalesReport report = await _reports.GetSalesReportAsync(FromDate, ToDate);
        SalesRows.Clear();
        foreach (SalesReportRow r in report.Rows)
        {
            SalesRows.Add(r);
        }

        SalesReportSummary s = report.Summary;
        Summary = $"{s.BillCount} bill(s) · Net {s.Net:0.00} · GST {s.TotalGst:0.00} · Gross {s.Gross:0.00} · Profit {s.TotalProfit:0.00}";
    }

    private async Task LoadLowStockAsync()
    {
        IReadOnlyList<LowStockRow> rows = await _reports.GetLowStockReportAsync();
        LowStockRows.Clear();
        foreach (LowStockRow r in rows)
        {
            LowStockRows.Add(r);
        }

        Summary = $"{LowStockRows.Count} product(s) at or below reorder level.";
    }

    private async Task LoadExpiryAsync()
    {
        int days = NearExpiryDays < 0 ? IInventoryService.DefaultNearExpiryDays : NearExpiryDays;
        IReadOnlyList<ExpiryRow> rows = await _reports.GetExpiryReportAsync(days);
        ExpiryRows.Clear();
        int expired = 0;
        int near = 0;
        foreach (ExpiryRow r in rows)
        {
            ExpiryRows.Add(r);
            if (r.IsExpired) expired++; else near++;
        }

        Summary = $"{ExpiryRows.Count} batch(es) — {expired} expired, {near} near-expiry (within {days} days).";
    }

    private async Task LoadScheduleAsync()
    {
        IReadOnlyList<ScheduleRegisterRow> rows = await _reports.GetScheduleRegisterAsync(FromDate, ToDate);
        ScheduleRows.Clear();
        foreach (ScheduleRegisterRow r in rows)
        {
            ScheduleRows.Add(r);
        }

        Summary = $"{ScheduleRows.Count} scheduled-drug dispensing(s) in the range.";
    }

    private async Task LoadHsnAsync()
    {
        HsnSummaryReport report = await _reports.GetHsnSummaryAsync(FromDate, ToDate);
        HsnRows.Clear();
        foreach (HsnSummaryRow r in report.Rows)
        {
            HsnRows.Add(r);
        }

        HsnSummaryTotals t = report.Totals;
        Summary = $"{HsnRows.Count} HSN/rate group(s) · Taxable {t.Taxable:0.00} · CGST {t.Cgst:0.00} · SGST {t.Sgst:0.00} · Total {t.Total:0.00}";
    }

    private async Task ExportCsvAsync()
    {
        try
        {
            string csv;
            string baseName;
            switch (SelectedReportType)
            {
                case ReportType.Sales:
                    csv = _exporter.SalesReportCsv(await _reports.GetSalesReportAsync(FromDate, ToDate));
                    baseName = "sales-report";
                    break;
                case ReportType.LowStock:
                    csv = _exporter.LowStockCsv(await _reports.GetLowStockReportAsync());
                    baseName = "low-stock";
                    break;
                case ReportType.Expiry:
                    csv = _exporter.ExpiryCsv(await _reports.GetExpiryReportAsync(NearExpiryDays));
                    baseName = "expiry";
                    break;
                case ReportType.ScheduleRegister:
                    csv = _exporter.ScheduleRegisterCsv(await _reports.GetScheduleRegisterAsync(FromDate, ToDate));
                    baseName = "schedule-register";
                    break;
                case ReportType.HsnSummary:
                    csv = _exporter.HsnSummaryCsv(await _reports.GetHsnSummaryAsync(FromDate, ToDate));
                    baseName = "gst-hsn-summary";
                    break;
                default:
                    return;
            }

            string path = await _files.SaveCsvAsync(csv, baseName);
            StatusMessage = $"CSV saved to {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    private async Task ExportPdfAsync()
    {
        try
        {
            byte[] pdf;
            string baseName;
            switch (SelectedReportType)
            {
                case ReportType.ScheduleRegister:
                    pdf = _exporter.ScheduleRegisterPdf(_header, FromDate, ToDate, await _reports.GetScheduleRegisterAsync(FromDate, ToDate));
                    baseName = "schedule-register";
                    break;
                case ReportType.HsnSummary:
                    pdf = _exporter.HsnSummaryPdf(_header, FromDate, ToDate, await _reports.GetHsnSummaryAsync(FromDate, ToDate));
                    baseName = "gst-hsn-summary";
                    break;
                default:
                    return; // PDF only for the register and the GST/HSN summary.
            }

            string path = await _files.SavePdfAsync(pdf, baseName);
            StatusMessage = $"PDF saved to {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    private static string? BuildSubHeader(PharmacyProfile profile)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.Gstin))
        {
            parts.Add($"GSTIN: {profile.Gstin}");
        }

        if (!string.IsNullOrWhiteSpace(profile.DlNumber))
        {
            parts.Add($"D.L. No: {profile.DlNumber}");
        }

        return parts.Count == 0 ? null : string.Join("  ·  ", parts);
    }

    private void RaiseCommandStates()
    {
        (RunCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportCsvCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportPdfCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
