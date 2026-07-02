using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Invoicing;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Desktop.Services;
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
    private readonly IInvoiceService _invoices;
    private readonly IReceiptPrinter _printer;
    private readonly ISessionContext _session;

    private UserRole _actingRole;
    private List<Product> _productList = new();

    // The sale id of the just-completed bill, so "Print receipt" reprints the last sale.
    private int? _lastSaleId;
    private string _reprintBillNo = string.Empty;

    // Non-expired lots per product, for the line FEFO preview (batch/expiry/rate/available).
    private Dictionary<int, List<Batch>> _nonExpiredByProduct = new();

    private Product? _selectedProduct;
    private decimal _newLineQty = 1m;
    private decimal _billDiscount;
    private PaymentMode _paymentMode = PaymentMode.Cash;

    private Customer? _selectedCustomer;
    private string _doctorName = string.Empty;
    private string _prescriptionRef = string.Empty;

    // Schedule-X strict dual-Rx capture (shown only when an X line is on the bill).
    private string _xPatientName = string.Empty;
    private string _xPatientAddress = string.Empty;
    private string _xPatientPhone = string.Empty;
    private string _xPrescriberName = string.Empty;
    private string _xPrescriberAddress = string.Empty;
    private string _xPrescriberRegNo = string.Empty;
    private string _xPrescriptionNumber = string.Empty;
    private DateTime? _xPrescriptionDate = DateTime.Today;
    private bool _xPrescriptionRetained;

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
        IInvoiceService invoices,
        IReceiptPrinter printer,
        ISessionContext session)
    {
        _billing = billing;
        _products = products;
        _customers = customers;
        _inventory = inventory;
        _gst = gst;
        _invoices = invoices;
        _printer = printer;
        _session = session;

        AddLineCommand = new RelayCommand(AddLine);
        RemoveLineCommand = new RelayCommand(RemoveSelectedLine);
        CompleteSaleCommand = new RelayCommand(async () => await CompleteSaleAsync());
        NewSaleCommand = new RelayCommand(ResetForNextSale);
        QuickAddCustomerCommand = new RelayCommand(async () => await QuickAddCustomerAsync());
        PrintReceiptCommand = new RelayCommand(async () => await PrintLastReceiptAsync());
        ReprintCommand = new RelayCommand(async () => await ReprintByBillNoAsync());

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

    /// <summary>True when any line is a Schedule X drug — shows the strict dual-Rx capture panel.</summary>
    public bool RequiresScheduleX => Lines.Any(l => l.Schedule == DrugSchedule.X);

    // ---- Schedule-X strict dual-Rx capture (bound only when RequiresScheduleX) ----

    public string XPatientName
    {
        get => _xPatientName;
        set => SetProperty(ref _xPatientName, value);
    }

    public string XPatientAddress
    {
        get => _xPatientAddress;
        set => SetProperty(ref _xPatientAddress, value);
    }

    public string XPatientPhone
    {
        get => _xPatientPhone;
        set => SetProperty(ref _xPatientPhone, value);
    }

    public string XPrescriberName
    {
        get => _xPrescriberName;
        set => SetProperty(ref _xPrescriberName, value);
    }

    public string XPrescriberAddress
    {
        get => _xPrescriberAddress;
        set => SetProperty(ref _xPrescriberAddress, value);
    }

    public string XPrescriberRegNo
    {
        get => _xPrescriberRegNo;
        set => SetProperty(ref _xPrescriberRegNo, value);
    }

    public string XPrescriptionNumber
    {
        get => _xPrescriptionNumber;
        set => SetProperty(ref _xPrescriptionNumber, value);
    }

    /// <summary>
    /// The prescription date. Nullable so clearing the DatePicker records NO date (rather than
    /// silently keeping today's) — a cleared date reaches the service as <c>default</c> and is
    /// rejected, so an unconfirmed date never lands in the legal register.
    /// </summary>
    public DateTime? XPrescriptionDate
    {
        get => _xPrescriptionDate;
        set => SetProperty(ref _xPrescriptionDate, value);
    }

    /// <summary>The biller confirms a duplicate copy of the prescription is retained at the pharmacy.</summary>
    public bool XPrescriptionRetained
    {
        get => _xPrescriptionRetained;
        set => SetProperty(ref _xPrescriptionRetained, value);
    }

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
    //
    // These mirror the authoritative BillingService roll-up so the on-screen figures match the
    // printed receipt: the whole-bill discount is subtracted from the taxable base FIRST, then
    // CGST/SGST are recomputed on that post-discount (net) value — not on the pre-discount line
    // GST. Without this, a bill discount would shrink the subtotal on screen but leave the shown
    // GST/total overstated (the deferred Phase-1d nit). The server re-derives everything
    // authoritatively at Complete Sale; this is display-only.

    /// <summary>Sum of each line's taxable base after its own line discount, before the bill discount.</summary>
    private decimal LineTaxableTotal => Lines.Sum(l => l.LineTaxable);

    /// <summary>The whole-bill discount actually applicable (capped at the pre-discount taxable base).</summary>
    private decimal EffectiveBillDiscount => Math.Min(Math.Max(0m, BillDiscount), LineTaxableTotal);

    /// <summary>Bill subtotal (sum of line net taxable) less the whole-bill discount, floored at 0.</summary>
    public decimal Subtotal => Math.Max(0m, LineTaxableTotal - EffectiveBillDiscount);

    public decimal TotalCgst => RecomputedGst().Cgst;
    public decimal TotalSgst => RecomputedGst().Sgst;

    /// <summary>Grand total = net subtotal + CGST + SGST, rounded to the nearest whole rupee (preview).</summary>
    public decimal GrandTotal => Math.Round(Subtotal + TotalCgst + TotalSgst, 0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Recomputes CGST/SGST on the bill-discounted net, apportioning the whole-bill discount across
    /// lines proportionally to their taxable (matching the service). When there is no bill discount
    /// this collapses to summing each line's own GST — so the common case is unchanged.
    /// </summary>
    private (decimal Cgst, decimal Sgst) RecomputedGst()
    {
        decimal lineTotal = LineTaxableTotal;
        decimal billDiscount = EffectiveBillDiscount;

        if (billDiscount <= 0m || lineTotal <= 0m)
        {
            return (Lines.Sum(l => l.LineCgst), Lines.Sum(l => l.LineSgst));
        }

        decimal cgst = 0m;
        decimal sgst = 0m;
        foreach (BillLineViewModel line in Lines)
        {
            // Each line keeps its share of the bill discount, then GST lands on the net.
            decimal lineNet = line.LineTaxable - (billDiscount * line.LineTaxable / lineTotal);
            GstResult gst = _gst.CalculateLineGst(lineNet, line.GstRate);
            cgst += gst.Cgst;
            sgst += gst.Sgst;
        }

        return (cgst, sgst);
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

    /// <summary>Bill number to reprint (typed into the reprint box).</summary>
    public string ReprintBillNo
    {
        get => _reprintBillNo;
        set => SetProperty(ref _reprintBillNo, value);
    }

    public ICommand AddLineCommand { get; }
    public ICommand RemoveLineCommand { get; }
    public ICommand CompleteSaleCommand { get; }
    public ICommand NewSaleCommand { get; }
    public ICommand QuickAddCustomerCommand { get; }

    /// <summary>Prints (previews) the receipt for the just-completed sale.</summary>
    public ICommand PrintReceiptCommand { get; }

    /// <summary>Reprints the receipt for an arbitrary earlier bill by its bill number.</summary>
    public ICommand ReprintCommand { get; }

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
        line.LineChanged += (_, _) => { RaiseTotals(); RaiseScheduleFlags(); };
        RefreshLinePreview(line);
        Lines.Add(line);

        RaiseScheduleFlags();
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
            RaiseScheduleFlags();
        }
    }

    private async Task CompleteSaleAsync()
    {
        if (Lines.Count == 0)
        {
            SetStatus("Add at least one line before completing the sale.", isError: true);
            return;
        }

        // Client-side guard for a friendly message; the BillingService is the real boundary.
        if (RequiresScheduleX && !ScheduleXCaptureIsComplete())
        {
            SetStatus(
                "Schedule X requires patient name & address, prescriber name, address & registration number, " +
                "prescription number & date, and confirmation the prescription copy is retained.",
                isError: true);
            return;
        }

        var input = new SaleInput
        {
            CustomerId = SelectedCustomer?.CustomerId,
            DoctorName = DoctorName,
            PrescriptionRef = PrescriptionRef,
            PaymentMode = PaymentMode,
            BillDiscount = BillDiscount,
            ScheduleX = RequiresScheduleX
                ? new ScheduleXCapture
                {
                    PatientName = XPatientName,
                    PatientAddress = XPatientAddress,
                    PatientPhone = XPatientPhone,
                    PrescriberName = XPrescriberName,
                    PrescriberAddress = XPrescriberAddress,
                    PrescriberRegNo = XPrescriberRegNo,
                    PrescriptionNumber = XPrescriptionNumber,
                    // A cleared (null) date reaches the service as default and is rejected there —
                    // never silently substitute today's date into a legal register.
                    PrescriptionDate = XPrescriptionDate ?? default,
                    PrescriptionRetained = XPrescriptionRetained,
                }
                : null,
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
        _lastSaleId = receipt.SaleId;
        CompletedBill =
            $"Bill {receipt.BillNo}\n" +
            $"Subtotal: {receipt.Subtotal:0.00}\n" +
            $"Discount: {receipt.Discount:0.00}\n" +
            $"CGST: {receipt.Cgst:0.00}    SGST: {receipt.Sgst:0.00}\n" +
            $"Round-off: {receipt.RoundOff:0.00}\n" +
            $"TOTAL: {receipt.Total:0.00}";

        SetStatus($"Sale complete — bill {receipt.BillNo}, total {receipt.Total:0.00}.", isError: false);

        // Generate + open the GST receipt for print/preview (plan.md §13). A print failure must
        // never lose the completed sale — the bill is already saved — so it only sets a status.
        await PrintReceiptAsync(receipt.SaleId, receipt.BillNo);

        // Refresh stock (FEFO previews) and customer balances after the sale, then clear the form
        // ready for the next customer (the completed-bill panel keeps a "Print receipt" button).
        await ReloadStockAsync();
        await ReloadCustomersAsync();
        ClearForm();
    }

    /// <summary>Re-generates and opens the receipt for the just-completed sale.</summary>
    private async Task PrintLastReceiptAsync()
    {
        if (_lastSaleId is not int saleId)
        {
            SetStatus("No completed sale to print yet.", isError: true);
            return;
        }

        await PrintReceiptAsync(saleId, null);
    }

    /// <summary>Reprints the receipt for an arbitrary earlier bill entered by its bill number.</summary>
    private async Task ReprintByBillNoAsync()
    {
        if (string.IsNullOrWhiteSpace(ReprintBillNo))
        {
            SetStatus("Enter a bill number to reprint.", isError: true);
            return;
        }

        MasterResult<int> found = await _billing.FindSaleIdByBillNoAsync(ReprintBillNo.Trim());
        if (!found.Succeeded)
        {
            SetStatus(found.Error, isError: true);
            return;
        }

        await PrintReceiptAsync(found.Value, ReprintBillNo.Trim());
    }

    /// <summary>
    /// Generates the GST receipt PDF for a saved sale and opens it in the default viewer for
    /// print/preview. Isolated so Complete Sale, "Print receipt", and reprint all share it. Never
    /// throws to the caller — any failure becomes a status message (plan.md §10, §13).
    /// </summary>
    private async Task PrintReceiptAsync(int saleId, string? billNoForName)
    {
        try
        {
            MasterResult<byte[]> pdf = await _invoices.GenerateReceiptPdf(saleId);
            if (!pdf.Succeeded)
            {
                SetStatus($"Sale saved, but the receipt could not be generated: {pdf.Error}", isError: true);
                return;
            }

            await _printer.PreviewAsync(pdf.Value!, billNoForName ?? $"sale-{saleId}");
        }
        catch (Exception ex)
        {
            SetStatus($"Sale saved, but printing failed: {ex.Message}", isError: true);
        }
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

        // Clear the Schedule-X capture too so it never carries to the next customer.
        XPatientName = string.Empty;
        XPatientAddress = string.Empty;
        XPatientPhone = string.Empty;
        XPrescriberName = string.Empty;
        XPrescriberAddress = string.Empty;
        XPrescriberRegNo = string.Empty;
        XPrescriptionNumber = string.Empty;
        XPrescriptionDate = DateTime.Today;
        XPrescriptionRetained = false;

        RaiseTotals();
        RaiseScheduleFlags();
    }

    /// <summary>Raises both schedule-driven prompt flags (H/H1 Rx and the strict Schedule-X panel).</summary>
    private void RaiseScheduleFlags()
    {
        OnPropertyChanged(nameof(RequiresPrescription));
        OnPropertyChanged(nameof(RequiresScheduleX));
    }

    /// <summary>All required Schedule-X capture fields present + the retained-copy box checked.</summary>
    private bool ScheduleXCaptureIsComplete() =>
        !string.IsNullOrWhiteSpace(XPatientName)
        && !string.IsNullOrWhiteSpace(XPatientAddress)
        && !string.IsNullOrWhiteSpace(XPrescriberName)
        && !string.IsNullOrWhiteSpace(XPrescriberAddress)
        && !string.IsNullOrWhiteSpace(XPrescriberRegNo)
        && !string.IsNullOrWhiteSpace(XPrescriptionNumber)
        && XPrescriptionDate.HasValue && XPrescriptionDate.Value != default
        && XPrescriptionRetained;

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
