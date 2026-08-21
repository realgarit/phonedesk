using System.Collections.Generic;
using Moq;
using PhoneDesk.Localization;
using PhoneDesk.Models;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Tests.TestSupport
{
    /// <summary>
    /// Wires all ~9 <see cref="PhoneDesk.ViewModels.ViewModelBase"/> collaborators with sane,
    /// non-throwing defaults so per-ViewModel tests only need to override the mocks that matter for the
    /// scenario under test. Every ViewModel constructor test should start from
    /// <c>new ViewModelTestHarness()</c> rather than hand-rolling mocks.
    /// </summary>
    public class ViewModelTestHarness
    {
        public Mock<IPowerShellContextService> PowerShellContextService { get; } = new();
        public Mock<IPowerShellCommandService> PowerShellCommandService { get; } = new();
        public Mock<ILoggingService> LoggingService { get; } = new();
        public Mock<ISessionManager> SessionManager { get; } = new();
        public Mock<INavigationService> NavigationService { get; } = new();
        public Mock<IErrorHandlingService> ErrorHandlingService { get; } = new();
        public Mock<IValidationService> ValidationService { get; } = new();
        public Mock<ISharedStateService> SharedStateService { get; } = new();
        public Mock<IDialogService> DialogService { get; } = new();
        public ITranslationService TranslationService { get; }

        public ViewModelTestHarness()
        {
            TranslationService = new TranslationService(
                new InMemoryUserPreferencesStore(),
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

            // Session: valid, not expired, so ExecutePowerShellCommandAsync's pre-flight check passes by default.
            SessionManager.SetupGet(s => s.IsSessionExpired).Returns(false);
            SessionManager.SetupGet(s => s.IsSessionValid).Returns(true);

            // Validation: everything valid by default.
            ValidationService.Setup(v => v.ValidatePrerequisites()).Returns(new ValidationResult());
            ValidationService.Setup(v => v.ValidateVariables(It.IsAny<IPhoneManagerVariables>())).Returns(new ValidationResult());

            // Shared state: real defaults, dialogs skipped so PreviewAndExecuteAsync / ConfirmAndExecuteAsync
            // fall straight through to execution unless a test opts into the dialog path explicitly.
            SharedStateService.SetupGet(s => s.Variables).Returns(new PhoneManagerVariables());
            SharedStateService.SetupGet(s => s.SkipScriptPreview).Returns(true);
            SharedStateService.SetupGet(s => s.SkipDeleteConfirmation).Returns(true);
            SharedStateService.SetupGet(s => s.AutoRefreshAfterOperations).Returns(true);

            // Dialogs: confirm/preview by default; a test that wants a user-cancel overrides these.
            DialogService.Setup(d => d.ShowScriptPreviewAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            DialogService.Setup(d => d.ShowConfirmationWithPreviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            DialogService.Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            DialogService.Setup(d => d.ShowMessageAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

            // Default PowerShell execution: a benign SUCCESS payload, no errors.
            SetExecutionResult("SUCCESS");
        }

        /// <summary>Stubs ExecuteCommandWithDetailsAsync to return the given raw output with no structured errors.</summary>
        public void SetExecutionResult(string output, bool hadErrors = false, IReadOnlyList<PowerShellErrorInfo>? errors = null)
        {
            var result = new PowerShellExecutionResult
            {
                Output = output,
                HadErrors = hadErrors,
                Errors = errors ?? System.Array.Empty<PowerShellErrorInfo>()
            };

            PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<IProgress<PowerShellProgress>?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);

            PowerShellContextService
                .Setup(p => p.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(output);

            PowerShellContextService
                .Setup(p => p.ExecuteCommandAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(output);
        }

        /// <summary>Makes the next ExecuteCommandWithDetailsAsync call throw <see cref="OperationCanceledException"/>.</summary>
        public void SetCancelled()
        {
            PowerShellContextService
                .Setup(p => p.ExecuteCommandWithDetailsAsync(
                    It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<IProgress<PowerShellProgress>?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());
        }

        /// <summary>Simulates an expired session, which trips the pre-flight check in ExecutePowerShellCommandAsync.</summary>
        public void SetSessionExpired()
        {
            SessionManager.SetupGet(s => s.IsSessionExpired).Returns(true);
            SessionManager.SetupGet(s => s.IsSessionValid).Returns(true);
        }

        private sealed class InMemoryUserPreferencesStore : IUserPreferencesStore
        {
            public AppLanguage? LoadLanguage() => null;

            public void SaveLanguage(AppLanguage language)
            {
            }
        }
    }
}
