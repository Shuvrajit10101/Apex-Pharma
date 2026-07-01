using System;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Settings;

/// <summary>
/// Settings module view-model (plan.md §6.1 Settings, §10) — the pharmacy-profile editor. Loads
/// the profile from <see cref="ISettingsService"/> on activation and saves it back on demand.
/// Owner-only: the nav item is gated on <see cref="Permission.ManageSettings"/> and the save is
/// re-checked in the service (defence in depth, plan.md §4). No settings logic lives here — the
/// view-model just binds fields and delegates persistence (plan.md §8 layering).
/// </summary>
public sealed class SettingsViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly ISettingsService _settings;
    private readonly ISessionContext _session;

    private string _pharmacyName = string.Empty;
    private string _addressLine = string.Empty;
    private string _city = string.Empty;
    private string _state = string.Empty;
    private string _gstin = string.Empty;
    private string _dlNumber = string.Empty;
    private string _phone = string.Empty;
    private string _invoiceFooter = string.Empty;
    private int _nearExpiryDays = 90;
    private TaxRoundingMode _taxRoundingMode = TaxRoundingMode.NearestRupee;

    private string? _statusMessage;
    private bool _isError;

    public SettingsViewModel(ISettingsService settings, ISessionContext session)
    {
        _settings = settings;
        _session = session;
        SaveCommand = new RelayCommand(async () => await SaveAsync());
        TaxRoundingModes = Enum.GetValues<TaxRoundingMode>();
    }

    public string PharmacyName { get => _pharmacyName; set => SetProperty(ref _pharmacyName, value); }
    public string AddressLine { get => _addressLine; set => SetProperty(ref _addressLine, value); }
    public string City { get => _city; set => SetProperty(ref _city, value); }
    public string State { get => _state; set => SetProperty(ref _state, value); }
    public string Gstin { get => _gstin; set => SetProperty(ref _gstin, value); }
    public string DlNumber { get => _dlNumber; set => SetProperty(ref _dlNumber, value); }
    public string Phone { get => _phone; set => SetProperty(ref _phone, value); }
    public string InvoiceFooter { get => _invoiceFooter; set => SetProperty(ref _invoiceFooter, value); }
    public int NearExpiryDays { get => _nearExpiryDays; set => SetProperty(ref _nearExpiryDays, value); }
    public TaxRoundingMode TaxRoundingMode { get => _taxRoundingMode; set => SetProperty(ref _taxRoundingMode, value); }

    public TaxRoundingMode[] TaxRoundingModes { get; }

    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsError { get => _isError; private set => SetProperty(ref _isError, value); }

    public ICommand SaveCommand { get; }

    /// <inheritdoc />
    public async Task ActivateAsync(UserRole role)
    {
        PharmacyProfile profile = await _settings.GetProfileAsync();
        PharmacyName = profile.PharmacyName;
        AddressLine = profile.AddressLine;
        City = profile.City;
        State = profile.State;
        Gstin = profile.Gstin;
        DlNumber = profile.DlNumber;
        Phone = profile.Phone;
        InvoiceFooter = profile.InvoiceFooter;
        NearExpiryDays = profile.NearExpiryDays;
        TaxRoundingMode = profile.TaxRoundingMode;
        SetStatus(null, isError: false);
    }

    private async Task SaveAsync()
    {
        var profile = new PharmacyProfile
        {
            PharmacyName = PharmacyName,
            AddressLine = AddressLine,
            City = City,
            State = State,
            Gstin = Gstin,
            DlNumber = DlNumber,
            Phone = Phone,
            InvoiceFooter = InvoiceFooter,
            NearExpiryDays = NearExpiryDays,
            TaxRoundingMode = TaxRoundingMode,
        };

        MasterResult result = await _settings.SaveProfileAsync(profile, _session.Role);
        if (!result.Succeeded)
        {
            SetStatus(result.Error, isError: true);
            return;
        }

        SetStatus("Pharmacy settings saved.", isError: false);
    }

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }
}
