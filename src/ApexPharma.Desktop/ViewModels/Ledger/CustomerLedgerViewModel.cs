using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Ledger;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Reporting;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Desktop.Services;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Ledger;

/// <summary>
/// Customer Ledger module view-model (plan.md §3, §10, §11). Keyboard-first: search a customer,
/// pick one, run a running-balance khata statement over a date range, and optionally record a
/// receipt against the balance. Recording is gated on <see cref="Permission.DoBilling"/> — a
/// <see cref="Permission.ViewReports"/>-only user (who can still reach the module) sees the record
/// panel disabled via <see cref="CanRecordReceipt"/>. CSV + A4-PDF export mirror the Reports hub.
/// No money rule lives here; all validation/mutation is in <see cref="ICustomerLedgerService"/>
/// (plan.md §8).
/// </summary>
public sealed class CustomerLedgerViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly ICustomerService _customers;
    private readonly ICustomerLedgerService _ledger;
    private readonly ILedgerExporter _exporter;
    private readonly IReportFileService _files;
    private readonly ISettingsService _settings;
    private readonly ISessionContext _session;
    private readonly IAuthService _auth;

    private UserRole _actingRole;
    private ReportHeader _header = new();

    private string _searchTerm = string.Empty;
    private Customer? _selectedCustomer;
    private DateTime _fromDate = DateTime.Today.AddDays(-90);
    private DateTime _toDate = DateTime.Today;

    private decimal _receiptAmount;
    private PaymentMode _receiptMode = PaymentMode.Cash;
    private string _receiptReference = string.Empty;
    private string _receiptNote = string.Empty;

    private PartyStatement? _statement;
    private string _summary = string.Empty;
    private string? _statusMessage;
    private bool _isError;
    private bool _canRecordReceipt;

    public CustomerLedgerViewModel(
        ICustomerService customers,
        ICustomerLedgerService ledger,
        ILedgerExporter exporter,
        IReportFileService files,
        ISettingsService settings,
        ISessionContext session,
        IAuthService auth)
    {
        _customers = customers;
        _ledger = ledger;
        _exporter = exporter;
        _files = files;
        _settings = settings;
        _session = session;
        _auth = auth;

        SearchCommand = new RelayCommand(async () => await SearchAsync());
        RunCommand = new RelayCommand(async () => await RunAsync(), () => SelectedCustomer is not null);
        RecordReceiptCommand = new RelayCommand(async () => await RecordReceiptAsync(),
            () => CanRecordReceipt && SelectedCustomer is not null);
        ExportCsvCommand = new RelayCommand(async () => await ExportCsvAsync(), () => _statement is not null);
        ExportPdfCommand = new RelayCommand(async () => await ExportPdfAsync(), () => _statement is not null);
    }

    /// <summary>Customer search results (bound to the picker list).</summary>
    public ObservableCollection<Customer> Results { get; } = new();

    /// <summary>The current statement's rows (opening line, in-window rows).</summary>
    public ObservableCollection<PartyStatementRow> Rows { get; } = new();

    /// <summary>Payment modes offered for a receipt (bound to a ComboBox).</summary>
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = new[]
    {
        PaymentMode.Cash, PaymentMode.Upi, PaymentMode.Card,
    };

    public string SearchTerm
    {
        get => _searchTerm;
        set => SetProperty(ref _searchTerm, value);
    }

    public Customer? SelectedCustomer
    {
        get => _selectedCustomer;
        set
        {
            if (SetProperty(ref _selectedCustomer, value))
            {
                OnPropertyChanged(nameof(SelectedCustomerBalance));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>The selected customer's current khata balance (0 when none selected).</summary>
    public decimal SelectedCustomerBalance => SelectedCustomer?.Balance ?? 0m;

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

    public decimal ReceiptAmount
    {
        get => _receiptAmount;
        set => SetProperty(ref _receiptAmount, value);
    }

    public PaymentMode ReceiptMode
    {
        get => _receiptMode;
        set => SetProperty(ref _receiptMode, value);
    }

    public string ReceiptReference
    {
        get => _receiptReference;
        set => SetProperty(ref _receiptReference, value);
    }

    public string ReceiptNote
    {
        get => _receiptNote;
        set => SetProperty(ref _receiptNote, value);
    }

    /// <summary>Footing line under the grid (opening / closing balance).</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsError
    {
        get => _isError;
        private set => SetProperty(ref _isError, value);
    }

    /// <summary>
    /// True when the acting role may record a receipt (<see cref="Permission.DoBilling"/>). Binds
    /// the record panel's enabled state so a ViewReports-only user sees it read-only (plan.md §4).
    /// </summary>
    public bool CanRecordReceipt
    {
        get => _canRecordReceipt;
        private set
        {
            if (SetProperty(ref _canRecordReceipt, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public ICommand SearchCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand RecordReceiptCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportPdfCommand { get; }

    /// <inheritdoc />
    public async Task ActivateAsync(UserRole role)
    {
        _actingRole = role;
        CanRecordReceipt = _auth.HasPermission(role, Permission.DoBilling);

        // Build the printed-report header from the pharmacy profile once on entry (as Reports does).
        PharmacyProfile profile = await _settings.GetProfileAsync();
        _header = new ReportHeader
        {
            PharmacyName = string.IsNullOrWhiteSpace(profile.PharmacyName) ? "Apex-Pharma" : profile.PharmacyName,
            SubHeader = BuildSubHeader(profile),
        };

        await SearchAsync();
    }

    /// <summary>Searches customers by name/phone; a blank term returns the full list.</summary>
    public async Task SearchAsync()
    {
        try
        {
            IReadOnlyList<Customer> found = await _customers.SearchAsync(SearchTerm ?? string.Empty);
            Results.Clear();
            foreach (Customer c in found)
            {
                Results.Add(c);
            }

            SetStatus(null, isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Search failed: {ex.Message}", isError: true);
        }
    }

    /// <summary>Runs the statement for the selected customer over the current date range.</summary>
    public async Task RunAsync()
    {
        if (SelectedCustomer is null)
        {
            SetStatus("Select a customer first.", isError: true);
            return;
        }

        MasterResult<PartyStatement> result =
            await _ledger.GetStatementAsync(SelectedCustomer.CustomerId, FromDate, ToDate, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        _statement = result.Value!;
        Rows.Clear();
        foreach (PartyStatementRow r in _statement.Rows)
        {
            Rows.Add(r);
        }

        Summary = $"Opening {_statement.OpeningBalance:0.00} · Closing {_statement.ClosingBalance:0.00} · {Rows.Count} row(s)";
        SetStatus(null, isError: false);
        RaiseCommandStates();
    }

    private async Task RecordReceiptAsync()
    {
        if (SelectedCustomer is null)
        {
            SetStatus("Select a customer first.", isError: true);
            return;
        }

        var input = new CustomerReceiptInput(
            SelectedCustomer.CustomerId, ReceiptAmount, ReceiptMode,
            ReceiptReference, ReceiptNote);

        MasterResult<CustomerReceipt> result =
            await _ledger.RecordReceiptAsync(input, _session.UserId, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus($"Recorded receipt of {ReceiptAmount:0.00} from {SelectedCustomer.Name}.", isError: false);

        // Reflect the reduced balance and clear the input, then refresh the statement.
        SelectedCustomer.Balance -= ReceiptAmount;
        OnPropertyChanged(nameof(SelectedCustomerBalance));
        ReceiptAmount = 0m;
        ReceiptReference = string.Empty;
        ReceiptNote = string.Empty;
        await RunAsync();
    }

    private async Task ExportCsvAsync()
    {
        if (_statement is null)
        {
            return;
        }

        try
        {
            string csv = _exporter.PartyStatementCsv(_statement);
            string path = await _files.SaveCsvAsync(csv, $"customer-statement-{Safe(_statement.PartyName)}");
            SetStatus($"CSV saved to {path}", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}", isError: true);
        }
    }

    private async Task ExportPdfAsync()
    {
        if (_statement is null)
        {
            return;
        }

        try
        {
            byte[] pdf = _exporter.PartyStatementPdf(_header, _statement);
            string path = await _files.SavePdfAsync(pdf, $"customer-statement-{Safe(_statement.PartyName)}");
            SetStatus($"PDF saved to {path}", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}", isError: true);
        }
    }

    private static string Safe(string name) =>
        string.IsNullOrWhiteSpace(name) ? "customer" : name.Replace(' ', '-');

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

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }

    private void RaiseCommandStates()
    {
        (RunCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RecordReceiptCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportCsvCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportPdfCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
