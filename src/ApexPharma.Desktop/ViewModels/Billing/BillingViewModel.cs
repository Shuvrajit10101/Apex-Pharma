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

namespace ApexPharma.Desktop.ViewModels.Billing;

/// <summary>
/// POS billing view-model (plan.md §6.1, §9, §10) — the flagship screen. Drives product search,
/// the bill line grid (each line shows its FEFO batch + expiry + rate + live GST), live
/// subtotal/CGST/SGST/total, a bill discount, payment mode, a customer picker with inline
/// quick-add (required for Credit), the Schedule-H doctor+Rx prompt shown when a scheduled item
/// is on the bill, and Complete Sale. All FEFO dispensing, GST, bill numbering, stock decrement,
/// and khata live in <see cref="IBillingService"/> — no money/stock logic here (plan.md §8). As
/// an <see cref="IActivatableViewModel"/> it loads products, batches, and customers when the
/// navigation service activates it inside a fresh DI scope. Gated on <see cref="Permission.DoBilling"/>.
/// </summary>
public class BillingViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly IBillingService _billing;
    private readonly IProductService _products;
    private readonly ICustomerService _customers;
    private readonly IInventoryService _inventory;
    private readonly IGstService _gst;
    private readonly ISessionContext _session;

    private UserRole _actingRole;
    private List<Product> _productList = new();

    // Non-expired lots per product, for the line FEFO preview (batch/expiry/rate/available).
    private Dictionary<int, List<Batch>> _nonExpiredByProduct = new();

    private Product? _selectedProduct;
    private decimal _newLineQty = 1m;
    private decimal _billDiscount;
    private PaymentMode _paymentMode = PaymentMode.Cash;

    private Customer? _selectedCustomer;
    private string _doctorName = string.Empty;
    private string _prescriptionRef = string.Empty;

    private string _quickAddName = string.Empty;
    private string _quickAddPhone = string.Empty;

    private string? _statusMessage;
    private bool _isError;
    private string? _completedBill;

    public BillingViewModel(
        IBillingService billing,
        IProductService products,
        ICustomerService customers,
        IInventoryService inventory,
        IGstService gst,
        ISessionContext session)
    {
        _billing = billing;
        _products = products;
        _customers = customers;
        _inventory = inventory;
        _gst = gst;
        _session = session;

        AddLineCommand = new RelayCommand(AddLine);
        RemoveLineCommand = new RelayCommand(RemoveSelectedLine);
        CompleteSaleCommand = new RelayCommand(async () => await CompleteSaleAsync());
        NewSaleCommand = new RelayCommand(ResetForNextSale);
        QuickAddCustomerCommand = new RelayCommand(async () => await QuickAddCustomerAsync());

        Lines.CollectionChanged += (_, _) => RaiseTotals();
        PaymentModes = Enum.GetValues<PaymentMode>();
    }

    public ObservableCollection<BillLineViewModel> Lines { get; } = new();
    public ObservableCollection<Product> AvailableProducts { get; } = new();
    public ObservableCollection<Customer> Customers { get; } = new();
    public PaymentMode[] PaymentModes { get; }

    /// <summary>The product chosen in the search box to add as a new line.</summary>
    public Product? SelectedProduct
    {
        get => _selectedProduct;
        set => SetProperty(ref _selectedProduct, value);
    }

    public decimal NewLineQty
    {
        get => _newLineQty;
        set => SetProperty(ref _newLineQty, value);
    }

    private BillLineViewModel? _selectedLine;
    public BillLineViewModel? SelectedLine
    {
        get => _selectedLine;
        set => SetProperty(ref _selectedLine, value);
    }

    public decimal BillDiscount
    {
        get => _billDiscount;
        set { if (SetProperty(ref _billDiscount, value)) RaiseTotals(); }
    }

    public PaymentMode PaymentMode
    {
        get => _paymentMode;
        set
        {
            if (SetProperty(ref _paymentMode, value))
            {
                OnPropertyChanged(nameof(IsCredit));
            }
        }
    }

    /// <summary>True when the payment mode is Credit — the customer picker becomes required.</summary>
    public bool IsCredit => PaymentMode == PaymentMode.Credit;

    public Customer? SelectedCustomer
    {
        get => _selectedCustomer;
        set
        {
            if (SetProperty(ref _selectedCustomer, value))
            {
                OnPropertyChanged(nameof(CustomerBalanceText));
            }
        }
    }

    /// <summary>The selected customer's current khata balance, for on-screen awareness.</summary>
    public string CustomerBalanceText => SelectedCustomer is null
        ? string.Empty
        : $"Outstanding: {SelectedCustomer.Balance:0.00}  ·  Credit limit: {SelectedCustomer.CreditLimit:0.00}";

    public string DoctorName
    {
        get => _doctorName;
        set => SetProperty(ref _doctorName, value);
    }

    public string PrescriptionRef
    {
        get => _prescriptionRef;
        set => SetProperty(ref _prescriptionRef, value);
    }

    /// <summary>True when any line on the bill is a Schedule H/H1 drug — shows the Rx prompt.</summary>
    public bool RequiresPrescription => Lines.Any(l => l.IsScheduled);

    public string QuickAddName
    {
        get => _quickAddName;
        set => SetProperty(ref _quickAddName, value);
    }

    public string QuickAddPhone
    {
        get => _quickAddPhone;
        set => SetProperty(ref _quickAddPhone, value);
    }

    // ---- Live totals ----

    /// <summary>Bill subtotal (sum of line net taxable) less the whole-bill discount, floored at 0.</summary>
    public decimal Subtotal => Math.Max(0m, Lines.Sum(l => l.LineTaxable) - BillDiscount);

    public decimal TotalCgst => Lines.Sum(l => l.LineCgst);
    public decimal TotalSgst => Lines.Sum(l => l.LineSgst);

    /// <summary>Grand total = subtotal + CGST + SGST, rounded to the nearest whole rupee (preview).</summary>
    public decimal GrandTotal => Math.Round(Subtotal + TotalCgst + TotalSgst, 0, MidpointRounding.AwayFromZero);

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

    /// <summary>The saved bill summary text shown after Complete Sale (null until a sale completes).</summary>
    public string? CompletedBill
    {
        get => _completedBill;
        private set
        {
            if (SetProperty(ref _completedBill, value))
            {
                OnPropertyChanged(nameof(HasCompletedBill));
            }
        }
    }

    public bool HasCompletedBill => !string.IsNullOrEmpty(CompletedBill);

    public ICommand AddLineCommand { get; }
    public ICommand RemoveLineCommand { get; }
    public ICommand CompleteSaleCommand { get; }
    public ICommand NewSaleCommand { get; }
    public ICommand QuickAddCustomerCommand { get; }

    /// <inheritdoc />
    public Task ActivateAsync(UserRole role) => InitializeAsync(role);

    /// <summary>Loads the active products, their non-expired batches, and customers.</summary>
    public async Task InitializeAsync(UserRole actingRole)
    {
        _actingRole = actingRole;

        _productList = (await _products.ListAsync()).ToList();
        AvailableProducts.Clear();
        foreach (Product p in _productList)
        {
            AvailableProducts.Add(p);
        }

        await ReloadStockAsync();
        await ReloadCustomersAsync();

        if (Lines.Count == 0)
        {
            // Start empty — the biller searches/scans a product then presses Add.
        }
    }

    private async Task ReloadStockAsync()
    {
        // Build the per-product non-expired lots from the read-only stock grid (never expired
        // rows are the FEFO candidates). Display only — the server re-selects FEFO at sale time.
        IReadOnlyList<StockRow> rows = await _inventory.GetStockAsync();
        _nonExpiredByProduct = rows
            .Where(r => !r.IsExpired)
            .GroupBy(r => r.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new Batch
                {
                    BatchId = r.BatchId,
                    ProductId = r.ProductId,
                    BatchNo = r.BatchNo,
                    ExpiryDate = r.ExpiryDate,
                    QtyOnHand = r.QtyOnHand,
                    SalePrice = r.SalePrice,
                    Mrp = r.Mrp,
                }).ToList());

        foreach (BillLineViewModel line in Lines)
        {
            RefreshLinePreview(line);
        }
    }

    private async Task ReloadCustomersAsync()
    {
        int? previous = SelectedCustomer?.CustomerId;
        Customers.Clear();
        foreach (Customer c in await _customers.ListAsync())
        {
            Customers.Add(c);
        }

        if (previous is int id)
        {
            SelectedCustomer = Customers.FirstOrDefault(c => c.CustomerId == id);
        }
    }

    private void AddLine()
    {
        if (SelectedProduct is null)
        {
            SetStatus("Search and select a product to add.", isError: true);
            return;
        }

        if (NewLineQty <= 0)
        {
            SetStatus("Quantity must be greater than zero.", isError: true);
            return;
        }

        var line = new BillLineViewModel(_gst, _productList)
        {
            SelectedProduct = SelectedProduct,
            Qty = NewLineQty,
        };
        line.LineChanged += (_, _) => { RaiseTotals(); OnPropertyChanged(nameof(RequiresPrescription)); };
        RefreshLinePreview(line);
        Lines.Add(line);

        OnPropertyChanged(nameof(RequiresPrescription));
        SetStatus(null, isError: false);

        // Reset the add box for the next scan.
        SelectedProduct = null;
        NewLineQty = 1m;
    }

    private void RefreshLinePreview(BillLineViewModel line)
    {
        if (line.SelectedProduct is null)
        {
            return;
        }

        List<Batch> lots = _nonExpiredByProduct.TryGetValue(line.SelectedProduct.ProductId, out List<Batch>? l)
            ? l
            : new List<Batch>();
        line.SetFefoPreview(lots);
    }

    private void RemoveSelectedLine()
    {
        if (SelectedLine is not null)
        {
            Lines.Remove(SelectedLine);
            RaiseTotals();
            OnPropertyChanged(nameof(RequiresPrescription));
        }
    }

    private async Task CompleteSaleAsync()
    {
        if (Lines.Count == 0)
        {
            SetStatus("Add at least one line before completing the sale.", isError: true);
            return;
        }

        var input = new SaleInput
        {
            CustomerId = SelectedCustomer?.CustomerId,
            DoctorName = DoctorName,
            PrescriptionRef = PrescriptionRef,
            PaymentMode = PaymentMode,
            BillDiscount = BillDiscount,
            Lines = Lines.Select(l => new SaleLineInput
            {
                ProductId = l.SelectedProduct?.ProductId ?? 0,
                Qty = l.Qty,
                LineDiscount = l.LineDiscount,
            }).ToList(),
        };

        MasterResult<SaleReceipt> result = await _billing.CreateSaleAsync(input, _actingRole, _session.UserId);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SaleReceipt receipt = result.Value;
        CompletedBill =
            $"Bill {receipt.BillNo}\n" +
            $"Subtotal: {receipt.Subtotal:0.00}\n" +
            $"Discount: {receipt.Discount:0.00}\n" +
            $"CGST: {receipt.Cgst:0.00}    SGST: {receipt.Sgst:0.00}\n" +
            $"Round-off: {receipt.RoundOff:0.00}\n" +
            $"TOTAL: {receipt.Total:0.00}";

        SetStatus($"Sale complete — bill {receipt.BillNo}, total {receipt.Total:0.00}.", isError: false);

        // Refresh stock (FEFO previews) and customer balances after the sale, then clear the form
        // ready for the next customer.
        await ReloadStockAsync();
        await ReloadCustomersAsync();
        ClearForm();
    }

    private async Task QuickAddCustomerAsync()
    {
        if (string.IsNullOrWhiteSpace(QuickAddName))
        {
            SetStatus("Enter a name to quick-add a customer.", isError: true);
            return;
        }

        MasterResult<Customer> result = await _customers.CreateAsync(
            new CustomerInput { Name = QuickAddName, Phone = QuickAddPhone }, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        await ReloadCustomersAsync();
        SelectedCustomer = Customers.FirstOrDefault(c => c.CustomerId == result.Value!.CustomerId);
        QuickAddName = string.Empty;
        QuickAddPhone = string.Empty;
        SetStatus($"Customer '{result.Value!.Name}' added and selected.", isError: false);
    }

    /// <summary>Clears the working bill (lines, discount, Rx, customer) for the next sale.</summary>
    private void ClearForm()
    {
        Lines.Clear();
        BillDiscount = 0m;
        DoctorName = string.Empty;
        PrescriptionRef = string.Empty;
        SelectedCustomer = null;
        PaymentMode = PaymentMode.Cash;
        SelectedProduct = null;
        NewLineQty = 1m;
        RaiseTotals();
        OnPropertyChanged(nameof(RequiresPrescription));
    }

    /// <summary>Dismisses the completed-bill panel and starts a fresh sale.</summary>
    private void ResetForNextSale()
    {
        CompletedBill = null;
        SetStatus(null, isError: false);
        ClearForm();
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
