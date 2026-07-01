namespace ApexPharma.Application.Services.Backup;

/// <summary>
/// Thin seam over Windows DPAPI (<c>System.Security.Cryptography.ProtectedData</c>, CurrentUser
/// scope). Protecting the backup data-key with DPAPI ties it to <b>this Windows user on this
/// machine</b>: the sealed blob can only be unsealed by the same user account, so a stolen backup
/// file plus a stolen settings row is still useless on any other machine/user (plan.md §14).
/// Behind an interface so the DPAPI dependency (Windows-only) is isolated and tests can substitute
/// a deterministic fake.
/// </summary>
public interface IDpapiProtector
{
    /// <summary>Seals <paramref name="plaintext"/> for the current user; returns the protected blob.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Unseals a blob produced by <see cref="Protect"/> on this machine/user.</summary>
    byte[] Unprotect(byte[] protectedBlob);
}
