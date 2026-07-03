using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.DayEnd;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.DayEnd;

/// <summary>
/// Day-End module view-model (plan.md §3, §10, §11 — Phase 2e). Keyboard-first cash reconciliation:
/// on activation it loads today's summary (a Cashier is scoped to their own till, server-side), shows
/// the cash breakdown → expected cash, and — for Owner/Pharmacist (<see cref="CanClose"/>) — offers a
/// count-and-close panel with a live colour-coded variance. Once a day is closed the frozen snapshot
/// is shown. No money rule lives here; all validation/mutation is in <see cref="IDayEndService"/>
/// (plan.md §8). The close is restricted to non-Cashier in both the VM (<see cref="CanClose"/>) and the
/// service (defence in depth).
/// </summary>
public sealed class DayEndViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly IDayEndService _dayEnd;
    private readonly ISessionContext _session;
    private readonly IAuthService _auth;

    private UserRole _actingRole;
    private DateTime _businessDate = DateTime.Today;

    private DayEndSummary? _summary;
    private decimal _openingFloat;
    private decimal _carriedForward;
    private decimal _countedCash;
    private decimal? _carryForward;
    private string _note = string.Empty;
    private string _openingFloatReason = string.Empty;

    private bool _canClose;
    private bool _isClosed;
    private bool _hasInputError;
    private string? _statusMessage;
    private bool _isError;

    public DayEndViewModel(IDayEndService dayEnd, ISessionContext session, IAuthService auth)
    {
        _dayEnd = dayEnd;
        _session = session;
        _auth = auth;

        RefreshCommand = new RelayCommand(async () => await LoadAsync());
        CloseDayCommand = new RelayCommand(async () => await CloseDayAsync(),
            () => CanClose && !IsClosed && !HasInputError);
    }

    /// <summary>The scoped per-bill rows for the sales grid ("my sales today" for a Cashier / whole-store otherwise).</summary>
    public ObservableCollection<DayEndSaleRow> OwnSales { get; } = new();

    /// <summary>The close-history rows (date, expected, counted, variance, closed-by), most-recent first.</summary>
    public ObservableCollection<DayEndCloseRow> History { get; } = new();

    /// <summary>The business-day being reconciled (today).</summary>
    public DateTime BusinessDate
    {
        get => _businessDate;
        private set => SetProperty(ref _businessDate, value);
    }

    // ---- Cash breakdown (read-only tiles) ----

    public decimal CashSales => _summary?.CashSales ?? 0m;
    public decimal CashReceipts => _summary?.CashReceipts ?? 0m;
    public decimal CashRefunds => _summary?.CashRefunds ?? 0m;
    public decimal CashSupplierPayments => _summary?.CashSupplierPayments ?? 0m;

    /// <summary>
    /// Live expected cash = <see cref="OpeningFloat"/> + the server-computed net of cash movements.
    /// The cash-deltas are fixed by the summary (<c>_summary.ExpectedCash − _summary.OpeningFloat</c> =
    /// CashSales + CashReceipts − CashRefunds − CashSupplierPayments); editing the float shifts Expected
    /// (and Variance) on screen to match exactly what <c>CloseDayAsync</c> will persist, since the
    /// service honors the operator's float while keeping the cash deltas server-computed.
    /// </summary>
    public decimal ExpectedCash =>
        _summary is null ? 0m : OpeningFloat + (_summary.ExpectedCash - _summary.OpeningFloat);

    public decimal UpiTotal => _summary?.UpiTotal ?? 0m;
    public decimal CardTotal => _summary?.CardTotal ?? 0m;
    public decimal CreditTotal => _summary?.CreditTotal ?? 0m;
    public int BillCount => _summary?.BillCount ?? 0;
    public decimal GrossSales => _summary?.GrossSales ?? 0m;

    /// <summary>Opening float (prefilled from the summary's carry-forward; editable before close).</summary>
    public decimal OpeningFloat
    {
        get => _openingFloat;
        set
        {
            if (SetProperty(ref _openingFloat, value))
            {
                // The operator's float feeds Expected (and therefore Variance) live — matching the
                // service, which honors this float while keeping the cash deltas server-computed.
                OnPropertyChanged(nameof(ExpectedCash));
                OnPropertyChanged(nameof(Variance));
                OnPropertyChanged(nameof(IsVarianceOver));
                OnPropertyChanged(nameof(IsVarianceShort));
                // Overriding the float (≠ the prefilled carry-forward) requires a reason (owner-approved).
                OnPropertyChanged(nameof(RequiresFloatReason));
            }
        }
    }

    /// <summary>
    /// True when the entered <see cref="OpeningFloat"/> differs from the prefilled carried-forward
    /// amount — an override that REQUIRES <see cref="OpeningFloatReason"/> (owner-approved day-end
    /// control). Drives the reason input's required/highlighted state; the service is the real boundary.
    /// </summary>
    public bool RequiresFloatReason => !IsClosed && OpeningFloat != _carriedForward;

    /// <summary>
    /// Reason for overriding the opening float; required by the service (and flagged client-side via
    /// <see cref="RequiresFloatReason"/>) when the float differs from the carried-forward amount.
    /// </summary>
    public string OpeningFloatReason
    {
        get => _openingFloatReason;
        set => SetProperty(ref _openingFloatReason, value);
    }

    /// <summary>Counted cash entered by the closer; drives the live variance.</summary>
    public decimal CountedCash
    {
        get => _countedCash;
        set
        {
            if (SetProperty(ref _countedCash, value))
            {
                OnPropertyChanged(nameof(Variance));
                OnPropertyChanged(nameof(IsVarianceOver));
                OnPropertyChanged(nameof(IsVarianceShort));
            }
        }
    }

    /// <summary>Carry-forward to the next day (defaults to counted cash when left null).</summary>
    public decimal? CarryForward
    {
        get => _carryForward;
        set => SetProperty(ref _carryForward, value);
    }

    /// <summary>Variance note; required by the service when the variance is non-zero.</summary>
    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    /// <summary>
    /// Live variance = counted − expected (positive over, negative short). Recomputed as the closer
    /// types; the authoritative figure is recomputed server-side at close time.
    /// </summary>
    public decimal Variance => CountedCash - ExpectedCash;

    /// <summary>True when counted exceeds expected (drives the green "over" colour).</summary>
    public bool IsVarianceOver => Variance > 0m;

    /// <summary>True when counted falls short of expected (drives the red "short" colour).</summary>
    public bool IsVarianceShort => Variance < 0m;

    /// <summary>
    /// True when the acting role may close the whole store day (NOT a Cashier). Binds the close
    /// panel's visibility/enabled state; the service also rejects a Cashier close (plan.md §4).
    /// </summary>
    public bool CanClose
    {
        get => _canClose;
        private set
        {
            if (SetProperty(ref _canClose, value))
            {
                (CloseDayCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when the day is already closed — shows the frozen snapshot and disables the close.</summary>
    public bool IsClosed
    {
        get => _isClosed;
        private set
        {
            if (SetProperty(ref _isClosed, value))
            {
                (CloseDayCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// True when any money input (counted cash / opening float / carry-forward) currently holds an
    /// invalid or cleared value — a WPF binding exception the view flags via <c>Validation.Error</c>.
    /// Set by the view's error handler; gates the close so a stale/blank figure can never finalize a
    /// day (e.g. a cleared counted-cash must not silently close on the last committed value).
    /// </summary>
    public bool HasInputError
    {
        get => _hasInputError;
        set
        {
            if (SetProperty(ref _hasInputError, value))
            {
                (CloseDayCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// "Opening-float override reason: …" line shown on the frozen snapshot when the closed day recorded
    /// a float override (empty otherwise, so the line collapses).
    /// </summary>
    public string ClosedFloatReasonDisplay =>
        _summary is { IsClosed: true } && !string.IsNullOrWhiteSpace(_summary.OpeningFloatReason)
            ? $"Opening-float override reason: {_summary.OpeningFloatReason}"
            : string.Empty;

    /// <summary>True when the frozen snapshot has an override reason to show (drives its visibility).</summary>
    public bool HasClosedFloatReason =>
        _summary is { IsClosed: true } && !string.IsNullOrWhiteSpace(_summary.OpeningFloatReason);

    /// <summary>"Closed at HH:mm by &lt;user&gt;" banner shown once the day is closed.</summary>
    public string ClosedSummary =>
        _summary is { IsClosed: true }
            ? $"Closed at {_summary.ClosedAt?.ToLocalTime():HH:mm} by {_summary.ClosedByName} · counted {_summary.CountedCash:0.00} · variance {_summary.Variance:0.00}"
            : string.Empty;

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

    public ICommand RefreshCommand { get; }
    public ICommand CloseDayCommand { get; }

    /// <inheritdoc />
    public async Task ActivateAsync(UserRole role)
    {
        _actingRole = role;
        // Owner/Pharmacist may close the whole store day; a Cashier only views their own till.
        CanClose = role != UserRole.Cashier;
        BusinessDate = DateTime.Today;
        await LoadAsync();
    }

    /// <summary>Loads today's summary (Cashier-scoped server-side) and the recent close history.</summary>
    public async Task LoadAsync()
    {
        int? scopedTo = _actingRole == UserRole.Cashier ? _session.UserId : (int?)null;

        MasterResult<DayEndSummary> result =
            await _dayEnd.GetDaySummaryAsync(DateTime.Today, _session.UserId, _actingRole, scopedTo);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        _summary = result.Value!;
        BusinessDate = _summary.BusinessDate;
        IsClosed = _summary.IsClosed;

        // The carried-forward amount the summary derived (prior close's carry-forward, else 0). Overriding
        // the opening float (setting it ≠ this) requires a reason — set this BEFORE OpeningFloat so the
        // setter's RequiresFloatReason check compares against the correct baseline.
        _carriedForward = _summary.OpeningFloat;

        // Prefill the opening float from that carried-forward amount (so it is NOT an override by default).
        OpeningFloat = _summary.OpeningFloat;

        if (_summary.IsClosed)
        {
            // Reflect the frozen snapshot in the counted/carry/note/reason inputs (read-only once closed).
            CountedCash = _summary.CountedCash ?? 0m;
            CarryForward = _summary.ClosingCarryForward;
            Note = _summary.Note ?? string.Empty;
            OpeningFloatReason = _summary.OpeningFloatReason ?? string.Empty;
        }
        else
        {
            CountedCash = 0m;
            CarryForward = null;
            Note = string.Empty;
            OpeningFloatReason = string.Empty;
        }

        OwnSales.Clear();
        foreach (DayEndSaleRow row in _summary.OwnSales)
        {
            OwnSales.Add(row);
        }

        RaiseSummaryBindings();
        await LoadHistoryAsync();
        SetStatus(null, isError: false);
    }

    private async Task LoadHistoryAsync()
    {
        int? scopedTo = _actingRole == UserRole.Cashier ? _session.UserId : (int?)null;
        MasterResult<System.Collections.Generic.IReadOnlyList<DayEndCloseRow>> history =
            await _dayEnd.GetCloseHistoryAsync(
                DateTime.Today.AddDays(-30), DateTime.Today, _session.UserId, _actingRole, scopedTo);

        History.Clear();
        if (history.Succeeded && history.Value is not null)
        {
            foreach (DayEndCloseRow row in history.Value)
            {
                History.Add(row);
            }
        }
    }

    private async Task CloseDayAsync()
    {
        if (IsClosed)
        {
            SetStatus("This day is already closed.", isError: true);
            return;
        }

        // Client-side guard mirroring the service: an opening-float override needs a reason. The service
        // is the real boundary (defence in depth), but blocking here gives a clear message before the call.
        if (RequiresFloatReason && string.IsNullOrWhiteSpace(OpeningFloatReason))
        {
            SetStatus("A reason is required when the opening float differs from the carried-forward amount.", isError: true);
            return;
        }

        var input = new DayEndCloseInput(
            BusinessDate: DateTime.Today,
            OpeningFloat: OpeningFloat,
            CountedCash: CountedCash,
            ClosingCarryForward: CarryForward,
            Note: string.IsNullOrWhiteSpace(Note) ? null : Note,
            OpeningFloatReason: string.IsNullOrWhiteSpace(OpeningFloatReason) ? null : OpeningFloatReason);

        MasterResult<DayEndClose> result =
            await _dayEnd.CloseDayAsync(input, _session.UserId, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus($"Day closed. Variance {result.Value!.Variance:0.00}.", isError: false);
        // Refresh so the frozen snapshot + updated history are shown.
        await LoadAsync();
    }

    private void RaiseSummaryBindings()
    {
        OnPropertyChanged(nameof(CashSales));
        OnPropertyChanged(nameof(CashReceipts));
        OnPropertyChanged(nameof(CashRefunds));
        OnPropertyChanged(nameof(CashSupplierPayments));
        OnPropertyChanged(nameof(ExpectedCash));
        OnPropertyChanged(nameof(UpiTotal));
        OnPropertyChanged(nameof(CardTotal));
        OnPropertyChanged(nameof(CreditTotal));
        OnPropertyChanged(nameof(BillCount));
        OnPropertyChanged(nameof(GrossSales));
        OnPropertyChanged(nameof(Variance));
        OnPropertyChanged(nameof(IsVarianceOver));
        OnPropertyChanged(nameof(IsVarianceShort));
        OnPropertyChanged(nameof(RequiresFloatReason));
        OnPropertyChanged(nameof(ClosedSummary));
        OnPropertyChanged(nameof(ClosedFloatReasonDisplay));
        OnPropertyChanged(nameof(HasClosedFloatReason));
    }

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }
}
