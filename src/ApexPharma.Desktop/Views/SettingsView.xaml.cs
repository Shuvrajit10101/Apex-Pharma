using System.Windows.Controls;
using ApexPharma.Desktop.ViewModels.Settings;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// The Settings (pharmacy profile + backup) view (plan.md §6.1, §10, §13), embedded in the
/// single-window shell. Hosts the editable profile form and the Owner-only Backup panel. Its
/// <see cref="ViewModels.Settings.SettingsViewModel"/> is supplied as the DataContext by the shell's
/// DataTemplate; the nav item and saves are gated on the Owner-only <c>ManageSettings</c> permission
/// (plan.md §4). The passphrase is read from the <c>PasswordBox</c> at click time (WPF has no secure
/// password binding) and passed to the view-model write-only — it is never bound or displayed.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void SetPassphrase_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            await vm.Backup.SetPassphraseAsync(PassphraseBox.Password);
            PassphraseBox.Clear();
        }
    }
}
