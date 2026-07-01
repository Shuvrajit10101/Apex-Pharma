using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using ApexPharma.Application.Services;
using ApexPharma.Application.Services.Backup;
using ApexPharma.Application.Services.MasterData;
using ApexPharma.Application.Services.Settings;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Desktop.Services;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Desktop.ViewModels.Settings;

/// <summary>
/// Backup &amp; restore panel (plan.md §6.1 backup, §13, §14), embedded in the Owner-only Settings
/// module. Surfaces "Backup now", "Restore from file…", the recent-backups list, last-backup
/// status, and the backup configuration (local/cloud folders, retention, auto-daily, and an
/// optional write-only passphrase). Gated on <see cref="Permission.Backup"/> (Owner). All backup
/// logic lives in <see cref="IBackupService"/>; this view-model only binds and delegates
/// (plan.md §8 layering). A failed backup/restore surfaces a clear message and never touches live
/// data (the service restores via decrypt→validate→atomic-swap).
/// </summary>
public sealed class BackupViewModel : ViewModelBase
{
    private readonly IBackupService _backup;
    private readonly ISettingsService _settings;
    private readonly IBackupDialogService _dialogs;
    private readonly IBackupPassphraseHolder _passphraseHolder;
    private readonly PassphraseBackupKeyProvider _passphraseProvider;
    private readonly ISessionContext _session;

    private string _localFolder = string.Empty;
    private string _cloudFolder = string.Empty;
    private int _retentionCount = 30;
    private bool _autoBackupEnabled = true;
    private string _lastBackupStatus = "No backup yet.";
    private string? _statusMessage;
    private bool _isError;
    private bool _isBusy;

    public BackupViewModel(
        IBackupService backup,
        ISettingsService settings,
        IBackupDialogService dialogs,
        IBackupPassphraseHolder passphraseHolder,
        PassphraseBackupKeyProvider passphraseProvider,
        ISessionContext session)
    {
        _backup = backup;
        _settings = settings;
        _dialogs = dialogs;
        _passphraseHolder = passphraseHolder;
        _passphraseProvider = passphraseProvider;
        _session = session;

        RecentBackups = new ObservableCollection<BackupInfo>();

        BackupNowCommand = new RelayCommand(() => _ = BackupNowAsync(), () => !IsBusy);
        RestoreCommand = new RelayCommand(() => _ = RestoreAsync(), () => !IsBusy);
        BrowseLocalCommand = new RelayCommand(BrowseLocal, () => !IsBusy);
        BrowseCloudCommand = new RelayCommand(BrowseCloud, () => !IsBusy);
        SaveBackupSettingsCommand = new RelayCommand(() => _ = SaveSettingsAsync(), () => !IsBusy);
    }

    public ObservableCollection<BackupInfo> RecentBackups { get; }

    public string LocalFolder { get => _localFolder; set => SetProperty(ref _localFolder, value); }
    public string CloudFolder { get => _cloudFolder; set => SetProperty(ref _cloudFolder, value); }
    public int RetentionCount { get => _retentionCount; set => SetProperty(ref _retentionCount, value); }
    public bool AutoBackupEnabled { get => _autoBackupEnabled; set => SetProperty(ref _autoBackupEnabled, value); }

    public string LastBackupStatus { get => _lastBackupStatus; private set => SetProperty(ref _lastBackupStatus, value); }
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsError { get => _isError; private set => SetProperty(ref _isError, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (BackupNowCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RestoreCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (BrowseLocalCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (BrowseCloudCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SaveBackupSettingsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand BackupNowCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand BrowseLocalCommand { get; }
    public ICommand BrowseCloudCommand { get; }
    public ICommand SaveBackupSettingsCommand { get; }

    /// <summary>Loads the current backup configuration + recent-backups list (called on activation).</summary>
    public async Task LoadAsync()
    {
        LocalFolder = await _settings.GetStringAsync(BackupKeys.LocalFolder, string.Empty);
        CloudFolder = await _settings.GetStringAsync(BackupKeys.CloudFolder, string.Empty);
        RetentionCount = await _settings.GetIntAsync(BackupKeys.RetentionCount, 30);
        string auto = await _settings.GetStringAsync(BackupKeys.AutoBackupEnabled, "true");
        AutoBackupEnabled = !bool.TryParse(auto, out bool a) || a;

        await RefreshLastBackupAsync();
        await RefreshRecentAsync();
        SetStatus(null, isError: false);
    }

    private async Task RefreshLastBackupAsync()
    {
        string raw = await _settings.GetStringAsync(BackupKeys.LastBackupUtc, string.Empty);
        LastBackupStatus = DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime utc)
            ? $"Last backup: {utc.ToLocalTime():dd MMM yyyy, HH:mm}"
            : "No backup yet.";
    }

    private async Task RefreshRecentAsync()
    {
        RecentBackups.Clear();
        foreach (BackupInfo info in await _backup.ListBackupsAsync())
        {
            RecentBackups.Add(info);
        }
    }

    private async Task BackupNowAsync()
    {
        IsBusy = true;
        try
        {
            string path = await _backup.CreateBackupAsync(_session.Role);
            await RefreshLastBackupAsync();
            await RefreshRecentAsync();
            SetStatus($"Backup created: {path}", isError: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Backup failed: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAsync()
    {
        string? file = _dialogs.PickBackupFile("Restore from backup", LocalFolder);
        if (file is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Restoring will replace ALL current data with the selected backup after a restart. " +
                "This cannot be undone. Continue?",
                "Confirm restore"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            MasterResult result = await _backup.RestoreFromBackupAsync(file, _session.Role);
            if (!result.Succeeded)
            {
                SetStatus(result.Error, isError: true);
                return;
            }

            SetStatus("Backup validated. Please restart Apex-Pharma to complete the restore.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Restore failed: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BrowseLocal()
    {
        string? picked = _dialogs.PickFolder("Choose local backup folder", LocalFolder);
        if (picked is not null)
        {
            LocalFolder = picked;
        }
    }

    private void BrowseCloud()
    {
        string? picked = _dialogs.PickFolder("Choose cloud-synced backup folder", CloudFolder);
        if (picked is not null)
        {
            CloudFolder = picked;
        }
    }

    private async Task SaveSettingsAsync()
    {
        IsBusy = true;
        try
        {
            int retention = RetentionCount > 0 ? RetentionCount : 30;
            await _settings.SetStringAsync(BackupKeys.LocalFolder, (LocalFolder ?? string.Empty).Trim());
            await _settings.SetStringAsync(BackupKeys.CloudFolder, (CloudFolder ?? string.Empty).Trim());
            await _settings.SetStringAsync(BackupKeys.RetentionCount, retention.ToString(CultureInfo.InvariantCulture));
            await _settings.SetStringAsync(BackupKeys.AutoBackupEnabled, AutoBackupEnabled ? "true" : "false");
            RetentionCount = retention;
            await RefreshRecentAsync();
            SetStatus("Backup settings saved.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save backup settings: {ex.Message}", isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Switches to the passphrase key scheme (write-only entry from the view). The passphrase is
    /// held in memory for the session and never persisted — only a salt + verifier are stored.
    /// </summary>
    public async Task SetPassphraseAsync(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            SetStatus("Enter a passphrase first.", isError: true);
            return;
        }

        try
        {
            await _passphraseProvider.SetPassphraseAsync(passphrase);
            SetStatus("Backup passphrase set. New backups will use it.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not set passphrase: {ex.Message}", isError: true);
        }
    }

    private void SetStatus(string? message, bool isError)
    {
        IsError = isError;
        StatusMessage = message;
    }
}
