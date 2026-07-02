using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Returns;

/// <summary>
/// Purchase-return view-model (plan.md §6.1, §10) — reachable from Purchases. The operator picks
/// a recent purchase to load its lines/batches, sets a return quantity per line + a reason, and
/// confirms. Each per-line return is validated and decremented atomically by
/// <see cref="IPurchaseService"/> (never negative, per-line over-return blocked) — no stock logic
/// lives here (plan.md §8). Permission-gated on <see cref="Permission.DoPurchases"/>.
/// </summary>
public class PurchaseReturnViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly IPurchaseService _purchases;
    private readonly ISessionContext _session;

    private UserRole _actingRole;
    private Purchase? _selectedPurchase;
    private string _reason = string.Empty;
    private string? _statusMessage;
    private bool _isError;
    private bool _purchaseLoaded;

    public PurchaseReturnViewModel(IPurchaseService purchases, ISessionContext session)
    {
        _purchases = purchases;
        _session = session;

        LoadLinesCommand = new RelayCommand(async () => await LoadLinesAsync());
        ConfirmReturnCommand = new RelayCommand(async () => await ConfirmAsync());
        RefreshCommand = new RelayCommand(async () => await ReloadRecentAsync());
    }

    public ObservableCollection<Purchase> RecentPurchases { get; } = new();
    public ObservableCollection<PurchaseReturnLineViewModel> Lines { get; } = new();

    public Purchase? SelectedPurchase
    {
        get => _selectedPurchase;
        set => SetProperty(ref _selectedPurchase, value);
    }

    public string Reason
    {
        get => _reason;
        set => SetProperty(ref _reason, value);
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

    /// <summary>True once a purchase's lines are loaded, so the grid + confirm are enabled.</summary>
    public bool PurchaseLoaded
    {
        get => _purchaseLoaded;
        private set => SetProperty(ref _purchaseLoaded, value);
    }

    public ICommand LoadLinesCommand { get; }
    public ICommand ConfirmReturnCommand { get; }
    public ICommand RefreshCommand { get; }

    /// <inheritdoc />
    public Task ActivateAsync(UserRole role) => InitializeAsync(role);

    public async Task InitializeAsync(UserRole role)
    {
        _actingRole = role;
        await ReloadRecentAsync();
    }

    private async Task ReloadRecentAsync()
    {
        RecentPurchases.Clear();
        foreach (Purchase p in await _purchases.GetRecentPurchasesAsync())
        {
            RecentPurchases.Add(p);
        }
    }

    private async Task LoadLinesAsync()
    {
        Lines.Clear();
        PurchaseLoaded = false;

        if (SelectedPurchase is null)
        {
            SetStatus("Select a purchase from the list to return against.", isError: true);
            return;
        }

        MasterResult<PurchaseReturnableLines> result = await _purchases.GetReturnableLinesAsync(SelectedPurchase.PurchaseId);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        PurchaseReturnableLines data = result.Value!;
        foreach (PurchaseReturnableLine line in data.Lines)
        {
            Lines.Add(new PurchaseReturnLineViewModel(line));
        }

        PurchaseLoaded = true;
        SetStatus($"Purchase from {data.SupplierName} loaded — {Lines.Count} line(s).", isError: false);
    }

    private async Task ConfirmAsync()
    {
        if (!PurchaseLoaded)
        {
            SetStatus("Load a purchase first.", isError: true);
            return;
        }

        var toReturn = Lines.Where(l => l.ReturnQty > 0).ToList();
        if (toReturn.Count == 0)
        {
            SetStatus("Enter a return quantity on at least one line.", isError: true);
            return;
        }

        foreach (PurchaseReturnLineViewModel line in toReturn)
        {
            if (line.ReturnQty > line.RemainingQty)
            {
                SetStatus($"'{line.ProductName}': cannot return {line.ReturnQty:0.##}, only {line.RemainingQty:0.##} remain.", isError: true);
                return;
            }
        }

        int done = 0;
        decimal totalQty = 0m;
        decimal totalAmount = 0m;

        // Each line return is its own atomic decrement (independent per batch). If one fails,
        // stop and report — the ones already committed stay, and reloading shows the new state.
        foreach (PurchaseReturnLineViewModel line in toReturn)
        {
            MasterResult<PurchaseReturn> result = await _purchases.ProcessPurchaseReturnLineAsync(
                line.PurchaseItemId, line.ReturnQty, Reason, _session.UserId, _actingRole);
            if (!result.Succeeded)
            {
                SetStatus($"Returned {done} line(s); '{line.ProductName}' failed: {result.Error}", isError: true);
                await LoadLinesAsync();
                return;
            }

            done++;
            totalQty += result.Value!.Qty;
            totalAmount += result.Value.Amount;
        }

        SetStatus($"Returned {totalQty:0.##} unit(s) across {done} line(s) to supplier. Value {totalAmount:0.00}.", isError: false);
        await ReloadRecentAsync();
        await LoadLinesAsync();
    }

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }
}
