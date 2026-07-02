namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// A backup file on disk, for the "recent backups" list in the UI (plan.md §6.1). Carries the
/// path, timestamp (parsed from the filename), and size so the Owner can see what exists and pick
/// one to restore.
/// </summary>
public sealed record BackupInfo(string Path, string FileName, DateTime TimestampLocal, long SizeBytes);
