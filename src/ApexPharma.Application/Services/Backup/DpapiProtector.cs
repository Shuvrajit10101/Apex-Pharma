using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Windows DPAPI implementation of <see cref="IDpapiProtector"/> (CurrentUser scope). An extra
/// application-specific entropy value is mixed in so the sealed blob is bound to Apex-Pharma as
/// well as the user account. This is the production key-wrapping primitive on the counter PC
/// (plan.md §14). The counter runs Windows, so DPAPI is always available there; it is isolated
/// behind <see cref="IDpapiProtector"/> so nothing else in the app takes a hard Windows dependency.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : IDpapiProtector
{
    // Application-scoped optional entropy — not a secret (it ships in the binary), but it further
    // scopes the DPAPI blob so it can't be unsealed by an unrelated app running as the same user.
    private static readonly byte[] Entropy = "ApexPharma.Backup.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedBlob)
    {
        ArgumentNullException.ThrowIfNull(protectedBlob);
        return ProtectedData.Unprotect(protectedBlob, Entropy, DataProtectionScope.CurrentUser);
    }
}
