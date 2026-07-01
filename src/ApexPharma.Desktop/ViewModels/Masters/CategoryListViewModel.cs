using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Masters;

/// <summary>
/// List + add/rename/deactivate for categories (plan.md §6.1, §10). All rules live in
/// <see cref="ICategoryService"/>; this view-model only marshals input and surfaces the
/// service's <see cref="MasterResult"/> messages — no data/business logic in the UI (§8).
/// </summary>
public class CategoryListViewModel : ViewModelBase
{
    private readonly ICategoryService _service;

    private UserRole _actingRole;
    private string _newName = string.Empty;
    private Category? _selected;
    private string? _statusMessage;
    private bool _isError;

    public CategoryListViewModel(ICategoryService service)
    {
        _service = service;
        AddCommand = new RelayCommand(async () => await AddAsync());
        RenameCommand = new RelayCommand(async () => await RenameAsync());
        DeactivateCommand = new RelayCommand(async () => await DeactivateAsync());
    }

    /// <summary>The active categories shown in the list.</summary>
    public ObservableCollection<Category> Items { get; } = new();

    /// <summary>Name for the add box (also the rename target for the selected row).</summary>
    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value);
    }

    /// <summary>The selected list row (rename/deactivate target).</summary>
    public Category? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>Last operation message (green on success, red on error).</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>True when <see cref="StatusMessage"/> is an error (drives red text in the view).</summary>
    public bool IsError
    {
        get => _isError;
        private set => SetProperty(ref _isError, value);
    }

    public ICommand AddCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeactivateCommand { get; }

    /// <summary>Applies the signed-in role (drives RBAC) and loads the list.</summary>
    public async Task InitializeAsync(UserRole actingRole)
    {
        _actingRole = actingRole;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var list = await _service.ListAsync();
        Items.Clear();
        foreach (var c in list)
        {
            Items.Add(c);
        }
    }

    private async Task AddAsync()
    {
        MasterResult<Category> result = await _service.CreateAsync(NewName, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        NewName = string.Empty;
        SetStatus("Category added.", isError: false);
        await ReloadAsync();
    }

    private async Task RenameAsync()
    {
        if (Selected is null)
        {
            SetStatus("Select a category to rename.", isError: true);
            return;
        }

        MasterResult result = await _service.RenameAsync(Selected.CategoryId, NewName, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        NewName = string.Empty;
        SetStatus("Category renamed.", isError: false);
        await ReloadAsync();
    }

    private async Task DeactivateAsync()
    {
        if (Selected is null)
        {
            SetStatus("Select a category to deactivate.", isError: true);
            return;
        }

        MasterResult result = await _service.DeactivateAsync(Selected.CategoryId, _actingRole);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus("Category deactivated.", isError: false);
        await ReloadAsync();
    }

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }
}
