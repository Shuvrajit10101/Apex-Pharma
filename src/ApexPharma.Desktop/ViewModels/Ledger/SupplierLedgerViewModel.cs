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
/// Supplier Ledger module view-model (plan.md §3, §10, §11). Keyboard-first: search a supplier,
/// pick one, run a running-balance account statement over a date range, and optionally record a
/// payment against the derived payable. Recording is gated on <see cref="Permission.DoPurchases"/>
/// — a <see cref="Permission.ViewReports"/>-only user (who can still reach the module) sees the
/// record panel disabled via <see cref="CanRecordPayment"/>. CSV + A4-PDF export mirror the Reports
/// hub. There is no stored supplier balance; the closing figure comes from the derived statement.
/// No money rule lives here; all validation/mutation is in <see cref="ISupplierLedgerService"/>
/// (plan.md §8).
/// </summary>
public sealed class SupplierLedgerViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly ISupplierService _suppliers;
    private readonly ISupplierLedgerService _ledger;
    private readonly ILedgerExporter _exporter;
    private readonly IReportFileService _files;
    private readonly ISettingsService _settings;
    private readonly ISessionContext _session;
    private readonly IAuthService _auth;

    private UserRole _actingRole;
    private ReportHeader _header = new();

    private string _searchTerm = string.Empty;
    private Supplier? _selectedSupplier;
    private DateTime _fromDate = DateTime.Today.AddDays(-90);
    private DateTime _toDate = DateTime.Today;

    private decimal _paymentAmount;
    private PaymentMode _paymentMode = PaymentMode.Cash;
    private string _paymentReference = string.Empty;
    private string _paymentNote = string.Empty;

    private PartyStatement? _statement;
    private string _summary = string.Empty;
    private string? _statusMessage;
    private bool _isError;
    private bool _canRecordPayment;

    public SupplierLedgerViewModel(
        ISupplierService suppliers,
        ISupplierLedgerService ledger,
        ILedgerExporter exporter,
        IReportFileService files,
        ISettingsService settings,
        ISessionContext session,
        IAuthService auth)
    {
        _suppliers = suppliers;
        _ledger = ledger;
        _exporter = exporter;
        _files = files;
        _settings = settings;
        _session = session;
        _auth = auth;

        SearchCommand = new RelayCommand(async () => await SearchAsync());
        RunCommand = new RelayCommand(async () => await RunAsync(), () => SelectedSupplier is not null);
        RecordPaymentCommand = new RelayCommand(async () => await RecordPaymentAsync(),
            () => CanRecordPayment && SelectedSupplier is not null);
        ExportCsvCommand = new RelayCommand(async () => await ExportCsvAsync(), () => _statement is not null);
        ExportPdfCommand = new RelayCommand(async () => await ExportPdfAsync(), () => _statement is not null);
    }

    /// <summary>Supplier search results (bound to the picker list).</summary>
    public ObservableCollection<Supplier> Results { get; } = new();

    /// <summary>The current statement's rows (opening line, in-window rows).</summary>
    public ObservableCollection<PartyStatementRow> Rows { get; } = new();

    /// <summary>Payment modes offered for a payment (bound to a ComboBox).</summary>
    public IReadOnlyList<PaymentMode> PaymentModes { get; } = new[]
    {
        PaymentMode.Cash, PaymentMode.Upi, PaymentMode.Card,
    };

    public string SearchTerm
    {
        get => _searchTerm;
        set => SetProperty(ref _searchTerm, value);
    }

    public Supplier? SelectedSupplier
    {
        get => _selectedSupplier;
        set
        {
            if (SetProperty(ref _selectedSupplier, value))
            {
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

    public decimal PaymentAmount
    {
        get => _paymentAmount;
        set => SetProperty(ref _paymentAmount, value);
    }

    public PaymentMode PaymentMode
    {
        get => _paymentMode;
        set => SetProperty(ref _paymentMode, value);
    }

    public string PaymentReference
    {
        get => _paymentReference;
        set => SetProperty(ref _paymentReference, value);
    }

    public string PaymentNote
    {
        get => _paymentNote;
        set => SetProperty(ref _paymentNote, value);
    }

    /// <summary>Footing line under the grid (opening / closing payable).</summary>
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
    /// True when the acting role may record a payment (<see cref="Permission.DoPurchases"/>). Binds
    /// the record panel's enabled state so a ViewReports-only user sees it read-only (plan.md §4).
    /// </summary>
    public bool CanRecordPayment
    {
        get => _canRecordPayment;
        private set
        {
            if (SetProperty(ref _canRecordPayment, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public ICommand SearchCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand RecordPaymentCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportPdfCommand { get; }

    /// <inheritdoc />
    public async Task ActivateAsync(UserRole role)
    {
        _actingRole = role;
        CanRecordPayment = _auth.HasPermission(role, Permission.DoPurchases);

        PharmacyProfile profile = await _settings.GetProfileAsync();
        _header = ReportHeaderFactory.From(profile);

        await SearchAsync();
    }

    /// <summary>Searches suppliers by name; a blank term returns the active list.</summary>
    public async Task SearchAsync()
    {
        try
        {
            IReadOnlyList<Supplier> found = await _suppliers.SearchAsync(SearchTerm ?? string.Empty);
            Results.Clear();
            foreach (Supplier s in found)
            {
                Results.Add(s);
            }

            SetStatus(null, isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Search failed: {ex.Message}", isError: true);
        }
    }

    /// <summary>Runs the statement for the selected supplier over the current date range.</summary>
    public async Task RunAsync()
    {
        if (SelectedSupplier is null)
        {
            SetStatus("Select a supplier first.", isError: true);
            return;
        }

        MasterResult<PartyStatement> result =
            await _ledger.GetStatementAsync(SelectedSupplier.SupplierId, FromDate, ToDate, _actingRole);
        if (!result.Succeeded)
        {
            // Clear any stale rows from a prior successful run so a failed re-run doesn't leave
            // out-of-date figures on screen with export still enabled.
            _statement = null;
            Rows.Clear();
            Summary = string.Empty;
            SetStatus(result.Error, isError: true);
            RaiseCommandStates();
            return;
        }

        _statement = result.Value!;
        Rows.Clear();
        foreach (PartyStatementRow r in _statement.Rows)
        {
            Rows.Add(r);
        }

        Summary = $"Opening {_statement.OpeningBalance:0.00} · Closing (payable) {_statement.ClosingBalance:0.00} · {Rows.Count} row(s)";
        SetStatus(null, isError: false);
        RaiseCommandStates();
    }

    private async Task RecordPaymentAsync()
    {
        if (SelectedSupplier is null)
        {
            SetStatus("Select a supplier first.", isError: true);
            return;
        }

        // Fail-fast client guard mirroring the sales-return flow; the service still owns the
        // authoritative validation (over-payment, negative, etc.).
        if (PaymentAmount <= 0)
        {
            SetStatus("Enter a payment amount greater than zero.", isError: true);
            return;
        }

        var input = new SupplierPaymentInput(
            SelectedSupplier.SupplierId, PaymentAmount, PaymentMode,
            PaymentReference, PaymentNote);

        MasterResult<SupplierPayment> result =
            await _ledger.RecordPaymentAsync(input, _session.UserId, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus($"Recorded payment of {PaymentAmount:0.00} to {SelectedSupplier.Name}.", isError: false);

        PaymentAmount = 0m;
        PaymentReference = string.Empty;
        PaymentNote = string.Empty;
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
            string path = await _files.SaveCsvAsync(csv, $"supplier-statement-{Safe(_statement.PartyName)}");
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
            string path = await _files.SavePdfAsync(pdf, $"supplier-statement-{Safe(_statement.PartyName)}");
            SetStatus($"PDF saved to {path}", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}", isError: true);
        }
    }

    private static string Safe(string name) =>
        string.IsNullOrWhiteSpace(name) ? "supplier" : name.Replace(' ', '-');

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }

    private void RaiseCommandStates()
    {
        (RunCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RecordPaymentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportCsvCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportPdfCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
