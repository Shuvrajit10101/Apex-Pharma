using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// The default post-login landing view (plan.md §10). Bound to a
/// <see cref="ViewModels.LandingViewModel"/> and shown in the shell's content region
/// until the user picks a module from the left nav.
/// </summary>
public partial class LandingView : UserControl
{
    public LandingView() => InitializeComponent();
}
