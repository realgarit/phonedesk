using PhoneDesk.Services.Interfaces;
using PhoneDesk.Localization;

namespace PhoneDesk.Services
{
    /// <summary>
    /// Service for handling and displaying errors.
    /// This service is now decoupled from UI framework dependencies via IDialogService.
    /// </summary>
    public class ErrorHandlingService : IErrorHandlingService
    {
        private readonly ILoggingService _loggingService;
        private readonly IDialogService _dialogService;
        private readonly ITranslationService? _translationService;

        public ErrorHandlingService(
            ILoggingService loggingService,
            IDialogService dialogService,
            ITranslationService? translationService = null)
        {
            _loggingService = loggingService;
            _dialogService = dialogService;
            _translationService = translationService;
        }

        private string GetText(
            UiTextKey key,
            string fallback,
            IReadOnlyDictionary<string, object?>? parameters = null)
            => _translationService?.Get(key, parameters) ?? FormatFallback(fallback, parameters);

        private static string FormatFallback(string template, IReadOnlyDictionary<string, object?>? parameters)
        {
            if (parameters is null || parameters.Count == 0)
            {
                return template;
            }

            var value = template;
            foreach (var parameter in parameters)
            {
                value = value.Replace(
                    "{" + parameter.Key + "}",
                    parameter.Value?.ToString() ?? string.Empty,
                    StringComparison.Ordinal);
            }

            return value;
        }

        public async Task HandlePowerShellError(string command, string error, string context = "")
        {
            var cleanCommand = command?.Replace("\r", "").Replace("\n", " ") ?? "";
            var message = GetText(
                UiTextKey.ErrorPowerShellLog,
                "PowerShell Error in {context}:\nCommand: {command}\nError: {error}",
                new Dictionary<string, object?>
                {
                    ["context"] = context,
                    ["command"] = cleanCommand,
                    ["error"] = error
                });
            _loggingService.Log(message, LogLevel.Error);
            
            await _dialogService.ShowMessageAsync(
                GetText(UiTextKey.ErrorPowerShellTitle, ConstantsService.ErrorDialogTitles.PowerShellError),
                GetText(
                    UiTextKey.ErrorPowerShellMessage,
                    "An error occurred while executing the PowerShell command.\n\n{error}",
                    new Dictionary<string, object?> { ["error"] = error })
            );
        }

        public async Task HandleValidationError(string message, string context = "")
        {
            var fullMessage = GetText(
                UiTextKey.ErrorValidationLog,
                "Validation Error in {context}: {message}",
                new Dictionary<string, object?>
                {
                    ["context"] = context,
                    ["message"] = message
                });
            _loggingService.Log(fullMessage, LogLevel.Warning);
            
            await _dialogService.ShowMessageAsync(
                GetText(UiTextKey.ErrorValidationTitle, ConstantsService.ErrorDialogTitles.ValidationError),
                message
            );
        }

        public async Task HandleConnectionError(string service, string error)
        {
            var message = GetText(
                UiTextKey.ErrorConnectionLog,
                "Failed to connect to {service}: {error}",
                new Dictionary<string, object?>
                {
                    ["service"] = service,
                    ["error"] = error
                });
            _loggingService.Log(message, LogLevel.Error);
            
            await _dialogService.ShowMessageAsync(
                GetText(UiTextKey.ErrorConnectionTitle, ConstantsService.ErrorDialogTitles.ConnectionError),
                GetText(
                    UiTextKey.ErrorConnectionMessage,
                    "Failed to connect to {service}:\n\n{error}",
                    new Dictionary<string, object?>
                    {
                        ["service"] = service,
                        ["error"] = error
                    })
            );
        }

        public async Task HandleGenericError(string message, string context = "")
        {
            var fullMessage = GetText(
                UiTextKey.ErrorGenericLog,
                "Error in {context}: {message}",
                new Dictionary<string, object?>
                {
                    ["context"] = context,
                    ["message"] = message
                });
            _loggingService.Log(fullMessage, LogLevel.Error);
            
            await _dialogService.ShowMessageAsync(
                GetText(UiTextKey.ErrorGenericTitle, ConstantsService.ErrorDialogTitles.Error),
                message
            );
        }

        public async Task<bool> HandleConfirmation(string message, string title = ConstantsService.ErrorDialogTitles.Confirmation)
        {
            _loggingService.Log(
                GetText(
                    UiTextKey.ErrorConfirmationLog,
                    "User confirmation requested: {title} - {message}",
                    new Dictionary<string, object?>
                    {
                        ["title"] = title,
                        ["message"] = message
                    }),
                LogLevel.Info);
            return await _dialogService.ShowConfirmationAsync(
                title == ConstantsService.ErrorDialogTitles.Confirmation
                    ? GetText(UiTextKey.ErrorConfirmationTitle, ConstantsService.ErrorDialogTitles.Confirmation)
                    : title,
                message);
        }

        public async Task ShowSuccess(string message, string title = ConstantsService.ErrorDialogTitles.Success)
        {
            _loggingService.Log(
                GetText(
                    UiTextKey.ErrorSuccessLog,
                    "Success: {title} - {message}",
                    new Dictionary<string, object?>
                    {
                        ["title"] = title,
                        ["message"] = message
                    }),
                LogLevel.Success);
            await _dialogService.ShowMessageAsync(
                title == ConstantsService.ErrorDialogTitles.Success
                    ? GetText(UiTextKey.ErrorSuccessTitle, ConstantsService.ErrorDialogTitles.Success)
                    : title,
                message);
        }

        public async Task ShowInfo(string message, string title = ConstantsService.ErrorDialogTitles.Information)
        {
            _loggingService.Log(
                GetText(
                    UiTextKey.ErrorInfoLog,
                    "Info: {title} - {message}",
                    new Dictionary<string, object?>
                    {
                        ["title"] = title,
                        ["message"] = message
                    }),
                LogLevel.Info);
            await _dialogService.ShowMessageAsync(
                title == ConstantsService.ErrorDialogTitles.Information
                    ? GetText(UiTextKey.ErrorInfoTitle, ConstantsService.ErrorDialogTitles.Information)
                    : title,
                message);
        }
    }
}
