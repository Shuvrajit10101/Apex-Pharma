using System.Windows.Controls;

namespace ApexPharma.Desktop.Views;

/// <summary>
/// The Reports hub view (plan.md §11, §14), embedded in the single-window shell. A report-type
/// selector + date-range picker drive one of five read-only report grids with a summary line and
/// CSV/PDF export. Its <see cref="ViewModels.Reports.ReportsViewModel"/> is supplied as the
/// DataContext by the shell's DataTemplate; the view performs no data access or logic.
/// </summary>
public partial class ReportsView : UserControl
{
    public ReportsView() => InitializeComponent();
}
