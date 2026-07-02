using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Purchases;

/// <summary>
/// Purchase / GRN entry view-model (plan.md §6.1, §9, §10). Drives the supplier picker,
/// the editable line grid (product + batch + expiry + qty + purchase price + MRP + GST),
/// live invoice totals rolled up from each line's GST, a recent-purchases list, and a
/// purchase-return action. All validation, the batch stock-in, and the atomic save live in
/// <see cref="IPurchaseService"/> — no money/stock logic here (plan.md §8). As an
/// <see cref="IActivatableViewModel"/> it loads suppliers/products/recent purchases when the
/// navigation service activates it inside a fresh DI scope.
/// </summary>
public class PurchaseViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly IPurchaseService _purchases;
    private readonly ISupplierService _suppliers;
    private readonly IProductService _products;
    private readonly IGstService _gst;
    private readonly ISessionContext _session;

    private UserRole _actingRole;
    private Supplier? _selectedSupplier;
    private string _supplierInvoiceNo = string.Empty;
    private DateTime _invoiceDate = DateTime.Today;
    private string? _statusMessage;
    private bool _isError;

    private Purchase? _selectedRecentPurchase;

    private List<Product> _productList = new();

    public PurchaseViewModel(
        IPurchaseService purchases,
        ISupplierService suppliers,
        IProductService products,
        IGstService gst,
        ISessionContext session)
    {
        _purchases = purchases;
        _suppliers = suppliers;
        _products = products;
        _gst = gst;
        _session = session;

        AddLineCommand = new RelayCommand(AddLine);
        RemoveLineCommand = new RelayCommand(RemoveSelectedLine);
        SaveCommand = new RelayCommand(async () => await SaveAsync());
        ClearCommand = new RelayCommand(ClearForm);

        Lines.CollectionChanged += (_, _) => RaiseTotals();
    }

    public ObservableCollection<Supplier> Suppliers { get; } = new();
    public ObservableCollection<PurchaseLineViewModel> Lines { get; } = new();
    public ObservableCollection<Purchase> RecentPurchases { get; } = new();

    public Supplier? SelectedSupplier
    {
        get => _selectedSupplier;
        set => SetProperty(ref _selectedSupplier, value);
    }

    public string SupplierInvoiceNo
    {
        get => _supplierInvoiceNo;
        set => SetProperty(ref _supplierInvoiceNo, value);
    }

    public DateTime InvoiceDate
    {
        get => _invoiceDate;
        set => SetProperty(ref _invoiceDate, value);
    }

    private PurchaseLineViewModel? _selectedLine;
    public PurchaseLineViewModel? SelectedLine
    {
        get => _selectedLine;
        set => SetProperty(ref _selectedLine, value);
    }

    /// <summary>Invoice subtotal (sum of line taxable bases, before GST).</summary>
    public decimal Subtotal => Lines.Sum(l => l.LineTaxable);

    /// <summary>Total CGST across all lines.</summary>
    public decimal TotalCgst => Lines.Sum(l => l.LineCgst);

    /// <summary>Total SGST across all lines.</summary>
    public decimal TotalSgst => Lines.Sum(l => l.LineSgst);

    /// <summary>Grand total (subtotal + CGST + SGST).</summary>
    public decimal GrandTotal => Lines.Sum(l => l.LineTotal);

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

    public Purchase? SelectedRecentPurchase
    {
        get => _selectedRecentPurchase;
        set => SetProperty(ref _selectedRecentPurchase, value);
    }

    public ICommand AddLineCommand { get; }
    public ICommand RemoveLineCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ClearCommand { get; }

    /// <inheritdoc />
    public Task ActivateAsync(UserRole role) => InitializeAsync(role);

    /// <summary>Loads suppliers, products, and recent purchases for the signed-in role.</summary>
    public async Task InitializeAsync(UserRole actingRole)
    {
        _actingRole = actingRole;

        Suppliers.Clear();
        foreach (Supplier s in await _suppliers.ListAsync())
        {
            Suppliers.Add(s);
        }

        _productList = (await _products.ListAsync()).ToList();

        await ReloadRecentAsync();

        // Start with one blank line so the grid isn't empty.
        if (Lines.Count == 0)
        {
            AddLine();
        }
    }

    private async Task ReloadRecentAsync()
    {
        RecentPurchases.Clear();
        foreach (Purchase p in await _purchases.GetRecentPurchasesAsync())
        {
            RecentPurchases.Add(p);
        }
    }

    private void AddLine()
    {
        var line = new PurchaseLineViewModel(_gst, _productList);
        line.LineChanged += (_, _) => RaiseTotals();
        Lines.Add(line);
    }

    private void RemoveSelectedLine()
    {
        if (SelectedLine is not null)
        {
            Lines.Remove(SelectedLine);
            RaiseTotals();
        }
    }

    private async Task SaveAsync()
    {
        if (SelectedSupplier is null)
        {
            SetStatus("Select a supplier before saving.", isError: true);
            return;
        }

        if (Lines.Count == 0)
        {
            SetStatus("Add at least one line item.", isError: true);
            return;
        }

        var input = new PurchaseInput
        {
            SupplierId = SelectedSupplier.SupplierId,
            SupplierInvoiceNo = SupplierInvoiceNo,
            InvoiceDate = InvoiceDate,
            Lines = Lines.Select(l => new PurchaseLineInput
            {
                ProductId = l.SelectedProduct?.ProductId ?? 0,
                BatchNo = l.BatchNo,
                ExpiryDate = l.ExpiryDate ?? DateTime.MinValue,
                Qty = l.Qty,
                PurchasePrice = l.PurchasePrice,
                Mrp = l.Mrp,
                GstRate = l.GstRate,
            }).ToList(),
        };

        MasterResult<Purchase> result = await _purchases.RecordPurchaseAsync(input, _session.UserId, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus($"Purchase saved — {Lines.Count} line(s) stocked in. Total {result.Value!.Total:0.00}.", isError: false);
        ClearForm();
        await ReloadRecentAsync();
    }

    private void ClearForm()
    {
        SelectedSupplier = null;
        SupplierInvoiceNo = string.Empty;
        InvoiceDate = DateTime.Today;
        Lines.Clear();
        AddLine();
        RaiseTotals();
    }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(TotalCgst));
        OnPropertyChanged(nameof(TotalSgst));
        OnPropertyChanged(nameof(GrandTotal));
    }

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }
}
