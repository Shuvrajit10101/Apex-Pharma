using System.Threading.Tasks;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Masters;

/// <summary>
/// Host view-model for the Masters area (plan.md §10). Aggregates the four catalog
/// list view-models and forwards the signed-in role so each can gate its mutations
/// via the services (plan.md §4). The window binds a tab per child.
/// </summary>
public class MastersViewModel : ViewModelBase
{
    public MastersViewModel(
        ProductListViewModel products,
        CategoryListViewModel categories,
        ManufacturerListViewModel manufacturers,
        SupplierListViewModel suppliers)
    {
        Products = products;
        Categories = categories;
        Manufacturers = manufacturers;
        Suppliers = suppliers;
    }

    public ProductListViewModel Products { get; }
    public CategoryListViewModel Categories { get; }
    public ManufacturerListViewModel Manufacturers { get; }
    public SupplierListViewModel Suppliers { get; }

    /// <summary>Loads every tab's data for the signed-in role.</summary>
    public async Task InitializeAsync(UserRole actingRole)
    {
        // Categories/manufacturers first so the product tab's pickers are populated.
        await Categories.InitializeAsync(actingRole);
        await Manufacturers.InitializeAsync(actingRole);
        await Suppliers.InitializeAsync(actingRole);
        await Products.InitializeAsync(actingRole);
    }
}
