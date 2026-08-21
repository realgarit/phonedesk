using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.ViewModels
{
    public partial class WelcomeViewModel : ViewModelBase
    {
        public WelcomeViewModel(
            IPowerShellContextService powerShellContextService,
            IPowerShellCommandService powerShellCommandService,
            ILoggingService loggingService,
            ISessionManager sessionManager,
            INavigationService navigationService,
            IErrorHandlingService errorHandlingService,
            IValidationService validationService,
            IAuditLog? auditLog = null,
            ITranslationService? translationService = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, auditLog: auditLog, translationService: translationService)
        {
            _loggingService.Log("Welcome page loaded", LogLevel.Info);
        }

        [ObservableProperty]
        private string _welcomeMessage = "Welcome to PhoneDesk! This application will help you manage your Microsoft Teams phone system configuration.";

        [RelayCommand]
        private new void NavigateToGetStarted()
        {
            NavigateTo("GetStarted");
        }

        [RelayCommand]
        private void OpenDocumentation()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/realgarit/phonedesk",
                    UseShellExecute = true
                });
                _loggingService.Log("Opening documentation in browser", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Failed to open documentation: {ex.Message}", LogLevel.Error);
            }
        }
    }
} 
