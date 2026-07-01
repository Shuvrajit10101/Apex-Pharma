using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Inventory;

/// <summary>
/// Read-only inventory view-model (plan.md §6.1, §10). Shows current stock as one row per
/// batch (product, batch, qty, expiry, MRP) with near-expiry / expired / low-stock flags the
/// view colour-codes, so the owner can see what a purchase just stocked in. It performs no
/// mutations — every query lives in <see cref="IInventoryService"/> (plan.md §8). As an
/// <see cref="IActivatableViewModel"/> it loads the stock grid when the navigation service
/// activates it inside a fresh DI scope.
/// </summary>
public class InventoryViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly IInventoryService _inventory;

    private int _nearExpiryDays = IInventoryService.DefaultNearExpiryDays;
    private string? _statusMessage;

    public InventoryViewModel(IInventoryService inventory)
    {
        _inventory = inventory;
        RefreshCommand = new RelayCommand(async () => await ReloadAsync());
    }

    /// <summary>One row per in-stock batch, with highlighting flags.</summary>
    public ObservableCollection<StockRow> Rows { get; } = new();

    /// <summary>The near-expiry window in days used to flag rows (default 90).</summary>
    public int NearExpiryDays
    {
        get => _nearExpiryDays;
        set => SetProperty(ref _nearExpiryDays, value);
    }

    /// <summary>A short summary of what's on screen (row count, low-stock/near-expiry counts).</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ICommand RefreshCommand { get; }

    /// <inheritdoc />
    public Task ActivateAsync(UserRole role) => ReloadAsync();

    /// <summary>Loads the current stock rows (read-only).</summary>
    public async Task ReloadAsync()
    {
        int days = NearExpiryDays < 0 ? IInventoryService.DefaultNearExpiryDays : NearExpiryDays;

        Rows.Clear();
        int nearCount = 0;
        int expiredCount = 0;
        int lowCount = 0;
        foreach (StockRow row in await _inventory.GetStockAsync(days))
        {
            Rows.Add(row);
            if (row.IsNearExpiry) nearCount++;
            if (row.IsExpired) expiredCount++;
            if (row.IsLowStock) lowCount++;
        }

        StatusMessage = $"{Rows.Count} batch row(s) — {nearCount} near-expiry, {expiredCount} expired, {lowCount} low-stock.";
    }
}
