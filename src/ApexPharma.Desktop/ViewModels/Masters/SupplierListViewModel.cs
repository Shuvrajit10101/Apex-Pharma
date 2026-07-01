using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Masters;

/// <summary>
/// List + search + add/update/deactivate for suppliers (plan.md §6.1, §7.2, §10). The
/// form fields feed a <see cref="SupplierInput"/>; all validation (name required, GSTIN
/// format) lives in <see cref="ISupplierService"/> — the UI just surfaces its messages.
/// </summary>
public class SupplierListViewModel : ViewModelBase
{
    private readonly ISupplierService _service;

    private UserRole _actingRole;
    private Supplier? _selected;
    private string _searchTerm = string.Empty;
    private string? _statusMessage;
    private bool _isError;

    // Editable form fields.
    private string _name = string.Empty;
    private string _gstin = string.Empty;
    private string _dlNumber = string.Empty;
    private string _phone = string.Empty;
    private string _email = string.Empty;
    private string _address = string.Empty;
    private string _stateCode = string.Empty;
    private decimal _openingBalance;

    public SupplierListViewModel(ISupplierService service)
    {
        _service = service;
        SaveCommand = new RelayCommand(async () => await SaveAsync());
        NewCommand = new RelayCommand(ClearForm);
        DeactivateCommand = new RelayCommand(async () => await DeactivateAsync());
        SearchCommand = new RelayCommand(async () => await SearchAsync());
    }

    public ObservableCollection<Supplier> Items { get; } = new();

    /// <summary>The selected row; selecting one loads it into the form for editing.</summary>
    public Supplier? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value) && value is not null)
            {
                LoadForm(value);
            }
        }
    }

    public string SearchTerm
    {
        get => _searchTerm;
        set => SetProperty(ref _searchTerm, value);
    }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Gstin { get => _gstin; set => SetProperty(ref _gstin, value); }
    public string DlNumber { get => _dlNumber; set => SetProperty(ref _dlNumber, value); }
    public string Phone { get => _phone; set => SetProperty(ref _phone, value); }
    public string Email { get => _email; set => SetProperty(ref _email, value); }
    public string Address { get => _address; set => SetProperty(ref _address, value); }
    public string StateCode { get => _stateCode; set => SetProperty(ref _stateCode, value); }
    public decimal OpeningBalance { get => _openingBalance; set => SetProperty(ref _openingBalance, value); }

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

    public ICommand SaveCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand SearchCommand { get; }

    public async Task InitializeAsync(UserRole actingRole)
    {
        _actingRole = actingRole;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var list = await _service.ListAsync();
        Fill(list);
    }

    private async Task SearchAsync()
    {
        var list = await _service.SearchAsync(SearchTerm);
        Fill(list);
    }

    private void Fill(System.Collections.Generic.IReadOnlyList<Supplier> list)
    {
        Items.Clear();
        foreach (var s in list)
        {
            Items.Add(s);
        }
    }

    private SupplierInput BuildInput() => new()
    {
        Name = Name,
        Gstin = Gstin,
        DlNumber = DlNumber,
        Phone = Phone,
        Email = Email,
        Address = Address,
        StateCode = StateCode,
        OpeningBalance = OpeningBalance
    };

    /// <summary>Creates a new supplier when none is selected; otherwise updates the selected one.</summary>
    private async Task SaveAsync()
    {
        SupplierInput input = BuildInput();
        if (Selected is null)
        {
            MasterResult<Supplier> created = await _service.CreateAsync(input, _actingRole);
            if (!created.Succeeded)
            {
                SetStatus(created.Error, isError: true);
                return;
            }

            SetStatus("Supplier added.", isError: false);
        }
        else
        {
            MasterResult updated = await _service.UpdateAsync(Selected.SupplierId, input, _actingRole);
            if (!updated.Succeeded)
            {
                SetStatus(updated.Error, isError: true);
                return;
            }

            SetStatus("Supplier updated.", isError: false);
        }

        ClearForm();
        await ReloadAsync();
    }

    private async Task DeactivateAsync()
    {
        if (Selected is null)
        {
            SetStatus("Select a supplier to deactivate.", isError: true);
            return;
        }

        MasterResult result = await _service.DeactivateAsync(Selected.SupplierId, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus("Supplier deactivated.", isError: false);
        ClearForm();
        await ReloadAsync();
    }

    private void LoadForm(Supplier s)
    {
        Name = s.Name;
        Gstin = s.Gstin ?? string.Empty;
        DlNumber = s.DlNumber ?? string.Empty;
        Phone = s.Phone ?? string.Empty;
        Email = s.Email ?? string.Empty;
        Address = s.Address ?? string.Empty;
        StateCode = s.StateCode ?? string.Empty;
        OpeningBalance = s.OpeningBalance;
    }

    private void ClearForm()
    {
        Selected = null;
        Name = Gstin = DlNumber = Phone = Email = Address = StateCode = string.Empty;
        OpeningBalance = 0m;
    }

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }
}
