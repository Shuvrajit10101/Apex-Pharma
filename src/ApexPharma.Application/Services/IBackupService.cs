namespace ApexPharma.Application.Services;

/// <summary>
/// Backup and restore (plan.md §6.1, §13). Automatic daily local backup, optional
/// encrypted cloud backup, and a tested one-click restore — insurance against PC
/// failure or theft.
/// </summary>
public interface IBackupService
{
    /// <summary>Creates a backup of the database; returns the backup file path.</summary>
    Task<string> CreateBackupAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores the database from a backup file (one-click restore).</summary>
    Task RestoreBackupAsync(string backupFilePath, CancellationToken cancellationToken = default);
}
