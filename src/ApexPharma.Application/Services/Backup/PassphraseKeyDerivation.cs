using System.Security.Cryptography;
using System.Text;

namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// PBKDF2 key derivation for the passphrase backup scheme (plan.md §14). The AES-256 key is
/// derived from the Owner's passphrase with <b>PBKDF2-HMAC-SHA256, 200,000 iterations</b> over a
/// random 16-byte salt. The passphrase is NEVER stored — only the public salt is (safe to store)
/// plus a small verifier so a wrong passphrase is caught with a clear message instead of producing
/// an unusable key. Derivation is deterministic given passphrase + salt, so restore re-derives the
/// same key on any machine that has the passphrase (this scheme trades DPAPI's machine-binding for
/// portability).
/// </summary>
public static class PassphraseKeyDerivation
{
    private const int Iterations = 200_000;
    internal const int SaltSize = 16;
    private const int VerifierSize = 16;

    /// <summary>Generates a fresh random salt for a newly-set passphrase.</summary>
    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    /// <summary>Derives the 32-byte AES-256 key from the passphrase + salt.</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(salt);

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            BackupCrypto.KeySize);
    }

    /// <summary>
    /// Computes a short verifier (a second, domain-separated PBKDF2 output) so the app can confirm
    /// a supplied passphrase matches the one set earlier, without storing the passphrase or the key.
    /// </summary>
    public static byte[] ComputeVerifier(string passphrase, byte[] salt)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(salt);

        // Domain-separate the verifier from the encryption key by prefixing the passphrase, so the
        // stored verifier can never be used as the key material.
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes("verify:" + passphrase),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            VerifierSize);
    }

    /// <summary>Fixed-time check that <paramref name="passphrase"/> matches <paramref name="storedVerifier"/>.</summary>
    public static bool VerifyPassphrase(string passphrase, byte[] salt, byte[] storedVerifier)
    {
        if (string.IsNullOrEmpty(passphrase) || salt is null || storedVerifier is null)
        {
            return false;
        }

        byte[] actual = ComputeVerifier(passphrase, salt);
        return CryptographicOperations.FixedTimeEquals(actual, storedVerifier);
    }
}
