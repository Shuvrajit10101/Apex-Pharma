using ApexPharma.Application.Services.Backup;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Application.Services;

/// <summary>
/// Backup and restore (plan.md §6.1, §13, §14). Automatic daily local + optional cloud-synced
/// encrypted backup, a retention window, and a tested one-click restore — insurance against PC
/// failure or theft. Backup/restore are Owner-only (gated on <see cref="Permission.Backup"/>).
/// Implemented by <see cref="Backup.BackupService"/> (AES-256-GCM, DPAPI/PBKDF2 key, VACUUM-INTO
/// snapshot, atomic swap).
/// </summary>
public interface IBackupService
{
    /// <summary>Creates an encrypted backup of the database; returns the local backup file path.</summary>
    Task<string> CreateBackupAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates an encrypted backup as the acting role (Owner-only); returns the local path.</summary>
    Task<string> CreateBackupAsync(UserRole actingRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an encrypted backup as the acting role (Owner-only) and returns the full
    /// <see cref="Backup.BackupResult"/>: the local path plus whether the optional off-site cloud
    /// copy succeeded. The local backup always succeeds (a failure throws); a cloud-copy failure is
    /// reported via <see cref="Backup.BackupResult.CloudWarning"/> rather than being swallowed.
    /// </summary>
    Task<Backup.BackupResult> CreateBackupWithResultAsync(UserRole actingRole, CancellationToken cancellationToken = default);

    /// <summary>Restores the database from a backup file (legacy contract; throws on failure).</summary>
    Task RestoreBackupAsync(string backupFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and stages a restore of the LIVE DB from <paramref name="backupPath"/> (applied on
    /// next startup). Returns a failed <see cref="MasterResult"/> — never throws — for an
    /// unauthorized caller, a wrong key, or an invalid/corrupt backup, leaving live data untouched.
    /// </summary>
    Task<MasterResult> RestoreFromBackupAsync(string backupPath, UserRole actingRole, CancellationToken cancellationToken = default);

    /// <summary>Restores a backup directly into <paramref name="targetDbPath"/> (decrypt → validate → atomic move).</summary>
    Task<MasterResult> RestoreToAsync(string backupPath, string targetDbPath, UserRole actingRole, CancellationToken cancellationToken = default);

    /// <summary>Runs the auto-daily backup if enabled and none exists for today. Non-throwing; returns the path or null.</summary>
    Task<string?> RunDailyIfDueAsync(UserRole actingRole, CancellationToken cancellationToken = default);

    /// <summary>Applies a pending restore at startup (before EF opens the DB). Returns true if one was applied.</summary>
    bool ApplyPendingRestoreIfAnyAsync();

    /// <summary>Lists recent backups in the local folder, newest first (for the UI).</summary>
    Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(CancellationToken cancellationToken = default);
}
