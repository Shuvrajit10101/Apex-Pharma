using System.Threading.Tasks;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Domain.Entities;
using ApexPharma.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ApexPharma.Tests;

/// <summary>
/// SettingsService tests (plan.md §6.1 Settings, §14 compliance): default seeding is idempotent,
/// raw get/set round-trips, the typed <see cref="PharmacyProfile"/> reads back what was written,
/// saving is Owner-only (RBAC), and validation rejects bad input.
/// </summary>
public class SettingsServiceTests : IDisposable
{
    private readonly SqliteInMemoryContext _fixture = new();
    private readonly SettingsService _sut;

    public SettingsServiceTests()
    {
        var auth = new AuthService(_fixture.Context);
        _sut = new SettingsService(_fixture.Context, auth);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task SeedDefaults_PopulatesMissingKeys()
    {
        await _sut.SeedDefaultsAsync();

        PharmacyProfile profile = await _sut.GetProfileAsync();
        Assert.Equal("Apex Pharmacy", profile.PharmacyName);
        Assert.Equal(90, profile.NearExpiryDays);
        Assert.Equal(TaxRoundingMode.NearestRupee, profile.TaxRoundingMode);
    }

    [Fact]
    public async Task SeedDefaults_IsIdempotent_AndDoesNotOverwrite()
    {
        await _sut.SeedDefaultsAsync();
        await _sut.SetStringAsync(SettingsService.KeyPharmacyName, "My Custom Pharmacy");

        // Second seed must NOT clobber the customised value.
        await _sut.SeedDefaultsAsync();

        Assert.Equal("My Custom Pharmacy", await _sut.GetStringAsync(SettingsService.KeyPharmacyName));
        // And it did not duplicate rows.
        int count = await _fixture.NewContext().Settings.CountAsync(s => s.Key == SettingsService.KeyPharmacyName);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SetString_And_GetString_RoundTrip()
    {
        await _sut.SetStringAsync("Test.Key", "hello");
        Assert.Equal("hello", await _sut.GetStringAsync("Test.Key"));

        // Update the same key (upsert path).
        await _sut.SetStringAsync("Test.Key", "world");
        Assert.Equal("world", await _sut.GetStringAsync("Test.Key"));
    }

    [Fact]
    public async Task GetString_MissingKey_ReturnsFallback()
    {
        Assert.Equal("fallback", await _sut.GetStringAsync("No.Such.Key", "fallback"));
    }

    [Fact]
    public async Task GetInt_ParsesOrFallsBack()
    {
        await _sut.SetStringAsync("Num.Key", "42");
        Assert.Equal(42, await _sut.GetIntAsync("Num.Key", 0));

        await _sut.SetStringAsync("Bad.Num", "not-a-number");
        Assert.Equal(7, await _sut.GetIntAsync("Bad.Num", 7));
    }

    [Fact]
    public async Task SaveProfile_AsOwner_PersistsAndReadsBack()
    {
        var profile = new PharmacyProfile
        {
            PharmacyName = "Apex Retail Chemists",
            AddressLine = "12 MG Road",
            City = "Kolkata",
            State = "West Bengal",
            Gstin = "19AABCU9603R1ZM",
            DlNumber = "WB-20B-1234 / WB-21B-1234",
            Phone = "9800000000",
            InvoiceFooter = "No returns on medicines.",
            NearExpiryDays = 60,
            TaxRoundingMode = TaxRoundingMode.None,
        };

        MasterResult result = await _sut.SaveProfileAsync(profile, UserRole.Owner);
        Assert.True(result.Succeeded, result.Error);

        PharmacyProfile read = await _sut.GetProfileAsync();
        Assert.Equal("Apex Retail Chemists", read.PharmacyName);
        Assert.Equal("12 MG Road", read.AddressLine);
        Assert.Equal("Kolkata", read.City);
        Assert.Equal("West Bengal", read.State);
        Assert.Equal("19AABCU9603R1ZM", read.Gstin);
        Assert.Equal("WB-20B-1234 / WB-21B-1234", read.DlNumber);
        Assert.Equal("9800000000", read.Phone);
        Assert.Equal("No returns on medicines.", read.InvoiceFooter);
        Assert.Equal(60, read.NearExpiryDays);
        Assert.Equal(TaxRoundingMode.None, read.TaxRoundingMode);
    }

    [Fact]
    public async Task SaveProfile_AsCashier_IsRefused()
    {
        var profile = new PharmacyProfile { PharmacyName = "X" };
        MasterResult result = await _sut.SaveProfileAsync(profile, UserRole.Cashier);

        Assert.False(result.Succeeded);
        Assert.Contains("permission", result.Error!);
        // Nothing persisted.
        Assert.False(await _fixture.NewContext().Settings.AnyAsync(s => s.Key == SettingsService.KeyPharmacyName));
    }

    [Fact]
    public async Task SaveProfile_AsPharmacist_IsRefused()
    {
        var profile = new PharmacyProfile { PharmacyName = "X" };
        MasterResult result = await _sut.SaveProfileAsync(profile, UserRole.Pharmacist);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SaveProfile_BlankName_IsRejected()
    {
        var profile = new PharmacyProfile { PharmacyName = "  " };
        MasterResult result = await _sut.SaveProfileAsync(profile, UserRole.Owner);
        Assert.False(result.Succeeded);
        Assert.Contains("name", result.Error!);
    }

    [Fact]
    public async Task SaveProfile_InvalidGstin_IsRejected()
    {
        var profile = new PharmacyProfile { PharmacyName = "Apex", Gstin = "NOT-A-GSTIN" };
        MasterResult result = await _sut.SaveProfileAsync(profile, UserRole.Owner);
        Assert.False(result.Succeeded);
        Assert.Contains("GSTIN", result.Error!);
    }

    [Fact]
    public async Task SaveProfile_BlankGstin_IsAllowed()
    {
        // Pre-configuration: the Owner may leave GSTIN blank and fill it later.
        var profile = new PharmacyProfile { PharmacyName = "Apex", Gstin = "" };
        MasterResult result = await _sut.SaveProfileAsync(profile, UserRole.Owner);
        Assert.True(result.Succeeded, result.Error);
    }
}
