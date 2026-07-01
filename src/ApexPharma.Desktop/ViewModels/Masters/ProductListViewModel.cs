using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Masters;

/// <summary>
/// List + search + add/update/deactivate for the product catalog (plan.md §6.1, §7.2,
/// §10). Category/manufacturer/schedule/GST-slab choices are offered as pickers; all
/// validation (name, GST slab, reorder level, unique barcode, FK existence) lives in
/// <see cref="IProductService"/> — no business logic in the UI (plan.md §8).
/// </summary>
public class ProductListViewModel : ViewModelBase
{
    private readonly IProductService _service;
    private readonly ICategoryService _categories;
    private readonly IManufacturerService _manufacturers;

    private UserRole _actingRole;
    private Product? _selected;
    private string _searchTerm = string.Empty;
    private string? _statusMessage;
    private bool _isError;

    // Editable form fields.
    private string _name = string.Empty;
    private string _genericName = string.Empty;
    private Category? _selectedCategory;
    private Manufacturer? _selectedManufacturer;
    private string _hsnCode = string.Empty;
    private decimal _gstRate;
    private DrugSchedule _schedule = DrugSchedule.None;
    private string _dosageForm = string.Empty;
    private string _strength = string.Empty;
    private string _packSize = string.Empty;
    private string _unit = string.Empty;
    private string _rackLocation = string.Empty;
    private int _reorderLevel;
    private string _barcode = string.Empty;

    public ProductListViewModel(
        IProductService service,
        ICategoryService categories,
        IManufacturerService manufacturers)
    {
        _service = service;
        _categories = categories;
        _manufacturers = manufacturers;

        SaveCommand = new RelayCommand(async () => await SaveAsync());
        NewCommand = new RelayCommand(ClearForm);
        DeactivateCommand = new RelayCommand(async () => await DeactivateAsync());
        SearchCommand = new RelayCommand(async () => await SearchAsync());
    }

    public ObservableCollection<Product> Items { get; } = new();
    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<Manufacturer> Manufacturers { get; } = new();

    /// <summary>The GST slabs the picker offers (plan.md §6.1 India slabs).</summary>
    public decimal[] GstRates { get; } = { 0m, 5m, 12m, 18m, 28m };

    /// <summary>The drug-schedule choices for the picker.</summary>
    public DrugSchedule[] Schedules { get; } =
        { DrugSchedule.None, DrugSchedule.H, DrugSchedule.H1, DrugSchedule.X };

    public Product? Selected
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

    public string SearchTerm { get => _searchTerm; set => SetProperty(ref _searchTerm, value); }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string GenericName { get => _genericName; set => SetProperty(ref _genericName, value); }
    public Category? SelectedCategory { get => _selectedCategory; set => SetProperty(ref _selectedCategory, value); }
    public Manufacturer? SelectedManufacturer { get => _selectedManufacturer; set => SetProperty(ref _selectedManufacturer, value); }
    public string HsnCode { get => _hsnCode; set => SetProperty(ref _hsnCode, value); }
    public decimal GstRate { get => _gstRate; set => SetProperty(ref _gstRate, value); }
    public DrugSchedule Schedule { get => _schedule; set => SetProperty(ref _schedule, value); }
    public string DosageForm { get => _dosageForm; set => SetProperty(ref _dosageForm, value); }
    public string Strength { get => _strength; set => SetProperty(ref _strength, value); }
    public string PackSize { get => _packSize; set => SetProperty(ref _packSize, value); }
    public string Unit { get => _unit; set => SetProperty(ref _unit, value); }
    public string RackLocation { get => _rackLocation; set => SetProperty(ref _rackLocation, value); }
    public int ReorderLevel { get => _reorderLevel; set => SetProperty(ref _reorderLevel, value); }
    public string Barcode { get => _barcode; set => SetProperty(ref _barcode, value); }

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
        await LoadLookupsAsync();
        await ReloadAsync();
    }

    private async Task LoadLookupsAsync()
    {
        Categories.Clear();
        foreach (var c in await _categories.ListAsync())
        {
            Categories.Add(c);
        }

        Manufacturers.Clear();
        foreach (var m in await _manufacturers.ListAsync())
        {
            Manufacturers.Add(m);
        }
    }

    private async Task ReloadAsync()
    {
        Fill(await _service.ListAsync());
    }

    private async Task SearchAsync()
    {
        Fill(await _service.SearchAsync(SearchTerm));
    }

    private void Fill(System.Collections.Generic.IReadOnlyList<Product> list)
    {
        Items.Clear();
        foreach (var p in list)
        {
            Items.Add(p);
        }
    }

    private ProductInput BuildInput() => new()
    {
        Name = Name,
        GenericName = GenericName,
        CategoryId = SelectedCategory?.CategoryId ?? 0,
        ManufacturerId = SelectedManufacturer?.ManufacturerId ?? 0,
        HsnCode = HsnCode,
        GstRate = GstRate,
        Schedule = Schedule,
        DosageForm = DosageForm,
        Strength = Strength,
        PackSize = PackSize,
        Unit = Unit,
        RackLocation = RackLocation,
        ReorderLevel = ReorderLevel,
        Barcode = Barcode
    };

    private async Task SaveAsync()
    {
        ProductInput input = BuildInput();
        if (Selected is null)
        {
            MasterResult<Product> created = await _service.CreateAsync(input, _actingRole);
            if (!created.Succeeded)
            {
                SetStatus(created.Error, isError: true);
                return;
            }

            SetStatus("Product added.", isError: false);
        }
        else
        {
            MasterResult updated = await _service.UpdateAsync(Selected.ProductId, input, _actingRole);
            if (!updated.Succeeded)
            {
                SetStatus(updated.Error, isError: true);
                return;
            }

            SetStatus("Product updated.", isError: false);
        }

        ClearForm();
        await ReloadAsync();
    }

    private async Task DeactivateAsync()
    {
        if (Selected is null)
        {
            SetStatus("Select a product to deactivate.", isError: true);
            return;
        }

        MasterResult result = await _service.DeactivateAsync(Selected.ProductId, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus("Product deactivated.", isError: false);
        ClearForm();
        await ReloadAsync();
    }

    private void LoadForm(Product p)
    {
        Name = p.Name;
        GenericName = p.GenericName ?? string.Empty;
        SelectedCategory = FindCategory(p.CategoryId);
        SelectedManufacturer = FindManufacturer(p.ManufacturerId);
        HsnCode = p.HsnCode ?? string.Empty;
        GstRate = p.GstRate;
        Schedule = p.Schedule;
        DosageForm = p.DosageForm ?? string.Empty;
        Strength = p.Strength ?? string.Empty;
        PackSize = p.PackSize ?? string.Empty;
        Unit = p.Unit ?? string.Empty;
        RackLocation = p.RackLocation ?? string.Empty;
        ReorderLevel = p.ReorderLevel;
        Barcode = p.Barcode ?? string.Empty;
    }

    private Category? FindCategory(int id)
    {
        foreach (var c in Categories)
        {
            if (c.CategoryId == id)
            {
                return c;
            }
        }

        return null;
    }

    private Manufacturer? FindManufacturer(int id)
    {
        foreach (var m in Manufacturers)
        {
            if (m.ManufacturerId == id)
            {
                return m;
            }
        }

        return null;
    }

    private void ClearForm()
    {
        Selected = null;
        Name = GenericName = HsnCode = DosageForm = Strength = PackSize = Unit = RackLocation = Barcode = string.Empty;
        SelectedCategory = null;
        SelectedManufacturer = null;
        GstRate = 0m;
        Schedule = DrugSchedule.None;
        ReorderLevel = 0;
    }

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }
}
