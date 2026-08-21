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

        private readonly IPageViewModelFactory _pageViewModelFactory;
        private readonly IUpdateCheckService _updateCheckService;
        private readonly IUpdateInstallerService _updateInstallerService;
        private readonly IDialogService _updateDialogService;
        private readonly IBundledModuleVersionService _bundledModuleVersionService;
        private UpdateInfo? _availableUpdate;
        private CancellationTokenSource? _updateCancellation;

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

        private async Task CheckForUpdateAsync()
        {
            var update = await _updateCheckService.CheckForUpdateAsync();
            if (update is null)
            {
                return;
            }

            _availableUpdate = update;
            _updateReleaseUrl = update.ReleaseUrl;
            UpdateBannerMessage = (_translationService?.Get(
                UiTextKey.UpdateAvailable,
                new Dictionary<string, object?> { ["version"] = update.LatestVersion }))
                ?? $"Version {update.LatestVersion} is available.";
            CanInstallUpdate = _updateInstallerService.IsSupported && update.WindowsInstaller is not null;
            IsUpdateAvailable = true;
            IsUpdateBannerVisible = true;
            _loggingService.Log($"Update available: {update.LatestVersion}", LogLevel.Info);
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
                _loggingService.Log("No verified Windows update installer is available.", LogLevel.Warning);
                return;
            }

            var confirmed = await _updateDialogService.ShowConfirmationAsync(
                "Install update",
                $"Download and install version {update.LatestVersion}? " +
                "PhoneDesk will close and restart automatically.");
            if (!confirmed)
            {
                return;
            }

            _updateCancellation?.Dispose();
            _updateCancellation = new CancellationTokenSource();
            IsUpdateInProgress = true;
            IsUpdateProgressIndeterminate = true;
            UpdateDownloadProgress = 0;
            UpdateBannerMessage = $"Downloading version {update.LatestVersion}...";

            var progress = new Progress<UpdateDownloadProgress>(download =>
            {
                IsUpdateProgressIndeterminate = download.TotalBytes is not > 0;
                UpdateDownloadProgress = download.Percentage;
                UpdateBannerMessage = download.TotalBytes is > 0
                    ? $"Downloading version {update.LatestVersion}... {download.Percentage}%"
                    : $"Downloading version {update.LatestVersion}...";
            });

            try
            {
                var installerPath = await _updateInstallerService.DownloadInstallerAsync(
                    installer,
                    progress,
                    _updateCancellation.Token);

                UpdateBannerMessage = "Starting the verified installer...";
                _updateInstallerService.LaunchInstaller(installerPath);
                _loggingService.Log(
                    $"Starting installer for version {update.LatestVersion}",
                    LogLevel.Info);

                if (Avalonia.Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            }
            catch (OperationCanceledException)
            {
                UpdateBannerMessage = $"Version {update.LatestVersion} is available.";
                _loggingService.Log("Update download cancelled.", LogLevel.Info);
            }
            catch (UpdateInstallationException ex)
            {
                UpdateBannerMessage = $"Version {update.LatestVersion} is available.";
                _loggingService.Log($"Update installation failed: {ex.Message}", LogLevel.Error);
                await _updateDialogService.ShowMessageAsync("Update failed", ex.Message);
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
                _loggingService.Log($"Could not open release page: {ex.Message}", LogLevel.Warning);
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
                    _loggingService.Log($"Skip script preview: {value}", LogLevel.Info);
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
                    _loggingService.Log($"Skip delete confirmation: {value}", LogLevel.Info);
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
                    _loggingService.Log($"Auto-refresh after operations: {value}", LogLevel.Info);
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
                    _loggingService.Log($"Minimum log level: {level}", LogLevel.Info);
                }
            }
        }

        /// <summary>The directory where per-tenant audit log files are stored (shown in Settings).</summary>
        public string AuditLogDirectory => _auditLog?.LogDirectoryPath ?? "Audit log not available";

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
                _loggingService.Log($"Could not open audit log folder: {ex.Message}", LogLevel.Warning);
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
            CurrentViewModel = _pageViewModelFactory.Create(value);
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
                var teams = _sessionManager.TeamsConnected ? "Connected" : "Disconnected";
                var graph = _sessionManager.GraphConnected ? "Connected" : "Disconnected";
                return $"Teams: {teams} | Graph: {graph}";
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

            _loggingService.Log("Application started", LogLevel.Info);

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
            _loggingService.Log($"Theme changed to {(value ? "Dark" : "Light")}", LogLevel.Info);
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
            _loggingService.Log($"Settings panel {(IsSettingsOpen ? "opened" : "closed")}", LogLevel.Info);
        }

        [RelayCommand]
        private void CloseSettings()
        {
            IsSettingsOpen = false;
            _loggingService.Log("Settings panel closed", LogLevel.Info);
        }

        [RelayCommand]
        private void ClearLog()
        {
            _loggingService.Clear();
            _loggingService.Log("Log cleared", LogLevel.Info);
        }

        [RelayCommand]
        private void ToggleLogDialog()
        {
            IsLogDialogOpen = !IsLogDialogOpen;
            _loggingService.Log($"Log viewer {(IsLogDialogOpen ? "opened" : "closed")}", LogLevel.Info);
        }

        [RelayCommand]
        private void CloseLogDialog()
        {
            IsLogDialogOpen = false;
            _loggingService.Log("Log viewer closed", LogLevel.Info);
        }

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

                _disposed = true;
            }
        }
    }
} 
