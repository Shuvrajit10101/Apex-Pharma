using ApexPharma.Application.Services.Settings;

namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Passphrase backup key scheme (plan.md §14): derives the AES-256 key with PBKDF2 from the
/// Owner's session passphrase (<see cref="IBackupPassphraseHolder"/>) over the stored public salt.
/// The passphrase is never stored — only the salt and a verifier are — so the backup is portable
/// (any machine with the passphrase can restore) but the passphrase MUST be supplied this session.
/// The verifier catches a wrong passphrase with a clear error instead of an unusable key.
/// </summary>
public sealed class PassphraseBackupKeyProvider : IBackupKeyProvider
{
    private readonly ISettingsService _settings;
    private readonly IBackupPassphraseHolder _holder;

    public PassphraseBackupKeyProvider(ISettingsService settings, IBackupPassphraseHolder holder)
    {
        _settings = settings;
        _holder = holder;
    }

    /// <inheritdoc />
    public async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default)
    {
        string? passphrase = _holder.Passphrase;
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new InvalidOperationException("Enter the backup passphrase before backing up or restoring.");
        }

        string saltB64 = await _settings.GetStringAsync(BackupKeys.PassphraseSalt, string.Empty, cancellationToken);
        if (string.IsNullOrEmpty(saltB64))
        {
            throw new InvalidOperationException("No backup passphrase has been set up yet.");
        }

        byte[] salt = Convert.FromBase64String(saltB64);

        string verifierB64 = await _settings.GetStringAsync(BackupKeys.PassphraseVerifier, string.Empty, cancellationToken);
        if (!string.IsNullOrEmpty(verifierB64))
        {
            byte[] verifier = Convert.FromBase64String(verifierB64);
            if (!PassphraseKeyDerivation.VerifyPassphrase(passphrase, salt, verifier))
            {
                throw new InvalidOperationException("The backup passphrase is incorrect.");
            }
        }

        return PassphraseKeyDerivation.DeriveKey(passphrase, salt);
    }

    /// <summary>
    /// Establishes the passphrase scheme: generates a fresh salt + verifier and persists them
    /// (public values), switches <see cref="BackupKeys.KeyScheme"/> to <c>Passphrase</c>, and holds
    /// the passphrase for the session. The passphrase itself is never persisted.
    /// </summary>
    public async Task SetPassphraseAsync(string passphrase, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);

        byte[] salt = PassphraseKeyDerivation.NewSalt();
        byte[] verifier = PassphraseKeyDerivation.ComputeVerifier(passphrase, salt);

        await _settings.SetStringAsync(BackupKeys.PassphraseSalt, Convert.ToBase64String(salt), cancellationToken);
        await _settings.SetStringAsync(BackupKeys.PassphraseVerifier, Convert.ToBase64String(verifier), cancellationToken);
        await _settings.SetStringAsync(BackupKeys.KeyScheme, "Passphrase", cancellationToken);

        _holder.Set(passphrase);
    }
}
