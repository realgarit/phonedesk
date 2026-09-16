using Microsoft.Extensions.DependencyInjection;
using Moq;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services.ScriptBuilders;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    public class PageViewModelFactoryTests
    {
        private static IServiceProvider BuildServiceProvider(ViewModelTestHarness harness)
        {
            var msalAuthService = new Mock<IMsalGraphAuthenticationService>().Object;
            var documentationScriptBuilder = new Mock<IDocumentationScriptBuilder>().Object;
            var sanitizationService = new Mock<IPowerShellSanitizationService>().Object;

            var services = new ServiceCollection();

            services.AddSingleton(sp => new WelcomeViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object));

            services.AddSingleton(sp => new GetStartedViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                msalAuthService));

            var topologyCache = new TenantTopologyCache();
            services.AddSingleton(sp => new DashboardViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                new TenantTopologyAssembler(),
                topologyCache));

            var healthPreferences = new Mock<ITenantHealthPreferencesStore>();
            healthPreferences.Setup(store => store.Load(It.IsAny<string>()))
                .Returns(PhoneDesk.HealthChecks.TenantHealthPreferences.Default);
            healthPreferences.Setup(store => store.Save(It.IsAny<string>(), It.IsAny<PhoneDesk.HealthChecks.TenantHealthPreferences>()))
                .Returns(true);
            services.AddSingleton(sp => new HealthCheckViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                topologyCache,
                new TenantHealthEvaluationService(),
                new TenantHealthEnrichmentParser(),
                new TenantHealthQueryBuilder(),
                healthPreferences.Object,
                new TenantHealthCheckCache()));

            services.AddSingleton(sp => new VariablesViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object));

            services.AddSingleton(sp => new M365GroupsViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object));

            services.AddSingleton(sp => new CallQueuesViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object));

            services.AddSingleton(sp => new AutoAttendantsViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object));

            services.AddSingleton(sp => new HolidaysViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object));

            services.AddSingleton(sp => new DocumentationViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object,
                documentationScriptBuilder));

            services.AddSingleton(sp => new WizardViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object));

            services.AddSingleton(sp => new BulkOperationsViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object,
                new BulkOperationsScriptBuilder(harness.PowerShellCommandService.Object, sanitizationService)));

            var auditLog = new Mock<IAuditLog>();
            auditLog.Setup(a => a.Read()).Returns(System.Array.Empty<PhoneDesk.Audit.AuditRecord>());
            auditLog.SetupGet(a => a.LogDirectoryPath).Returns("/tmp/audit");
            services.AddSingleton(sp => new HistoryViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                auditLog.Object));

            return services.BuildServiceProvider();
        }

        private static PageViewModelFactory CreateFactory(out IServiceProvider provider)
        {
            var harness = new ViewModelTestHarness();
            provider = BuildServiceProvider(harness);
            return new PageViewModelFactory(provider);
        }

        public static IEnumerable<object[]> KnownPages()
        {
            yield return new object[] { ConstantsService.Pages.Welcome, typeof(WelcomeViewModel) };
            yield return new object[] { ConstantsService.Pages.Dashboard, typeof(DashboardViewModel) };
            yield return new object[] { ConstantsService.Pages.HealthCheck, typeof(HealthCheckViewModel) };
            yield return new object[] { ConstantsService.Pages.HealthCheck.Replace(" ", ""), typeof(HealthCheckViewModel) };
            yield return new object[] { ConstantsService.Pages.GetStarted, typeof(GetStartedViewModel) };
            yield return new object[] { ConstantsService.Pages.GetStarted.Replace(" ", ""), typeof(GetStartedViewModel) };
            yield return new object[] { ConstantsService.Pages.Variables, typeof(VariablesViewModel) };
            yield return new object[] { ConstantsService.Pages.M365Groups, typeof(M365GroupsViewModel) };
            yield return new object[] { ConstantsService.Pages.M365Groups.Replace(" ", ""), typeof(M365GroupsViewModel) };
            yield return new object[] { ConstantsService.Pages.CallQueues, typeof(CallQueuesViewModel) };
            yield return new object[] { ConstantsService.Pages.CallQueues.Replace(" ", ""), typeof(CallQueuesViewModel) };
            yield return new object[] { ConstantsService.Pages.AutoAttendants, typeof(AutoAttendantsViewModel) };
            yield return new object[] { ConstantsService.Pages.AutoAttendants.Replace(" ", ""), typeof(AutoAttendantsViewModel) };
            yield return new object[] { ConstantsService.Pages.Holidays, typeof(HolidaysViewModel) };
            yield return new object[] { ConstantsService.Pages.Documentation, typeof(DocumentationViewModel) };
            yield return new object[] { ConstantsService.Pages.Wizard, typeof(WizardViewModel) };
            yield return new object[] { ConstantsService.Pages.BulkOperations, typeof(BulkOperationsViewModel) };
            yield return new object[] { ConstantsService.Pages.History, typeof(HistoryViewModel) };
        }

        [Theory]
        [MemberData(nameof(KnownPages))]
        public void Create_KnownPage_ReturnsExpectedViewModelType(string page, Type expectedType)
        {
            var factory = CreateFactory(out _);

            var result = factory.Create(page);

            Assert.IsType(expectedType, result);
        }

        [Fact]
        public void Create_UnknownPage_FallsBackToWelcomeViewModel()
        {
            var factory = CreateFactory(out _);

            var result = factory.Create("SomeUnknownPage");

            Assert.IsType<WelcomeViewModel>(result);
        }

        [Fact]
        public void Create_NullPage_FallsBackToWelcomeViewModel()
        {
            var factory = CreateFactory(out _);

            var result = factory.Create(null!);

            Assert.IsType<WelcomeViewModel>(result);
        }
    }
}
