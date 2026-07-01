using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Masters;

/// <summary>
/// List + add/rename/deactivate for manufacturers (plan.md §6.1, §10). Mirrors
/// <see cref="CategoryListViewModel"/>; all rules live in <see cref="IManufacturerService"/>.
/// </summary>
public class ManufacturerListViewModel : ViewModelBase
{
    private readonly IManufacturerService _service;

    private UserRole _actingRole;
    private string _newName = string.Empty;
    private Manufacturer? _selected;
    private string? _statusMessage;
    private bool _isError;

    public ManufacturerListViewModel(IManufacturerService service)
    {
        _service = service;
        AddCommand = new RelayCommand(async () => await AddAsync());
        RenameCommand = new RelayCommand(async () => await RenameAsync());
        DeactivateCommand = new RelayCommand(async () => await DeactivateAsync());
    }

    public ObservableCollection<Manufacturer> Items { get; } = new();

    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value);
    }

    public Manufacturer? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
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

    public ICommand AddCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeactivateCommand { get; }

    public async Task InitializeAsync(UserRole actingRole)
    {
        _actingRole = actingRole;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var list = await _service.ListAsync();
        Items.Clear();
        foreach (var m in list)
        {
            Items.Add(m);
        }
    }

    private async Task AddAsync()
    {
        MasterResult<Manufacturer> result = await _service.CreateAsync(NewName, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        NewName = string.Empty;
        SetStatus("Manufacturer added.", isError: false);
        await ReloadAsync();
    }

    private async Task RenameAsync()
    {
        if (Selected is null)
        {
            SetStatus("Select a manufacturer to rename.", isError: true);
            return;
        }

        MasterResult result = await _service.RenameAsync(Selected.ManufacturerId, NewName, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        NewName = string.Empty;
        SetStatus("Manufacturer renamed.", isError: false);
        await ReloadAsync();
    }

    private async Task DeactivateAsync()
    {
        if (Selected is null)
        {
            SetStatus("Select a manufacturer to deactivate.", isError: true);
            return;
        }

        MasterResult result = await _service.DeactivateAsync(Selected.ManufacturerId, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus("Manufacturer deactivated.", isError: false);
        await ReloadAsync();
    }

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }
}
