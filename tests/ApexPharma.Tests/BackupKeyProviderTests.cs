using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Backup;
using ApexPharma.Application.Services.Settings;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// Backup key schemes (plan.md §14): the DPAPI scheme mints+wraps a key once and returns the SAME
/// key on later calls; the passphrase scheme derives deterministically, catches a wrong passphrase,
/// and never persists the passphrase; the composite dispatches on the stored scheme.
/// </summary>
public class BackupKeyProviderTests : IDisposable
{
    private readonly SqliteInMemoryContext _ctx = new();
    private readonly SettingsService _settings;

    public BackupKeyProviderTests()
    {
        var auth = new AuthService(_ctx.Context);
        _settings = new SettingsService(_ctx.Context, auth);
        _settings.SeedDefaultsAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task Dpapi_MintsOnce_ThenReturnsSameKey_AndPersistsWrappedNotPlaintext()
    {
        var provider = new DpapiBackupKeyProvider(_settings, new FakeDpapiProtector());

        byte[] first = await provider.GetKeyAsync();
        byte[] second = await provider.GetKeyAsync();

        Assert.Equal(BackupCrypto.KeySize, first.Length);
        Assert.Equal(first, second); // same key across calls

        // The stored wrapped-key setting must NOT equal the plaintext key (it's the sealed blob).
        string wrapped = await _settings.GetStringAsync(BackupKeys.WrappedKey);
        Assert.False(string.IsNullOrEmpty(wrapped));
        Assert.NotEqual(Convert.ToBase64String(first), wrapped);
        Assert.Equal("Dpapi", await _settings.GetStringAsync(BackupKeys.KeyScheme));
    }

    [Fact]
    public async Task Dpapi_ConcurrentFirstUse_AllReturnSameKey_AndPersistsExactlyOne()
    {
        // Fix 1 (data-safety): many concurrent first-ever backups must converge on ONE wrapped key.
        // Uses a thread-safe settings store + a Protect-counting protector so the assertion is about
        // the provider's single-flight minting, not EF's non-thread-safe shared context.
        var store = new ConcurrentSettingsStore();
        var protector = new CountingDpapiProtector();
        var provider = new DpapiBackupKeyProvider(store, protector);

        const int callers = 64;
        using var start = new SemaphoreSlim(0, callers);

        Task<byte[]>[] tasks = Enumerable.Range(0, callers)
            .Select(_ => Task.Run(async () =>
            {
                await start.WaitAsync();      // release all at once to maximise the race
                return await provider.GetKeyAsync();
            }))
            .ToArray();

        start.Release(callers);
        byte[][] keys = await Task.WhenAll(tasks);

        // Every caller got the SAME 32-byte key...
        byte[] expected = keys[0];
        Assert.Equal(BackupCrypto.KeySize, expected.Length);
        Assert.All(keys, k => Assert.Equal(expected, k));

        // ...and the persisted wrapped key unwraps back to that same key (no "lost" key was stored).
        string wrapped = await store.GetStringAsync(BackupKeys.WrappedKey);
        Assert.False(string.IsNullOrEmpty(wrapped));
        Assert.Equal(expected, protector.Unprotect(Convert.FromBase64String(wrapped)));

        // Exactly one key was ever minted+wrapped — no concurrent caller minted a losing key.
        Assert.Equal(1, protector.ProtectCount);
        Assert.Equal("Dpapi", await store.GetStringAsync(BackupKeys.KeyScheme));
    }

    [Fact]
    public async Task Passphrase_DerivesDeterministically_AndVerifiesWrongPassphrase()
    {
        var holder = new BackupPassphraseHolder();
        var provider = new PassphraseBackupKeyProvider(_settings, holder);

        await provider.SetPassphraseAsync("correct horse battery staple");
        byte[] key1 = await provider.GetKeyAsync();

        // Same passphrase, fresh holder -> same key (deterministic from stored salt).
        var holder2 = new BackupPassphraseHolder();
        holder2.Set("correct horse battery staple");
        var provider2 = new PassphraseBackupKeyProvider(_settings, holder2);
        byte[] key2 = await provider2.GetKeyAsync();
        Assert.Equal(key1, key2);

        // Wrong passphrase is rejected by the verifier.
        var holder3 = new BackupPassphraseHolder();
        holder3.Set("wrong passphrase");
        var provider3 = new PassphraseBackupKeyProvider(_settings, holder3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider3.GetKeyAsync());

        // The passphrase itself is never persisted — only salt + verifier are.
        Assert.False(string.IsNullOrEmpty(await _settings.GetStringAsync(BackupKeys.PassphraseSalt)));
        Assert.False(string.IsNullOrEmpty(await _settings.GetStringAsync(BackupKeys.PassphraseVerifier)));
    }

    [Fact]
    public async Task Passphrase_MissingPassphrase_Throws()
    {
        var provider = new PassphraseBackupKeyProvider(_settings, new BackupPassphraseHolder());
        await _settings.SetStringAsync(BackupKeys.PassphraseSalt, Convert.ToBase64String(PassphraseKeyDerivation.NewSalt()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetKeyAsync());
    }

    [Fact]
    public async Task Composite_DispatchesOnStoredScheme()
    {
        var dpapi = new DpapiBackupKeyProvider(_settings, new FakeDpapiProtector());
        var holder = new BackupPassphraseHolder();
        var pass = new PassphraseBackupKeyProvider(_settings, holder);
        var composite = new CompositeBackupKeyProvider(_settings, dpapi, pass);

        // Default (no scheme set) -> DPAPI path works.
        byte[] dpapiKey = await composite.GetKeyAsync();
        Assert.Equal(BackupCrypto.KeySize, dpapiKey.Length);

        // Switch to passphrase scheme.
        await pass.SetPassphraseAsync("a good passphrase");
        byte[] passKey = await composite.GetKeyAsync();
        Assert.Equal(BackupCrypto.KeySize, passKey.Length);
        Assert.NotEqual(dpapiKey, passKey);
    }
}
