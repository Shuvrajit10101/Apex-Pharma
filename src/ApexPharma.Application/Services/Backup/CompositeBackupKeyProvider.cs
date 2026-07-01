using ApexPharma.Application.Services.Settings;

namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Dispatches key acquisition to the configured scheme (plan.md §14): <c>Passphrase</c> (PBKDF2 —
/// portable, needs the Owner's passphrase this session) or <c>Dpapi</c> (default — a random
/// data-key sealed to this Windows user/machine, no passphrase to remember). Reading
/// <see cref="BackupKeys.KeyScheme"/> at call time means a fresh install uses DPAPI automatically,
/// and switching to a passphrase later just changes one setting. This is the provider the
/// <see cref="BackupService"/> depends on.
/// </summary>
public sealed class CompositeBackupKeyProvider : IBackupKeyProvider
{
    private readonly ISettingsService _settings;
    private readonly DpapiBackupKeyProvider _dpapi;
    private readonly PassphraseBackupKeyProvider _passphrase;

    public CompositeBackupKeyProvider(
        ISettingsService settings,
        DpapiBackupKeyProvider dpapi,
        PassphraseBackupKeyProvider passphrase)
    {
        _settings = settings;
        _dpapi = dpapi;
        _passphrase = passphrase;
    }

    /// <inheritdoc />
    public async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default)
    {
        string scheme = await _settings.GetStringAsync(BackupKeys.KeyScheme, "Dpapi", cancellationToken);
        return string.Equals(scheme, "Passphrase", StringComparison.OrdinalIgnoreCase)
            ? await _passphrase.GetKeyAsync(cancellationToken)
            : await _dpapi.GetKeyAsync(cancellationToken);
    }
}
