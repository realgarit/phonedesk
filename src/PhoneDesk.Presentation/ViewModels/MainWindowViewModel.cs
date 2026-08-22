using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using System.Collections.ObjectModel;
using System.Collections;
using System.Text;
using System.Linq;
using System;
using PhoneDesk.Models;
using FluentAvalonia.Styling;
using System.Collections.Specialized;
using System.ComponentModel;
using PhoneDesk.Localization;

namespace PhoneDesk.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private const int MaxLogEntries = 1000;
        private bool _disposed = false;
        private string? _allLogEntriesTextCache;
        private bool _logCacheDirty = true;

        private PropertyChangedEventHandler? _loggingPropertyHandler;
        private NotifyCollectionChangedEventHandler? _logEntriesHandler;
        private PropertyChangedEventHandler? _navigationPropertyHandler;
        private PropertyChangedEventHandler? _translationPropertyHandler;

        private readonly IPageViewModelFactory _pageViewModelFactory;
        private readonly IUpdateCheckService _updateCheckService;
        private readonly IUpdateInstallerService _updateInstallerService;
        private readonly IDialogService _updateDialogService;
        private readonly IBundledModuleVersionService _bundledModuleVersionService;
        private UpdateInfo? _availableUpdate;
        private CancellationTokenSource? _updateCancellation;
        private UpdateBannerState _updateBannerState;
        private string? _updateBannerVersion;
        private double? _updateBannerProgress;

        [ObservableProperty]
        private bool _isUpdateBannerVisible;

        [ObservableProperty]
        private bool _isUpdateAvailable;

        [ObservableProperty]
        private string _updateBannerMessage = string.Empty;

        [ObservableProperty]
        private bool _canInstallUpdate;

        [ObservableProperty]
        private bool _isUpdateInProgress;

        [ObservableProperty]
        private bool _isUpdateProgressIndeterminate;

        [ObservableProperty]
        private double _updateDownloadProgress;

        private string? _updateReleaseUrl;

        [ObservableProperty]
        private string _bundledTeamsModuleVersion = string.Empty;

        [ObservableProperty]
        private string _bundledGraphModuleVersion = string.Empty;

        [ObservableProperty]
        private string _bundledPowerShellSdkVersion = string.Empty;

        private enum UpdateBannerState
        {
            None,
            Available,
            Downloading,
            DownloadingProgress,
            StartingInstaller
        }

        private void SetUpdateBannerState(UpdateBannerState state, string? version = null, double? progress = null)
        {
            _updateBannerState = state;
            _updateBannerVersion = version ?? _updateBannerVersion;
            _updateBannerProgress = progress;
            RefreshUpdateBannerMessage();
        }

        private void RefreshUpdateBannerMessage()
        {
            UpdateBannerMessage = _updateBannerState switch
            {
                UpdateBannerState.Available when !string.IsNullOrEmpty(_updateBannerVersion)
                    => GetText(
                        UiTextKey.UpdateAvailable,
                        "Version {version} is available.",
                        new Dictionary<string, object?> { ["version"] = _updateBannerVersion }),
                UpdateBannerState.Downloading when !string.IsNullOrEmpty(_updateBannerVersion)
                    => GetText(
                        UiTextKey.UpdateDownloading,
                        "Downloading version {version}...",
                        new Dictionary<string, object?> { ["version"] = _updateBannerVersion }),
                UpdateBannerState.DownloadingProgress when !string.IsNullOrEmpty(_updateBannerVersion) && _updateBannerProgress is { } progress
                    => GetText(
                        UiTextKey.UpdateDownloadingProgress,
                        "Downloading version {version}... {progress}%",
                        new Dictionary<string, object?> { ["version"] = _updateBannerVersion, ["progress"] = progress }),
                UpdateBannerState.StartingInstaller
                    => GetText(
                        UiTextKey.UpdateStartingInstaller,
                        "Starting the verified installer...",
                        parameters: null),
                _ => string.Empty
            };
        }

        private string GetStateLabel(bool value)
            => GetText(value ? UiTextKey.CommonEnabled : UiTextKey.CommonDisabled, value ? "enabled" : "disabled");

        private string GetOpenClosedLabel(bool value)
            => GetText(value ? UiTextKey.CommonOpened : UiTextKey.CommonClosed, value ? "opened" : "closed");

        private string GetThemeLabel(bool value)
            => GetText(value ? UiTextKey.MainThemeDark : UiTextKey.MainThemeLight, value ? "Dark" : "Light");

        private string GetLogLevelLabel(LogLevel level) => level switch
        {
            LogLevel.Success => GetText(UiTextKey.SettingsLogLevelSuccess, "Success"),
            LogLevel.Warning => GetText(UiTextKey.SettingsLogLevelWarning, "Warning"),
            LogLevel.Error => GetText(UiTextKey.SettingsLogLevelError, "Error"),
            _ => GetText(UiTextKey.SettingsLogLevelInfo, "Info")
        };

        private async Task CheckForUpdateAsync()
        {
            var update = await _updateCheckService.CheckForUpdateAsync();
            if (update is null)
            {
                return;
            }

            _availableUpdate = update;
            _updateReleaseUrl = update.ReleaseUrl;
            SetUpdateBannerState(UpdateBannerState.Available, update.LatestVersion);
            CanInstallUpdate = _updateInstallerService.IsSupported && update.WindowsInstaller is not null;
            IsUpdateAvailable = true;
            IsUpdateBannerVisible = true;
            LogLocalized(
                UiTextKey.MainUpdateAvailableLog,
                "Update available: {version}",
                LogLevel.Info,
                new Dictionary<string, object?> { ["version"] = update.LatestVersion });
        }

        partial void OnCanInstallUpdateChanged(bool value)
        {
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }

        partial void OnIsUpdateInProgressChanged(bool value)
        {
            InstallUpdateCommand.NotifyCanExecuteChanged();
            CancelUpdateCommand.NotifyCanExecuteChanged();
        }

        private bool CanInstallUpdateNow() => CanInstallUpdate && !IsUpdateInProgress;

        [RelayCommand(CanExecute = nameof(CanInstallUpdateNow))]
        private async Task InstallUpdateAsync()
        {
            var update = _availableUpdate;
            if (update?.WindowsInstaller is not { } installer)
            {
                LogLocalized(UiTextKey.MainNoVerifiedInstallerLog, "No verified Windows update installer is available.", LogLevel.Warning);
                return;
            }

            var confirmed = await _updateDialogService.ShowConfirmationAsync(
                GetText(UiTextKey.MainInstallUpdateDialogTitle, "Install update"),
                GetText(
                    UiTextKey.MainInstallUpdateDialogMessage,
                    "Download and install version {version}? PhoneDesk will close and restart automatically.",
                    new Dictionary<string, object?> { ["version"] = update.LatestVersion }));
            if (!confirmed)
            {
                return;
            }

            _updateCancellation?.Dispose();
            _updateCancellation = new CancellationTokenSource();
            IsUpdateInProgress = true;
            IsUpdateProgressIndeterminate = true;
            UpdateDownloadProgress = 0;
            SetUpdateBannerState(UpdateBannerState.Downloading, update.LatestVersion);

            var progress = new Progress<UpdateDownloadProgress>(download =>
            {
                IsUpdateProgressIndeterminate = download.TotalBytes is not > 0;
                UpdateDownloadProgress = download.Percentage;
                SetUpdateBannerState(
                    download.TotalBytes is > 0 ? UpdateBannerState.DownloadingProgress : UpdateBannerState.Downloading,
                    update.LatestVersion,
                    download.TotalBytes is > 0 ? download.Percentage : null);
            });

            try
            {
                var installerPath = await _updateInstallerService.DownloadInstallerAsync(
                    installer,
                    progress,
                    _updateCancellation.Token);

                SetUpdateBannerState(UpdateBannerState.StartingInstaller, update.LatestVersion);
                _updateInstallerService.LaunchInstaller(installerPath);
                LogLocalized(
                    UiTextKey.MainStartingInstallerLog,
                    "Starting installer for version {version}",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["version"] = update.LatestVersion });

                if (Avalonia.Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            }
            catch (OperationCanceledException)
            {
                SetUpdateBannerState(UpdateBannerState.Available, update.LatestVersion);
                LogLocalized(UiTextKey.MainUpdateDownloadCancelledLog, "Update download cancelled.", LogLevel.Info);
            }
            catch (UpdateInstallationException ex)
            {
                SetUpdateBannerState(UpdateBannerState.Available, update.LatestVersion);
                LogLocalized(
                    UiTextKey.MainUpdateInstallationFailedLog,
                    "Update installation failed: {error}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
                await _updateDialogService.ShowMessageAsync(
                    GetText(UiTextKey.MainUpdateFailedTitle, "Update failed"),
                    ex.Message);
            }
            finally
            {
                _updateCancellation.Dispose();
                _updateCancellation = null;
                IsUpdateInProgress = false;
                IsUpdateProgressIndeterminate = false;
            }
        }

        private bool CanCancelUpdate() => IsUpdateInProgress;

        [RelayCommand(CanExecute = nameof(CanCancelUpdate))]
        private void CancelUpdate()
        {
            _updateCancellation?.Cancel();
        }

        [RelayCommand]
        private void OpenUpdatePage()
        {
            if (_updateReleaseUrl is null)
            {
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _updateReleaseUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.MainOpenReleasePageFailedLog,
                    "Could not open release page: {error}",
                    LogLevel.Warning,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        [RelayCommand]
        private void DismissUpdateBanner()
        {
            IsUpdateBannerVisible = false;
        }

        [RelayCommand]
        private void ShowUpdateBanner()
        {
            IsUpdateBannerVisible = true;
        }

        public ObservableCollection<string> LogEntries => _loggingService.LogEntries;
        public string LatestLogEntry => _loggingService.LatestLogEntry;

        public string AllLogEntriesText
        {
            get
            {
                if (_logCacheDirty || _allLogEntriesTextCache == null)
                {
                    var sb = new StringBuilder();
                    var entries = _loggingService.GetFilteredEntries();
                    foreach (var entry in entries)
                    {
                        sb.AppendLine(entry);
                    }
                    _allLogEntriesTextCache = sb.ToString();
                    _logCacheDirty = false;
                }
                return _allLogEntriesTextCache;
            }
        }

        [ObservableProperty]
        private bool _isDarkTheme = true;

        // Settings properties — these proxy through to ISharedStateService
        public bool SkipScriptPreview
        {
            get => _sharedStateService?.SkipScriptPreview ?? false;
            set
            {
                if (_sharedStateService != null)
                {
                    _sharedStateService.SkipScriptPreview = value;
                    OnPropertyChanged();
                    LogLocalized(
                        UiTextKey.MainSkipScriptPreviewLog,
                        "Skip script preview: {state}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["state"] = GetStateLabel(value) });
                }
            }
        }

        public bool SkipDeleteConfirmation
        {
            get => _sharedStateService?.SkipDeleteConfirmation ?? false;
            set
            {
                if (_sharedStateService != null)
                {
                    _sharedStateService.SkipDeleteConfirmation = value;
                    OnPropertyChanged();
                    LogLocalized(
                        UiTextKey.MainSkipDeleteConfirmationLog,
                        "Skip delete confirmation: {state}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["state"] = GetStateLabel(value) });
                }
            }
        }

        public bool AutoRefreshAfterOperations
        {
            get => _sharedStateService?.AutoRefreshAfterOperations ?? true;
            set
            {
                if (_sharedStateService != null)
                {
                    _sharedStateService.AutoRefreshAfterOperations = value;
                    OnPropertyChanged();
                    LogLocalized(
                        UiTextKey.MainAutoRefreshAfterOperationsLog,
                        "Auto-refresh after operations: {state}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["state"] = GetStateLabel(value) });
                }
            }
        }

        public int SelectedLogLevelIndex
        {
            get => (int)(_sharedStateService?.MinimumLogLevel ?? LogLevel.Info);
            set
            {
                var level = (LogLevel)value;
                if (_sharedStateService != null)
                {
                    _sharedStateService.MinimumLogLevel = level;
                    _loggingService.MinimumLogLevel = level;
                    OnPropertyChanged();
                    // Invalidate log cache so filtered view updates
                    _logCacheDirty = true;
                    OnPropertyChanged(nameof(AllLogEntriesText));
                    LogLocalized(
                        UiTextKey.MainMinimumLogLevelLog,
                        "Minimum log level: {level}",
                        LogLevel.Info,
                        new Dictionary<string, object?> { ["level"] = GetLogLevelLabel(level) });
                }
            }
        }

        public AppLanguage CurrentLanguage
        {
            get => _translationService?.CurrentLanguage ?? AppLanguage.English;
            set
            {
                if (_translationService is null || _translationService.CurrentLanguage == value)
                {
                    return;
                }

                _translationService.CurrentLanguage = value;
            }
        }

        public IReadOnlyList<LanguageOption> LanguageOptions =>
        [
            new(AppLanguage.English, GetText(UiTextKey.SettingsLanguageEnglish, "English")),
            new(AppLanguage.German, GetText(UiTextKey.SettingsLanguageGerman, "Deutsch"))
        ];

        /// <summary>The directory where per-tenant audit log files are stored (shown in Settings).</summary>
        public string AuditLogDirectory => _auditLog?.LogDirectoryPath
            ?? GetText(UiTextKey.SettingsAuditLogUnavailable, "Audit log not available");

        [RelayCommand]
        private void OpenAuditFolder()
        {
            var path = _auditLog?.LogDirectoryPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogLocalized(
                    UiTextKey.MainOpenAuditLogFolderFailedLog,
                    "Could not open audit log folder: {error}",
                    LogLevel.Warning,
                    new Dictionary<string, object?> { ["error"] = ex.Message });
            }
        }

        [ObservableProperty]
        private string _currentPage = Services.ConstantsService.Pages.Welcome;

        /// <summary>
        /// The ViewModel for the current page. Bound by the content host; the ViewLocator renders
        /// the matching View with this as its DataContext (VM-first; no service locator).
        /// </summary>
        [ObservableProperty]
        private object? _currentViewModel;

        partial void OnCurrentPageChanged(string value)
        {
            var previousViewModel = CurrentViewModel;
            var nextViewModel = _pageViewModelFactory.Create(value);
            CurrentViewModel = nextViewModel;

            if (!ReferenceEquals(previousViewModel, nextViewModel)
                && previousViewModel is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        [ObservableProperty]
        private bool _isSettingsOpen;

        [ObservableProperty]
        private bool _isLogDialogOpen;

        [ObservableProperty]
        private string _version = Services.ConstantsService.Application.Version;

        public string Copyright => Services.ConstantsService.Application.Copyright;

        public bool IsTeamsConnected => _sessionManager.TeamsConnected;
        public bool IsGraphConnected => _sessionManager.GraphConnected;
        public string ConnectionStatusText
        {
            get
            {
                var teams = GetText(
                    _sessionManager.TeamsConnected
                        ? UiTextKey.MainConnectionStatusConnected
                        : UiTextKey.MainConnectionStatusDisconnected,
                    _sessionManager.TeamsConnected ? "Connected" : "Disconnected");
                var graph = GetText(
                    _sessionManager.GraphConnected
                        ? UiTextKey.MainConnectionStatusConnected
                        : UiTextKey.MainConnectionStatusDisconnected,
                    _sessionManager.GraphConnected ? "Connected" : "Disconnected");
                return GetText(
                    UiTextKey.MainConnectionStatusSummary,
                    "Teams: {teamsStatus} | Graph: {graphStatus}",
                    new Dictionary<string, object?>
                    {
                        ["teamsStatus"] = teams,
                        ["graphStatus"] = graph
                    });
            }
        }

        public PhoneManagerVariables Variables
        {
            get => _sharedStateService?.Variables ?? new PhoneManagerVariables();
            set
            {
                if (_sharedStateService != null)
                {
                    _sharedStateService.Variables = value;
                    OnPropertyChanged();
                }
            }
        }

        public MainWindowViewModel(
            IPowerShellContextService powerShellContextService,
            IPowerShellCommandService powerShellCommandService,
            ILoggingService loggingService,
            ISessionManager sessionManager,
            INavigationService navigationService,
            IErrorHandlingService errorHandlingService,
            IValidationService validationService,
            ISharedStateService sharedStateService,
            IDialogService dialogService,
            IPageViewModelFactory pageViewModelFactory,
            IUpdateCheckService updateCheckService,
            IUpdateInstallerService updateInstallerService,
            IBundledModuleVersionService bundledModuleVersionService,
            IAuditLog? auditLog = null,
            ITranslationService? translationService = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, sharedStateService, dialogService, auditLog, translationService)
        {
            _pageViewModelFactory = pageViewModelFactory;
            _updateCheckService = updateCheckService;
            _updateInstallerService = updateInstallerService;
            _updateDialogService = dialogService;
            _bundledModuleVersionService = bundledModuleVersionService;
            CurrentViewModel = _pageViewModelFactory.Create(CurrentPage);

            BundledTeamsModuleVersion = _bundledModuleVersionService.TeamsModuleVersion;
            BundledGraphModuleVersion = _bundledModuleVersionService.GraphModuleVersion;
            BundledPowerShellSdkVersion = _bundledModuleVersionService.PowerShellSdkVersion;

            LogLocalized(UiTextKey.MainApplicationStartedLog, "Application started", LogLevel.Info);

            _ = CheckForUpdateAsync();

            _loggingPropertyHandler = (s, e) =>
            {
                if (e.PropertyName == nameof(ILoggingService.LatestLogEntry))
                {
                    OnPropertyChanged(nameof(LatestLogEntry));
                }
            };
            _loggingService.PropertyChanged += _loggingPropertyHandler;

            _logEntriesHandler = (s, e) =>
            {
                _logCacheDirty = true;

                // Limit log size
                while (LogEntries.Count > MaxLogEntries)
                {
                    LogEntries.RemoveAt(0);
                }

                OnPropertyChanged(nameof(AllLogEntriesText));
            };
            LogEntries.CollectionChanged += _logEntriesHandler;

            _translationPropertyHandler = (s, e) =>
            {
                if (e.PropertyName == nameof(ITranslationService.CurrentLanguage))
                {
                    if (IsUpdateBannerVisible)
                    {
                        RefreshUpdateBannerMessage();
                    }

                    OnPropertyChanged(nameof(CurrentLanguage));
                    OnPropertyChanged(nameof(LanguageOptions));
                    OnPropertyChanged(nameof(ConnectionStatusText));
                    OnPropertyChanged(nameof(AuditLogDirectory));
                }
            };
            _translationService?.PropertyChanged += _translationPropertyHandler;

            _navigationPropertyHandler = (s, e) =>
            {
                if (e.PropertyName == nameof(INavigationService.CurrentPage))
                {
                    CurrentPage = _navigationService.CurrentPage;
                    RefreshConnectionStatus();
                }
            };
            _navigationService.PropertyChanged += _navigationPropertyHandler;
        }

        partial void OnIsDarkThemeChanged(bool value)
        {
            // Update FluentAvalonia theme
            var app = Avalonia.Application.Current;
            if (app != null)
            {
                var faTheme = app.RequestedThemeVariant;
                app.RequestedThemeVariant = value ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            }
            LogLocalized(
                UiTextKey.MainThemeChangedLog,
                "Theme changed to {theme}",
                LogLevel.Info,
                new Dictionary<string, object?> { ["theme"] = GetThemeLabel(value) });
        }

        [RelayCommand]
        public new void NavigateTo(string page)
        {
            _navigationService.NavigateTo(page);
        }

        public void RefreshConnectionStatus()
        {
            OnPropertyChanged(nameof(IsTeamsConnected));
            OnPropertyChanged(nameof(IsGraphConnected));
            OnPropertyChanged(nameof(ConnectionStatusText));
        }

        [RelayCommand]
        private void ToggleSettings()
        {
            IsSettingsOpen = !IsSettingsOpen;
            LogLocalized(
                UiTextKey.MainSettingsPanelStateLog,
                "Settings panel {state}",
                LogLevel.Info,
                new Dictionary<string, object?> { ["state"] = GetOpenClosedLabel(IsSettingsOpen) });
        }

        [RelayCommand]
        private void CloseSettings()
        {
            IsSettingsOpen = false;
            LogLocalized(
                UiTextKey.MainSettingsPanelStateLog,
                "Settings panel {state}",
                LogLevel.Info,
                new Dictionary<string, object?> { ["state"] = GetOpenClosedLabel(false) });
        }

        [RelayCommand]
        private void ClearLog()
        {
            _loggingService.Clear();
            LogLocalized(UiTextKey.MainLogClearedLog, "Log cleared", LogLevel.Info);
        }

        [RelayCommand]
        private void ToggleLogDialog()
        {
            IsLogDialogOpen = !IsLogDialogOpen;
            LogLocalized(
                UiTextKey.MainLogViewerStateLog,
                "Log viewer {state}",
                LogLevel.Info,
                new Dictionary<string, object?> { ["state"] = GetOpenClosedLabel(IsLogDialogOpen) });
        }

        [RelayCommand]
        private void CloseLogDialog()
        {
            IsLogDialogOpen = false;
            LogLocalized(
                UiTextKey.MainLogViewerStateLog,
                "Log viewer {state}",
                LogLevel.Info,
                new Dictionary<string, object?> { ["state"] = GetOpenClosedLabel(false) });
        }

        public sealed record LanguageOption(AppLanguage Language, string Label);

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_loggingPropertyHandler != null)
                    _loggingService.PropertyChanged -= _loggingPropertyHandler;

                if (_logEntriesHandler != null)
                    LogEntries.CollectionChanged -= _logEntriesHandler;

                if (_navigationPropertyHandler != null)
                    _navigationService.PropertyChanged -= _navigationPropertyHandler;

                if (_translationPropertyHandler != null && _translationService != null)
                    _translationService.PropertyChanged -= _translationPropertyHandler;

                if (CurrentViewModel is IDisposable disposable)
                    disposable.Dispose();

                _updateCancellation?.Dispose();

                _disposed = true;
            }
        }
    }
} 
