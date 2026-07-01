namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Wiring for <see cref="BackupService"/> that isn't user-configurable (plan.md §13): the absolute
/// path of the live SQLite database to snapshot, and the default local backup folder used when the
/// Owner hasn't picked one yet. Supplied once at composition (the Desktop app points it at
/// <c>%LocalAppData%\ApexPharma\apexpharma.db</c>). Kept as a tiny options object so the service
/// takes no hard dependency on <c>App</c> and is trivially unit-testable with temp paths.
/// </summary>
public sealed class BackupOptions
{
    public BackupOptions(string liveDatabasePath, string defaultLocalFolder)
    {
        LiveDatabasePath = liveDatabasePath;
        DefaultLocalFolder = defaultLocalFolder;
    }

    /// <summary>Absolute path to the live SQLite DB that backups snapshot.</summary>
    public string LiveDatabasePath { get; }

    /// <summary>Fallback local backup folder used when the <c>Backup.LocalFolder</c> setting is blank.</summary>
    public string DefaultLocalFolder { get; }
}
