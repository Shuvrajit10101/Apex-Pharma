namespace ApexPharma.Desktop.ViewModels;

/// <summary>
/// The default post-login landing view-model (plan.md §10). Shows a simple welcome /
/// dashboard placeholder ("Select a module to begin") so the shell always has content
/// after sign-in, before any module is chosen. Kept deliberately minimal — a richer KPI
/// dashboard is a later-phase enhancement (plan.md §11, §15 Phase 3).
/// </summary>
public class LandingViewModel : ViewModelBase
{
    /// <summary>Welcome heading shown on the landing view.</summary>
    public string Heading => "Welcome to Apex-Pharma";

    /// <summary>Guidance prompting the user to pick a module from the left nav.</summary>
    public string Prompt => "Select a module to begin.";
}
