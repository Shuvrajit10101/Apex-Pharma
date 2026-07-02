using System.Security.Cryptography;
using ApexPharma.Application.Services.Settings;

namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Default backup key scheme (plan.md §14): a random 32-byte AES-256 <b>data key</b> is generated
/// once, wrapped with Windows DPAPI (<see cref="IDpapiProtector"/>, CurrentUser), and the wrapped
/// blob persisted (base64) in the <see cref="BackupKeys.WrappedKey"/> setting. The plaintext key
/// never touches disk or the DB — only the DPAPI-sealed blob does, and that blob is decryptable
/// ONLY by this Windows user on this machine. On every backup/restore the provider unwraps the blob
/// to recover the key in memory. This needs no passphrase for the Owner to remember, which suits a
/// single-PC counter deployment.
/// </summary>
public sealed class DpapiBackupKeyProvider : IBackupKeyProvider
{
    private readonly ISettingsService _settings;
    private readonly IDpapiProtector _dpapi;

    // Serialises first-use key minting so concurrent first-ever backups can never each mint a
    // different key and persist the loser — which would leave a backup encrypted under a key that
    // was never stored (unrecoverable). All callers converge on the ONE wrapped key.
    private readonly SemaphoreSlim _mintGate = new(1, 1);

    public DpapiBackupKeyProvider(ISettingsService settings, IDpapiProtector dpapi)
    {
        _settings = settings;
        _dpapi = dpapi;
    }

    /// <inheritdoc />
    public async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default)
    {
        // Fast path: a wrapped key already exists — unwrap it without taking the mint lock.
        byte[]? existing = await TryUnwrapExistingAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        // First use on this machine. Single-flight the mint: only one caller creates + persists the
        // key; everyone else waits, then re-reads the freshly-persisted blob so ALL callers return
        // the SAME key and exactly one wrapped key is ever stored.
        await _mintGate.WaitAsync(cancellationToken);
        try
        {
            // Double-check under the lock: a racing caller may have persisted the key while we waited.
            byte[]? justPersisted = await TryUnwrapExistingAsync(cancellationToken);
            if (justPersisted is not null)
            {
                return justPersisted;
            }

            byte[] key = RandomNumberGenerator.GetBytes(BackupCrypto.KeySize);
            byte[] sealedBlob = _dpapi.Protect(key);
            await _settings.SetStringAsync(BackupKeys.WrappedKey, Convert.ToBase64String(sealedBlob), cancellationToken);
            await _settings.SetStringAsync(BackupKeys.KeyScheme, "Dpapi", cancellationToken);
            return key;
        }
        finally
        {
            _mintGate.Release();
        }
    }

    /// <summary>
    /// Returns the unwrapped key if a wrapped-key blob is already persisted, else null. Unwrapping
    /// throws <see cref="System.Security.Cryptography.CryptographicException"/> if this isn't the
    /// owning user/machine — a clear, correct failure rather than a silently wrong key.
    /// </summary>
    private async Task<byte[]?> TryUnwrapExistingAsync(CancellationToken cancellationToken)
    {
        string wrappedBase64 = await _settings.GetStringAsync(BackupKeys.WrappedKey, string.Empty, cancellationToken);
        if (string.IsNullOrEmpty(wrappedBase64))
        {
            return null;
        }

        byte[] wrapped = Convert.FromBase64String(wrappedBase64);
        return _dpapi.Unprotect(wrapped);
    }
}
