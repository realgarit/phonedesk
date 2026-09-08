using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Services
{
    /// <summary>
    /// Resolves the ViewModel for a navigation page name from the DI container. Lives at the
    /// composition boundary (it's allowed to know the container); ViewModels and Views do not.
    /// Accepts both the spaced page constants ("Get Started") and the space-stripped sidebar tags
    /// ("GetStarted"), mirroring the old PageTemplateSelector normalization.
    /// </summary>
    public interface IPageViewModelFactory
    {
        ViewModelBase Create(string page);
    }

    public class PageViewModelFactory : IPageViewModelFactory
    {
        private static readonly Dictionary<string, string> PageKeyMap = BuildPageKeyMap();

        private readonly IServiceProvider _services;

        public PageViewModelFactory(IServiceProvider services)
        {
            _services = services;
        }

        private static Dictionary<string, string> BuildPageKeyMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in new[]
            {
                ConstantsService.Pages.Welcome,
                ConstantsService.Pages.Dashboard,
                ConstantsService.Pages.HealthCheck,
                ConstantsService.Pages.GetStarted,
                ConstantsService.Pages.Variables,
                ConstantsService.Pages.M365Groups,
                ConstantsService.Pages.CallQueues,
                ConstantsService.Pages.AutoAttendants,
                ConstantsService.Pages.Holidays,
                ConstantsService.Pages.Documentation,
                ConstantsService.Pages.Wizard,
                ConstantsService.Pages.BulkOperations,
                ConstantsService.Pages.History,
            })
            {
                map[page] = page;
                map[page.Replace(" ", "")] = page;
            }
            return map;
        }

        public ViewModelBase Create(string page)
        {
            var canonical = PageKeyMap.TryGetValue(page ?? string.Empty, out var c)
                ? c
                : ConstantsService.Pages.Welcome;

            return canonical switch
            {
                ConstantsService.Pages.Welcome => _services.GetRequiredService<WelcomeViewModel>(),
                ConstantsService.Pages.Dashboard => _services.GetRequiredService<DashboardViewModel>(),
                ConstantsService.Pages.HealthCheck => _services.GetRequiredService<HealthCheckViewModel>(),
                ConstantsService.Pages.GetStarted => _services.GetRequiredService<GetStartedViewModel>(),
                ConstantsService.Pages.Variables => _services.GetRequiredService<VariablesViewModel>(),
                ConstantsService.Pages.M365Groups => _services.GetRequiredService<M365GroupsViewModel>(),
                ConstantsService.Pages.CallQueues => _services.GetRequiredService<CallQueuesViewModel>(),
                ConstantsService.Pages.AutoAttendants => _services.GetRequiredService<AutoAttendantsViewModel>(),
                ConstantsService.Pages.Holidays => _services.GetRequiredService<HolidaysViewModel>(),
                ConstantsService.Pages.Documentation => _services.GetRequiredService<DocumentationViewModel>(),
                ConstantsService.Pages.Wizard => _services.GetRequiredService<WizardViewModel>(),
                ConstantsService.Pages.BulkOperations => _services.GetRequiredService<BulkOperationsViewModel>(),
                ConstantsService.Pages.History => _services.GetRequiredService<HistoryViewModel>(),
                _ => _services.GetRequiredService<WelcomeViewModel>()
            };
        }
    }
}
