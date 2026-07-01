namespace ApexPharma.Desktop.ViewModels;

/// <summary>
/// View-model for the shell window. Holds the window title today; module navigation
/// and role-aware state are added in Phase 1 (plan.md §10).
/// </summary>
public class MainViewModel : ViewModelBase
{
    private string _title = "Apex-Pharma — Pharmacy Management";

    /// <summary>Window title shown in the title bar (bound in MainWindow.xaml).</summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}
