namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Outcome of a successful <see cref="IBackupService.CreateBackupWithResultAsync"/> (plan.md §6.1,
/// §14). The local backup always succeeded (a create failure throws instead of returning this), but
/// the optional off-site copy to the cloud-synced folder can fail independently — a missing or
/// unwritable OneDrive/Google-Drive folder must NOT be silently swallowed, or the Owner would
/// believe off-site backups exist when only local ones were written. When that happens
/// <see cref="CloudCopySucceeded"/> is false and <see cref="CloudWarning"/> carries a clear,
/// user-facing reason; the local backup at <see cref="LocalPath"/> is nonetheless durable.
/// </summary>
public sealed record BackupResult(string LocalPath, bool CloudCopySucceeded, string? CloudWarning)
{
    /// <summary>Result when no cloud folder is configured (local-only backup — nothing to warn about).</summary>
    public static BackupResult LocalOnly(string localPath) => new(localPath, CloudCopySucceeded: true, CloudWarning: null);

    /// <summary>Result when the configured cloud copy succeeded alongside the local backup.</summary>
    public static BackupResult CloudOk(string localPath) => new(localPath, CloudCopySucceeded: true, CloudWarning: null);

    /// <summary>Result when the local backup succeeded but the configured cloud copy failed.</summary>
    public static BackupResult CloudFailed(string localPath, string warning) => new(localPath, CloudCopySucceeded: false, CloudWarning: warning);
}
