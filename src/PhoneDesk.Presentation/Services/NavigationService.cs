using CommunityToolkit.Mvvm.ComponentModel;
using PhoneDesk.Localization;
using PhoneDesk.Services.Interfaces;
using System.Collections.Generic;

namespace PhoneDesk.Services
{
    public class NavigationService : ObservableObject, INavigationService
    {
        private readonly ILoggingService _loggingService;
        private readonly ITranslationService? _translationService;
        private string _currentPage = ConstantsService.Pages.Welcome;

        public NavigationService(ILoggingService loggingService, ITranslationService? translationService = null)
        {
            _loggingService = loggingService;
            _translationService = translationService;
            _loggingService.Log(
                _translationService?.Get(UiTextKey.NavigationServiceInitializedLog)
                    ?? "Navigation service initialized",
                LogLevel.Info);
        }

        public string CurrentPage
        {
            get => _currentPage;
            set
            {
                if (_currentPage != value)
                {
                    _currentPage = value;
                    OnPropertyChanged();
                    _loggingService.Log(
                        _translationService?.Get(
                            UiTextKey.NavigationServiceNavigatedToPageLog,
                            new Dictionary<string, object?> { ["page"] = value })
                            ?? $"Navigated to {value} page",
                        LogLevel.Info);
                }
            }
        }

        public void NavigateTo(string page)
        {
            CurrentPage = page;
        }
    }
}
