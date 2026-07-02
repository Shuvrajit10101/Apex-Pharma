using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Returns;

/// <summary>
/// Sales-return view-model (plan.md §6.1, §10) — reachable from Billing. The operator enters a
/// bill number to load its sold lines, sets a return quantity per line + a reason, and confirms.
/// All validation, batch restock, GST/khata reversal, and the atomic save live in
/// <see cref="ISaleReturnService"/> — no money/stock logic here (plan.md §8). Permission-gated on
/// <see cref="Permission.DoBilling"/> (activation is refused by the navigation service otherwise).
/// </summary>
public class SalesReturnViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly ISaleReturnService _returns;
    private readonly ISessionContext _session;

    private UserRole _actingRole;
    private string _billNo = string.Empty;
    private string _reason = string.Empty;
    private string? _statusMessage;
    private bool _isError;
    private bool _billLoaded;
    private int _loadedSaleId;
    private bool _loadedIsCredit;

    public SalesReturnViewModel(ISaleReturnService returns, ISessionContext session)
    {
        _returns = returns;
        _session = session;

        FindBillCommand = new RelayCommand(async () => await FindBillAsync());
        ConfirmReturnCommand = new RelayCommand(async () => await ConfirmAsync());
        ClearCommand = new RelayCommand(ClearForm);
    }

    /// <summary>The loaded bill's returnable lines (each with an editable return qty).</summary>
    public ObservableCollection<SalesReturnLineViewModel> Lines { get; } = new();

    public string BillNo
    {
        get => _billNo;
        set => SetProperty(ref _billNo, value);
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

    /// <summary>True once a bill has been loaded, so the lines grid + confirm are enabled.</summary>
    public bool BillLoaded
    {
        get => _billLoaded;
        private set => SetProperty(ref _billLoaded, value);
    }

    public ICommand FindBillCommand { get; }
    public ICommand ConfirmReturnCommand { get; }
    public ICommand ClearCommand { get; }

    /// <inheritdoc />
    public Task ActivateAsync(UserRole role)
    {
        _actingRole = role;
        return Task.CompletedTask;
    }

    private async Task FindBillAsync()
    {
        Lines.Clear();
        BillLoaded = false;

        MasterResult<SaleReturnableLines> result = await _returns.GetReturnableLinesAsync(BillNo);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SaleReturnableLines data = result.Value!;
        _loadedSaleId = data.SaleId;
        _loadedIsCredit = data.IsCredit;
        foreach (SaleReturnableLine line in data.Lines)
        {
            Lines.Add(new SalesReturnLineViewModel(line));
        }

        BillLoaded = true;
        SetStatus(
            $"Bill {data.BillNo} loaded — {Lines.Count} line(s). " +
            (data.IsCredit ? "Credit sale: a return reduces the customer's khata." : "Cash sale."),
            isError: false);
    }

    private async Task ConfirmAsync()
    {
        if (!BillLoaded)
        {
            SetStatus("Find a bill first.", isError: true);
            return;
        }

        var requested = Lines
            .Where(l => l.ReturnQty > 0)
            .Select(l => new SaleReturnLineInput { SaleItemId = l.SaleItemId, Qty = l.ReturnQty })
            .ToList();

        if (requested.Count == 0)
        {
            SetStatus("Enter a return quantity on at least one line.", isError: true);
            return;
        }

        // Client-side guard for a friendlier message; the service is the real boundary.
        foreach (SalesReturnLineViewModel line in Lines.Where(l => l.ReturnQty > 0))
        {
            if (line.ReturnQty > line.RemainingQty)
            {
                SetStatus($"'{line.ProductName}': cannot return {line.ReturnQty:0.##}, only {line.RemainingQty:0.##} remain.", isError: true);
                return;
            }
        }

        var input = new SaleReturnInput { BillNo = BillNo, Reason = Reason, Lines = requested };
        MasterResult<SaleReturnReceipt> result = await _returns.ProcessSaleReturnAsync(input, _session.UserId, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SaleReturnReceipt r = result.Value;
        string khata = r.KhataReduced > 0 ? $" Khata reduced by {r.KhataReduced:0.00}." : string.Empty;
        SetStatus(
            $"Returned {r.TotalQty:0.##} unit(s) across {r.LinesReturned} line(s). Restocked. " +
            $"Refund/credit note {r.TotalRefund:0.00} (CGST {r.Cgst:0.00} + SGST {r.Sgst:0.00}).{khata}",
            isError: false);

        // Reload the bill so the remaining-qty figures reflect the just-processed return.
        await FindBillAsync();
    }

    private void ClearForm()
    {
        BillNo = string.Empty;
        Reason = string.Empty;
        Lines.Clear();
        BillLoaded = false;
        _loadedSaleId = 0;
        _loadedIsCredit = false;
        SetStatus(null, isError: false);
    }

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }
}
