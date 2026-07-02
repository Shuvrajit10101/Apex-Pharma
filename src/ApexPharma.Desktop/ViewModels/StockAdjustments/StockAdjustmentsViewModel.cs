using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services.Inventory;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.StockAdjustments;

/// <summary>
/// Stock Adjustment module view-model (plan.md §6.1, §10). Three panels:
/// <list type="bullet">
///   <item><b>Adjust a batch</b> — pick a batch, enter a delta OR a counted quantity + reason, apply
///   (breakage/wastage or physical-count correction).</item>
///   <item><b>Expiry write-off</b> — grid of expired batches; write off selected, or "write off all
///   expired" with a result summary (count + value lost at cost and MRP).</item>
///   <item><b>Adjustment history</b> — recent adjustments (date, product, batch, type, delta, reason, user).</item>
/// </list>
/// All validation/mutation lives in <see cref="IStockAdjustmentService"/> — no stock rule here (plan.md §8).
/// The whole module is gated on <see cref="Permission.AdjustStock"/> (the nav service refuses activation
/// otherwise, and the service re-checks on every mutation).
/// </summary>
public class StockAdjustmentsViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly IStockAdjustmentService _service;
    private readonly ISessionContext _session;

    private UserRole _actingRole;

    // Adjust-a-batch panel state.
    private AdjustableBatch? _selectedBatch;
    private decimal _delta;
    private decimal _countedQty;
    private string _reason = string.Empty;
    private bool _useCount;

    private string? _statusMessage;
    private bool _isError;

    public StockAdjustmentsViewModel(IStockAdjustmentService service, ISessionContext session)
    {
        _service = service;
        _session = session;

        ApplyAdjustmentCommand = new RelayCommand(async () => await ApplyAdjustmentAsync());
        WriteOffSelectedCommand = new RelayCommand(async () => await WriteOffSelectedAsync());
        WriteOffAllExpiredCommand = new RelayCommand(async () => await WriteOffAllExpiredAsync());
        RefreshCommand = new RelayCommand(async () => await ReloadAsync());
    }

    /// <summary>In-stock batches available to adjust (the "Adjust a batch" picker).</summary>
    public ObservableCollection<AdjustableBatch> Batches { get; } = new();

    /// <summary>Expired batches carrying stock (the write-off grid).</summary>
    public ObservableCollection<ExpiredBatchViewModel> ExpiredBatches { get; } = new();

    /// <summary>Recent adjustments (audit/history grid).</summary>
    public ObservableCollection<AdjustmentHistoryRow> History { get; } = new();

    public AdjustableBatch? SelectedBatch
    {
        get => _selectedBatch;
        set => SetProperty(ref _selectedBatch, value);
    }

    /// <summary>Signed quantity change for a breakage/wastage adjustment (negative removes).</summary>
    public decimal Delta
    {
        get => _delta;
        set => SetProperty(ref _delta, value);
    }

    /// <summary>The counted on-hand for a physical-count correction.</summary>
    public decimal CountedQty
    {
        get => _countedQty;
        set => SetProperty(ref _countedQty, value);
    }

    public string Reason
    {
        get => _reason;
        set => SetProperty(ref _reason, value);
    }

    /// <summary>When true, the "apply" button does a physical-count correction; otherwise a breakage delta.</summary>
    public bool UseCount
    {
        get => _useCount;
        set => SetProperty(ref _useCount, value);
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

    public ICommand ApplyAdjustmentCommand { get; }
    public ICommand WriteOffSelectedCommand { get; }
    public ICommand WriteOffAllExpiredCommand { get; }
    public ICommand RefreshCommand { get; }

    /// <inheritdoc />
    public async Task ActivateAsync(UserRole role)
    {
        _actingRole = role;
        await ReloadAsync();
    }

    /// <summary>Reloads the three grids (adjustable batches, expired batches, history) from the service.</summary>
    public async Task ReloadAsync()
    {
        Batches.Clear();
        foreach (AdjustableBatch b in await _service.GetAdjustableBatchesAsync())
        {
            Batches.Add(b);
        }

        ExpiredBatches.Clear();
        foreach (AdjustableBatch b in await _service.GetExpiredBatchesAsync())
        {
            ExpiredBatches.Add(new ExpiredBatchViewModel(b));
        }

        History.Clear();
        foreach (AdjustmentHistoryRow h in await _service.GetHistoryAsync())
        {
            History.Add(h);
        }
    }

    private async Task ApplyAdjustmentAsync()
    {
        if (SelectedBatch is null)
        {
            SetStatus("Select a batch to adjust.", isError: true);
            return;
        }

        MasterResult result = UseCount
            ? await _service.CorrectCountAsync(SelectedBatch.BatchId, CountedQty, Reason, _session.UserId, _actingRole)
            : await _service.AdjustByDeltaAsync(SelectedBatch.BatchId, Delta, Reason, _session.UserId, _actingRole);

        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus(
            UseCount
                ? $"Count corrected for batch '{SelectedBatch.BatchNo}'."
                : $"Adjusted batch '{SelectedBatch.BatchNo}' by {Delta:0.##}.",
            isError: false);

        Delta = 0m;
        CountedQty = 0m;
        Reason = string.Empty;
        await ReloadAsync();
    }

    private async Task WriteOffSelectedAsync()
    {
        var selected = ExpiredBatches.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetStatus("Tick at least one expired batch to write off.", isError: true);
            return;
        }

        int done = 0;
        decimal cost = 0m;
        decimal mrp = 0m;
        foreach (ExpiredBatchViewModel b in selected)
        {
            // Each batch is written off in its own atomic adjustment (plan.md §6.1).
            MasterResult<ExpiryWriteOffLine> r = await _service.WriteOffExpiredBatchAsync(b.BatchId, _session.UserId, _actingRole);
            if (r.Succeeded)
            {
                done++;
                cost += r.Value!.ValueAtCost;
                mrp += r.Value!.ValueAtMrp;
            }
            else if (done == 0)
            {
                // Surface the first failure (e.g. RBAC refusal) so the operator understands why.
                SetStatus(r.Error, isError: true);
                return;
            }
        }

        SetStatus($"Wrote off {done} expired batch(es). Value lost: {cost:0.00} at cost, {mrp:0.00} at MRP.", isError: false);
        await ReloadAsync();
    }

    private async Task WriteOffAllExpiredAsync()
    {
        MasterResult<ExpiryWriteOffSummary> result = await _service.WriteOffAllExpiredAsync(_session.UserId, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        ExpiryWriteOffSummary s = result.Value!;
        SetStatus(
            s.BatchCount == 0
                ? "No expired batches to write off."
                : $"Wrote off {s.BatchCount} expired batch(es), {s.TotalQty:0.##} unit(s). " +
                  $"Value lost: {s.TotalValueAtCost:0.00} at cost, {s.TotalValueAtMrp:0.00} at MRP.",
            isError: false);
        await ReloadAsync();
    }

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }
}
