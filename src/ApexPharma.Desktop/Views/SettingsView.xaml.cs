using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// The Settings (pharmacy profile) view (plan.md §6.1, §10), embedded in the single-window shell.
/// Hosts the editable profile form (name, address, GSTIN, DL number, phone, invoice footer,
/// near-expiry window, tax rounding). Its <see cref="ViewModels.Settings.SettingsViewModel"/> is
/// supplied as the DataContext by the shell's DataTemplate; the nav item and the save are gated on
/// the Owner-only <c>ManageSettings</c> permission (plan.md §4).
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
