namespace ApexPharma.Application.Services;

/// <summary>
/// Skeleton <see cref="IBackupService"/>. Implemented in Phase 1/3 (scheduled local
/// backup, one-click restore, optional encrypted cloud) (plan.md §13).
/// </summary>
public class BackupService : IBackupService
{
    public Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task RestoreBackupAsync(string backupFilePath, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
