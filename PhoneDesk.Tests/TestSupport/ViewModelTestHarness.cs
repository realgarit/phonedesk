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
                        [UiTextKey.UpdateStartingInstaller] = "Starting the verified installer...",
                        [UiTextKey.DialogOk] = "OK",
                        [UiTextKey.DialogCancel] = "Cancel",
                        [UiTextKey.DialogExecute] = "Execute",
                        [UiTextKey.DialogConfirmAndExecute] = "Confirm & Execute",
                        [UiTextKey.DialogReviewPowerShellScript] = "Review the PowerShell script that will be executed:",
                        [UiTextKey.DialogScriptToBeExecuted] = "Script to be executed:",
                        [UiTextKey.DialogPowerShellScriptWatermark] = "PowerShell Script",
                        [UiTextKey.DialogPreviewTitle] = "Preview: {context}",
                        [UiTextKey.DialogConfirmTitle] = "Confirm: {context}",
                        [UiTextKey.RuntimeCancellationRequestedLog] = "Cancellation requested by user",
                        [UiTextKey.RuntimeCancelling] = "Cancelling…",
                        [UiTextKey.RuntimeSessionExpiredMessage] = "Your session has expired (24h timeout). Please reconnect to Teams and Microsoft Graph.",
                        [UiTextKey.RuntimeSessionExpiredShort] = "Session expired. Please reconnect.",
                        [UiTextKey.RuntimeOperationCancelledLog] = "Operation cancelled: {context}",
                        [UiTextKey.RuntimeOperationCancelled] = "Operation cancelled.",
                        [UiTextKey.RuntimeOperationCancelledByUser] = "Operation cancelled by user",
                        [UiTextKey.RuntimeErrorWithDetails] = "Error: {error}",
                        [UiTextKey.RuntimeScriptPreviewCancelledLog] = "User cancelled script preview for: {context}",
                        [UiTextKey.RuntimeDestructiveOperationCancelledLog] = "User cancelled destructive operation: {context}",
                        [UiTextKey.ErrorPowerShellTitle] = "PowerShell Error",
                        [UiTextKey.ErrorPowerShellMessage] = "An error occurred while executing the PowerShell command.\n\n{error}",
                        [UiTextKey.ErrorPowerShellLog] = "PowerShell Error in {context}:\nCommand: {command}\nError: {error}",
                        [UiTextKey.ErrorValidationTitle] = "Validation Error",
                        [UiTextKey.ErrorValidationLog] = "Validation Error in {context}: {message}",
                        [UiTextKey.ErrorConnectionTitle] = "Connection Error",
                        [UiTextKey.ErrorConnectionMessage] = "Failed to connect to {service}:\n\n{error}",
                        [UiTextKey.ErrorConnectionLog] = "Failed to connect to {service}: {error}",
                        [UiTextKey.ErrorGenericTitle] = "Error",
                        [UiTextKey.ErrorGenericLog] = "Error in {context}: {message}",
                        [UiTextKey.ErrorConfirmationTitle] = "Confirmation",
                        [UiTextKey.ErrorConfirmationLog] = "User confirmation requested: {title} - {message}",
                        [UiTextKey.ErrorSuccessTitle] = "Success",
                        [UiTextKey.ErrorSuccessLog] = "Success: {title} - {message}",
                        [UiTextKey.ErrorInfoTitle] = "Information",
                        [UiTextKey.ErrorInfoLog] = "Info: {title} - {message}",
                        [UiTextKey.MainInstallUpdateDialogTitle] = "Install update",
                        [UiTextKey.MainInstallUpdateDialogMessage] = "Download and install version {version}? PhoneDesk will close and restart automatically.",
                        [UiTextKey.MainUpdateFailedTitle] = "Update failed",
                        [UiTextKey.HolidaysVerifyAutoAttendantSuccess] = "Auto attendant '{name}' verified successfully and is ready for holiday configuration",
                        [UiTextKey.HolidaysResetStateStatus] = "Holiday state reset. You can now create a new holiday.",
                        [UiTextKey.HolidaysVariablesNotFoundError] = "Error: Variables not found",
                        [UiTextKey.HolidaysNoHolidaysConfiguredError] = "Error: No holidays configured. Please add holidays in the Variables page first.",
                        [UiTextKey.HolidaysHolidaySeriesCreatedSuccess] = "Holiday series '{name}' created successfully with {count} dates!",
                        [UiTextKey.HolidaysCreateHolidaySeriesError] = "Error creating holiday series: {details}",
                        [UiTextKey.HolidaysAutoAttendantNameRequiredError] = "Error: Auto attendant name cannot be empty",
                        [UiTextKey.HolidaysVerifyAutoAttendantMissingError] = "Error: Auto attendant '{name}' not found or not accessible: {details}",
                        [UiTextKey.HolidaysAttachHolidaySuccess] = "Successfully attached holiday '{holidayName}' to auto attendant '{name}'",
                        [UiTextKey.HolidaysAttachHolidayError] = "Error attaching holiday to auto attendant: {details}"
                    },
                    [AppLanguage.German] = new Dictionary<UiTextKey, string>
                    {
                        [UiTextKey.SettingsTitle] = "Einstellungen",
                        [UiTextKey.UpdateAvailable] = "Version {version} ist verfügbar.",
                        [UiTextKey.UpdateDownloading] = "Version {version} wird heruntergeladen...",
                        [UiTextKey.UpdateDownloadingProgress] = "Version {version} wird heruntergeladen... {progress}%",
                        [UiTextKey.UpdateStartingInstaller] = "Das verifizierte Installationsprogramm wird gestartet...",
                        [UiTextKey.DialogOk] = "OK",
                        [UiTextKey.DialogCancel] = "Abbrechen",
                        [UiTextKey.DialogExecute] = "Ausführen",
                        [UiTextKey.DialogConfirmAndExecute] = "Bestätigen und ausführen",
                        [UiTextKey.DialogReviewPowerShellScript] = "Prüfen Sie das PowerShell-Skript, das ausgeführt wird:",
                        [UiTextKey.DialogScriptToBeExecuted] = "Skript, das ausgeführt wird:",
                        [UiTextKey.DialogPowerShellScriptWatermark] = "PowerShell-Skript",
                        [UiTextKey.DialogPreviewTitle] = "Vorschau: {context}",
                        [UiTextKey.DialogConfirmTitle] = "Bestätigen: {context}",
                        [UiTextKey.RuntimeCancellationRequestedLog] = "Abbruch durch Benutzer angefordert",
                        [UiTextKey.RuntimeCancelling] = "Abbruch läuft…",
                        [UiTextKey.RuntimeSessionExpiredMessage] = "Ihre Sitzung ist abgelaufen (24-Stunden-Timeout). Verbinden Sie Teams und Microsoft Graph erneut.",
                        [UiTextKey.RuntimeSessionExpiredShort] = "Sitzung abgelaufen. Bitte erneut verbinden.",
                        [UiTextKey.RuntimeOperationCancelledLog] = "Vorgang abgebrochen: {context}",
                        [UiTextKey.RuntimeOperationCancelled] = "Vorgang abgebrochen.",
                        [UiTextKey.RuntimeOperationCancelledByUser] = "Vorgang durch Benutzer abgebrochen",
                        [UiTextKey.RuntimeErrorWithDetails] = "Fehler: {error}",
                        [UiTextKey.RuntimeScriptPreviewCancelledLog] = "Benutzer hat die Skriptvorschau abgebrochen: {context}",
                        [UiTextKey.RuntimeDestructiveOperationCancelledLog] = "Benutzer hat den destruktiven Vorgang abgebrochen: {context}",
                        [UiTextKey.ErrorPowerShellTitle] = "PowerShell-Fehler",
                        [UiTextKey.ErrorPowerShellMessage] = "Beim Ausführen des PowerShell-Befehls ist ein Fehler aufgetreten.\n\n{error}",
                        [UiTextKey.ErrorPowerShellLog] = "PowerShell-Fehler in {context}:\nBefehl: {command}\nFehler: {error}",
                        [UiTextKey.ErrorValidationTitle] = "Validierungsfehler",
                        [UiTextKey.ErrorValidationLog] = "Validierungsfehler in {context}: {message}",
                        [UiTextKey.ErrorConnectionTitle] = "Verbindungsfehler",
                        [UiTextKey.ErrorConnectionMessage] = "Verbindung zu {service} fehlgeschlagen:\n\n{error}",
                        [UiTextKey.ErrorConnectionLog] = "Verbindung zu {service} fehlgeschlagen: {error}",
                        [UiTextKey.ErrorGenericTitle] = "Fehler",
                        [UiTextKey.ErrorGenericLog] = "Fehler in {context}: {message}",
                        [UiTextKey.ErrorConfirmationTitle] = "Bestätigung",
                        [UiTextKey.ErrorConfirmationLog] = "Benutzerbestätigung angefordert: {title} - {message}",
                        [UiTextKey.ErrorSuccessTitle] = "Erfolg",
                        [UiTextKey.ErrorSuccessLog] = "Erfolg: {title} - {message}",
                        [UiTextKey.ErrorInfoTitle] = "Information",
                        [UiTextKey.ErrorInfoLog] = "Info: {title} - {message}",
                        [UiTextKey.MainInstallUpdateDialogTitle] = "Update installieren",
                        [UiTextKey.MainInstallUpdateDialogMessage] = "Version {version} herunterladen und installieren? PhoneDesk wird geschlossen und automatisch neu gestartet.",
                        [UiTextKey.MainUpdateFailedTitle] = "Update fehlgeschlagen",
                        [UiTextKey.HolidaysVerifyAutoAttendantSuccess] = "Automatische Telefonzentrale '{name}' wurde erfolgreich geprüft und ist für die Feiertagskonfiguration bereit.",
                        [UiTextKey.HolidaysResetStateStatus] = "Feiertagsstatus zurückgesetzt. Sie können jetzt einen neuen Feiertag erstellen.",
                        [UiTextKey.HolidaysVariablesNotFoundError] = "Fehler: Variablen nicht gefunden",
                        [UiTextKey.HolidaysNoHolidaysConfiguredError] = "Fehler: Keine Feiertage konfiguriert. Fügen Sie zuerst Feiertage auf der Seite 'Konfiguration' hinzu.",
                        [UiTextKey.HolidaysHolidaySeriesCreatedSuccess] = "Feiertagsserie '{name}' mit {count} Daten erfolgreich erstellt!",
                        [UiTextKey.HolidaysCreateHolidaySeriesError] = "Fehler beim Erstellen der Feiertagsserie: {details}",
                        [UiTextKey.HolidaysAutoAttendantNameRequiredError] = "Fehler: Name der automatischen Telefonzentrale darf nicht leer sein",
                        [UiTextKey.HolidaysVerifyAutoAttendantMissingError] = "Fehler: Automatische Telefonzentrale '{name}' wurde nicht gefunden oder ist nicht zugänglich: {details}",
                        [UiTextKey.HolidaysAttachHolidaySuccess] = "Feiertag '{holidayName}' wurde erfolgreich mit der automatischen Telefonzentrale '{name}' verknüpft",
                        [UiTextKey.HolidaysAttachHolidayError] = "Fehler beim Verknüpfen des Feiertags mit der automatischen Telefonzentrale: {details}"
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
