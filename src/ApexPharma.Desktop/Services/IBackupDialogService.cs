namespace ApexPharma.Desktop.Services;

/// <summary>
/// Owns the Win32 folder/file pickers and confirmation prompt for the Backup panel (plan.md §10,
/// §13). Kept behind an interface so the <see cref="ViewModels.Settings.BackupViewModel"/> stays
/// free of WPF dialog types and is unit-testable (the view-model just asks "pick a folder" /
/// "confirm this restore").
/// </summary>
public interface IBackupDialogService
{
    /// <summary>Prompts for a folder; returns the chosen path or null if cancelled.</summary>
    string? PickFolder(string title, string? initialPath = null);

    /// <summary>Prompts to open a <c>.bak</c> file; returns the chosen path or null if cancelled.</summary>
    string? PickBackupFile(string title, string? initialPath = null);

    /// <summary>Shows a yes/no confirmation (used before a destructive restore). True = confirmed.</summary>
    bool Confirm(string message, string caption);
}
