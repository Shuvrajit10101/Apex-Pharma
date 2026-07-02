using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ApexPharma.Desktop.Services;

/// <summary>
/// Default <see cref="IBackupDialogService"/> over the WPF <see cref="Microsoft.Win32"/> common
/// dialogs. Singleton stateless UI helper — the picker/prompt logic lives here so view-models never
/// reference dialog types (plan.md §8 layering, §10).
/// </summary>
public sealed class BackupDialogService : IBackupDialogService
{
    public string? PickFolder(string title, string? initialPath = null)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            dialog.InitialDirectory = initialPath;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickBackupFile(string title, string? initialPath = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Apex-Pharma backup (*.bak)|*.bak|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            dialog.InitialDirectory = initialPath;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool Confirm(string message, string caption) =>
        MessageBox.Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
