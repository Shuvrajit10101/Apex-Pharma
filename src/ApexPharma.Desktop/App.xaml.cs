using System.Windows;

namespace ApexPharma.Desktop;

/// <summary>
/// WPF application entry point. Dependency injection wiring for services and the
/// DbContext is added in Phase 1 (plan.md §8 layering).
/// </summary>
public partial class App : System.Windows.Application
{
    // Note: base type is fully qualified because the project reference
    // ApexPharma.Application makes the bare name `Application` resolve to that
    // namespace instead of System.Windows.Application (CS0118).
}
