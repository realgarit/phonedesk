using System.Collections.ObjectModel;
using System.ComponentModel;
using Moq;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    public class MainWindowViewModelTests
    {
        private readonly Mock<IPageViewModelFactory> _pageViewModelFactory = new();
        private readonly Mock<IUpdateCheckService> _updateCheckService = new();
        private readonly Mock<IUpdateInstallerService> _updateInstallerService = new();
        private readonly Mock<IBundledModuleVersionService> _bundledModuleVersionService = new();

        public MainWindowViewModelTests()
        {
            // A sentinel placeholder is enough: MainWindowViewModel treats CurrentViewModel as an opaque
            // object resolved via the factory, it never inspects the concrete type itself.
            _pageViewModelFactory.Setup(f => f.Create(It.IsAny<string>())).Returns((ViewModelBase)null!);
            _updateCheckService.Setup(u => u.CheckForUpdateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((UpdateInfo?)null);
            _bundledModuleVersionService.SetupGet(m => m.TeamsModuleVersion).Returns("7.8.0");
            _bundledModuleVersionService.SetupGet(m => m.GraphModuleVersion).Returns("2.38.1");
            _bundledModuleVersionService.SetupGet(m => m.PowerShellSdkVersion).Returns("7.6.0");
        }

        private MainWindowViewModel CreateViewModel(ViewModelTestHarness harness)
        {
            // MainWindowViewModel wires a CollectionChanged handler onto ILoggingService.LogEntries in
            // its constructor; the shared harness leaves LogEntries unstubbed (unused by the other VMs),
            // so this VM needs its own backing collection.
            harness.LoggingService.SetupGet(l => l.LogEntries).Returns(new ObservableCollection<string>());

            return new MainWindowViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object,
                _pageViewModelFactory.Object,
                _updateCheckService.Object,
                _updateInstallerService.Object,
                _bundledModuleVersionService.Object,
                translationService: harness.TranslationService);
        }

        [Fact]
        public void Construction_ResolvesInitialPageFromFactoryAndLogsStartup()
        {
            var harness = new ViewModelTestHarness();

            CreateViewModel(harness);

            _pageViewModelFactory.Verify(f => f.Create(ConstantsService.Pages.Welcome), Times.Once);
            harness.LoggingService.Verify(l => l.Log("Application started", LogLevel.Info), Times.Once);
        }

        [Fact]
        public void Construction_ChecksForUpdatesOnStartup()
        {
            var harness = new ViewModelTestHarness();

            CreateViewModel(harness);

            _updateCheckService.Verify(u => u.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public void Construction_UpdateAvailable_ShowsUpdateBanner()
        {
            var harness = new ViewModelTestHarness();
            _updateCheckService.Setup(u => u.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateInfo("2.0.0", "https://example.com/release"));

            var vm = CreateViewModel(harness);

            Assert.True(vm.IsUpdateBannerVisible);
            Assert.True(vm.IsUpdateAvailable);
            Assert.Contains("2.0.0", vm.UpdateBannerMessage);
        }

        [Fact]
        public void VisibleUpdateBanner_RefreshesWhenLanguageChangesToGerman()
        {
            var harness = new ViewModelTestHarness();
            _updateCheckService.Setup(u => u.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateInfo("2.0.0", "https://example.com/release"));

            var vm = CreateViewModel(harness);
            Assert.Equal("Version 2.0.0 is available.", vm.UpdateBannerMessage);

            harness.TranslationService.CurrentLanguage = Localization.AppLanguage.German;

            Assert.True(vm.IsUpdateBannerVisible);
            Assert.Equal("Version 2.0.0 ist verfügbar.", vm.UpdateBannerMessage);
        }

        [Fact]
        public void DismissUpdateBannerCommand_HidesBanner()
        {
            var harness = new ViewModelTestHarness();
            _updateCheckService.Setup(u => u.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateInfo("2.0.0", "https://example.com/release"));
            var vm = CreateViewModel(harness);

            vm.DismissUpdateBannerCommand.Execute(null);

            Assert.False(vm.IsUpdateBannerVisible);
            Assert.True(vm.IsUpdateAvailable);
        }

        [Fact]
        public async Task InstallUpdateCommand_DownloadsAndLaunchesVerifiedInstaller()
        {
            var harness = new ViewModelTestHarness();
            var asset = new UpdateAsset(
                "phonedesk-win-x64-setup.exe",
                "https://github.com/realgarit/phonedesk/releases/download/v2.0.0/setup.exe",
                new string('a', 64));
            _updateInstallerService.SetupGet(u => u.IsSupported).Returns(true);
            _updateInstallerService
                .Setup(u => u.DownloadInstallerAsync(
                    asset,
                    It.IsAny<IProgress<UpdateDownloadProgress>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("verified-setup.exe");
            _updateCheckService.Setup(u => u.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateInfo("2.0.0", "https://example.com/release", asset));
            var vm = CreateViewModel(harness);

            await vm.InstallUpdateCommand.ExecuteAsync(null);

            _updateInstallerService.Verify(
                u => u.LaunchInstaller("verified-setup.exe"),
                Times.Once);
            Assert.False(vm.IsUpdateInProgress);
        }

        [Fact]
        public async Task InstallUpdateCommand_UserDeclines_DoesNotDownload()
        {
            var harness = new ViewModelTestHarness();
            var asset = new UpdateAsset(
                "phonedesk-win-x64-setup.exe",
                "https://github.com/realgarit/phonedesk/releases/download/v2.0.0/setup.exe",
                new string('a', 64));
            harness.DialogService
                .Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(false);
            _updateInstallerService.SetupGet(u => u.IsSupported).Returns(true);
            _updateCheckService.Setup(u => u.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateInfo("2.0.0", "https://example.com/release", asset));
            var vm = CreateViewModel(harness);

            await vm.InstallUpdateCommand.ExecuteAsync(null);

            _updateInstallerService.Verify(
                u => u.DownloadInstallerAsync(
                    It.IsAny<UpdateAsset>(),
                    It.IsAny<IProgress<UpdateDownloadProgress>>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task InstallUpdateCommand_CancelRecovery_UsesCurrentLanguage()
        {
            var harness = new ViewModelTestHarness();
            var asset = new UpdateAsset(
                "phonedesk-win-x64-setup.exe",
                "https://github.com/realgarit/phonedesk/releases/download/v2.0.0/setup.exe",
                new string('a', 64));
            _updateInstallerService.SetupGet(u => u.IsSupported).Returns(true);
            _updateInstallerService
                .Setup(u => u.DownloadInstallerAsync(
                    asset,
                    It.IsAny<IProgress<UpdateDownloadProgress>>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());
            _updateCheckService.Setup(u => u.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateInfo("2.0.0", "https://example.com/release", asset));
            var vm = CreateViewModel(harness);
            harness.TranslationService.CurrentLanguage = Localization.AppLanguage.German;

            await vm.InstallUpdateCommand.ExecuteAsync(null);

            Assert.Equal("Version 2.0.0 ist verfügbar.", vm.UpdateBannerMessage);
        }

        [Fact]
        public async Task InstallUpdateCommand_FailureRecovery_UsesCurrentLanguage()
        {
            var harness = new ViewModelTestHarness();
            var asset = new UpdateAsset(
                "phonedesk-win-x64-setup.exe",
                "https://github.com/realgarit/phonedesk/releases/download/v2.0.0/setup.exe",
                new string('a', 64));
            _updateInstallerService.SetupGet(u => u.IsSupported).Returns(true);
            _updateInstallerService
                .Setup(u => u.DownloadInstallerAsync(
                    asset,
                    It.IsAny<IProgress<UpdateDownloadProgress>>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new UpdateInstallationException("checksum mismatch"));
            _updateCheckService.Setup(u => u.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateInfo("2.0.0", "https://example.com/release", asset));
            var vm = CreateViewModel(harness);
            harness.TranslationService.CurrentLanguage = Localization.AppLanguage.German;

            await vm.InstallUpdateCommand.ExecuteAsync(null);

            Assert.Equal("Version 2.0.0 ist verfügbar.", vm.UpdateBannerMessage);
        }

        [Fact]
        public void NavigateToCommand_DelegatesToNavigationService()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.NavigateToCommand.Execute(ConstantsService.Pages.M365Groups);

            harness.NavigationService.Verify(n => n.NavigateTo(ConstantsService.Pages.M365Groups), Times.Once);
        }

        [Fact]
        public void CurrentPageChanged_ResolvesNewViewModelFromFactory()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            _pageViewModelFactory.Invocations.Clear();

            vm.CurrentPage = ConstantsService.Pages.Holidays;

            _pageViewModelFactory.Verify(f => f.Create(ConstantsService.Pages.Holidays), Times.Once);
        }

        [Fact]
        public void ToggleSettingsCommand_TogglesIsSettingsOpen()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.ToggleSettingsCommand.Execute(null);
            Assert.True(vm.IsSettingsOpen);

            vm.ToggleSettingsCommand.Execute(null);
            Assert.False(vm.IsSettingsOpen);
        }

        [Fact]
        public void CurrentLanguage_StartsInEnglish()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            Assert.Equal(AppLanguage.English, vm.CurrentLanguage);
        }

        [Fact]
        public void CurrentLanguage_SettingGermanPersistsChoiceAndRaisesNotification()
        {
            var store = new TrackingUserPreferencesStore();
            var translationService = CreateTranslationService(store);
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness, translationService);
            var notifications = new List<string?>();
            vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

            vm.CurrentLanguage = AppLanguage.German;

            Assert.Equal(AppLanguage.German, vm.CurrentLanguage);
            Assert.Equal(AppLanguage.German, translationService.CurrentLanguage);
            Assert.Equal(AppLanguage.German, store.SavedLanguage);
            Assert.Equal(1, store.SaveCount);
            Assert.Contains(nameof(MainWindowViewModel.CurrentLanguage), notifications);
        }

        [Fact]
        public void CurrentLanguage_SettingSameLanguageTwiceDoesNotPersistRepeatedly()
        {
            var store = new TrackingUserPreferencesStore();
            var translationService = CreateTranslationService(store);
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness, translationService);

            vm.CurrentLanguage = AppLanguage.German;
            vm.CurrentLanguage = AppLanguage.German;

            Assert.Equal(1, store.SaveCount);
        }

        [Fact]
        public void CloseSettingsCommand_ClosesSettingsPanel()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.IsSettingsOpen = true;

            vm.CloseSettingsCommand.Execute(null);

            Assert.False(vm.IsSettingsOpen);
        }

        [Fact]
        public void ToggleLogDialogCommand_TogglesIsLogDialogOpen()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.ToggleLogDialogCommand.Execute(null);
            Assert.True(vm.IsLogDialogOpen);

            vm.ToggleLogDialogCommand.Execute(null);
            Assert.False(vm.IsLogDialogOpen);
        }

        [Fact]
        public void CloseLogDialogCommand_ClosesLogDialog()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.IsLogDialogOpen = true;

            vm.CloseLogDialogCommand.Execute(null);

            Assert.False(vm.IsLogDialogOpen);
        }

        [Fact]
        public void ClearLogCommand_ClearsLoggingService()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.ClearLogCommand.Execute(null);

            harness.LoggingService.Verify(l => l.Clear(), Times.Once);
        }

        [Fact]
        public void OpenUpdatePageCommand_NoReleaseUrl_DoesNotThrow()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            var exception = Record.Exception(() => vm.OpenUpdatePageCommand.Execute(null));

            Assert.Null(exception);
        }

        [Fact]
        public void SkipScriptPreview_ProxiesToSharedStateService()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.SkipScriptPreview = true;

            harness.SharedStateService.VerifySet(s => s.SkipScriptPreview = true, Times.Once);
        }

        [Fact]
        public void RefreshConnectionStatus_RaisesPropertyChangedForConnectionProperties()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            var raisedProperties = new List<string>();
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != null)
                {
                    raisedProperties.Add(e.PropertyName);
                }
            };

            vm.RefreshConnectionStatus();

            Assert.Contains(nameof(MainWindowViewModel.IsTeamsConnected), raisedProperties);
            Assert.Contains(nameof(MainWindowViewModel.IsGraphConnected), raisedProperties);
            Assert.Contains(nameof(MainWindowViewModel.ConnectionStatusText), raisedProperties);
        }

        [Fact]
        public void Dispose_DetachesEventHandlersWithoutThrowing()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            var exception = Record.Exception(() => vm.Dispose());

            Assert.Null(exception);
        }

        private MainWindowViewModel CreateViewModel(ViewModelTestHarness harness, ITranslationService translationService)
        {
            harness.LoggingService.SetupGet(l => l.LogEntries).Returns(new ObservableCollection<string>());

            return new MainWindowViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object,
                _pageViewModelFactory.Object,
                _updateCheckService.Object,
                _updateInstallerService.Object,
                _bundledModuleVersionService.Object,
                translationService: translationService);
        }

        private static ITranslationService CreateTranslationService(TrackingUserPreferencesStore store)
            => new TranslationService(
                store,
                new Dictionary<AppLanguage, IReadOnlyDictionary<UiTextKey, string>>
                {
                    [AppLanguage.English] = new Dictionary<UiTextKey, string>
                    {
                        [UiTextKey.SettingsTitle] = "Settings",
                        [UiTextKey.UpdateAvailable] = "Version {version} is available.",
                        [UiTextKey.UpdateDownloading] = "Downloading version {version}...",
                        [UiTextKey.UpdateDownloadingProgress] = "Downloading version {version}... {progress}%",
                        [UiTextKey.UpdateStartingInstaller] = "Starting the verified installer..."
                    },
                    [AppLanguage.German] = new Dictionary<UiTextKey, string>
                    {
                        [UiTextKey.SettingsTitle] = "Einstellungen",
                        [UiTextKey.UpdateAvailable] = "Version {version} ist verfügbar.",
                        [UiTextKey.UpdateDownloading] = "Version {version} wird heruntergeladen...",
                        [UiTextKey.UpdateDownloadingProgress] = "Version {version} wird heruntergeladen... {progress}%",
                        [UiTextKey.UpdateStartingInstaller] = "Das verifizierte Installationsprogramm wird gestartet..."
                    }
                });

        private sealed class TrackingUserPreferencesStore : IUserPreferencesStore
        {
            public AppLanguage? SavedLanguage { get; private set; }

            public int SaveCount { get; private set; }

            public AppLanguage? LoadLanguage() => SavedLanguage;

            public void SaveLanguage(AppLanguage language)
            {
                SavedLanguage = language;
                SaveCount++;
            }
        }
    }
}
