using System.Windows;

namespace ApexPharma.Desktop;

/// <summary>
/// The application shell window. Navigation buttons are placeholders for now; the
/// module views (Billing, Inventory, Purchases, Reports, Settings) are built in
/// Phase 1 (plan.md §10).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
