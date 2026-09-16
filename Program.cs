using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using System;
using PhoneDesk.Planning;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services.ScriptBuilders;
using PhoneDesk.ViewModels;

namespace PhoneDesk;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Fix for 'window handle must be configured' error in Microsoft Graph PowerShell SDK
        // This forces MSAL and Azure.Identity to use the system browser instead of WAM (Web Account Manager)
        // These must be set at the process level before any authentication modules are loaded.
        Environment.SetEnvironmentVariable("MSAL_DISABLE_WAM", "true", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("AZURE_IDENTITY_DISABLE_WAM", "true", EnvironmentVariableTarget.Process);

        var services = new ServiceCollection();
        ConfigureServices(services);
        var provider = services.BuildServiceProvider();

        // Hand the composed container to the App instance (no global static service locator).
        BuildAvaloniaApp()
            .AfterSetup(builder =>
            {
                if (builder.Instance is App app)
                {
                    app.Services = provider;
                }
            })
            .StartWithClassicDesktopLifetime(args);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Core services (singletons for app lifetime)
        services.AddSingleton<ILoggingService, LoggingService>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IPowerShellContextService, PowerShellContextService>();
        services.AddSingleton<IMsalGraphAuthenticationService, MsalGraphAuthenticationService>();
        services.AddSingleton<ISharedStateService, SharedStateService>();
        services.AddSingleton<IUserPreferencesStore, UserPreferencesStore>();
        services.AddSingleton<ITranslationService>(_ => new TranslationService(
            _.GetRequiredService<IUserPreferencesStore>(),
            new Dictionary<AppLanguage, IReadOnlyDictionary<UiTextKey, string>>
            {
                [AppLanguage.English] = TranslationCatalogLoader.Load(
                    new Uri("avares://PhoneDesk.Presentation/Resources/Localization/Strings.en.json")),
                [AppLanguage.German] = TranslationCatalogLoader.Load(
                    new Uri("avares://PhoneDesk.Presentation/Resources/Localization/Strings.de.json"))
            }));
        services.AddSingleton<IUpdateCheckService, GitHubUpdateCheckService>();
        services.AddSingleton<IUpdateInstallerService, GitHubUpdateInstallerService>();

        // Persistent audit log (issue #67): per-tenant JSON-lines under the app-data directory.
        services.AddSingleton<IAuditLog, FileAuditLog>();
        services.AddSingleton<IBundledModuleVersionService, BundledModuleVersionService>();
        services.AddSingleton<ITenantHealthPreferencesStore, TenantHealthPreferencesStore>();
        services.AddSingleton<ITenantHealthCheckCache, TenantHealthCheckCache>();
        
        // UI Services (singleton - manages UI state)
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IPortabilityFileService, PortabilityFileService>();
        services.AddSingleton<IPageViewModelFactory, PageViewModelFactory>();

        // Throttling resilience (foundations #62): shared options + retry policy, per-run bulk pacer.
        services.AddSingleton(ThrottleRetryOptions.Default);
        services.AddSingleton<IThrottleRetryPolicy, ThrottleRetryPolicy>();
        services.AddTransient<IBulkPacer, BulkPacer>();

        // Transient services (new instance per request)
        services.AddTransient<IPowerShellCommandService, PowerShellCommandService>();
        services.AddTransient<IValidationService, ValidationService>();
        services.AddTransient<IErrorHandlingService, ErrorHandlingService>();
        services.AddTransient<IPowerShellSanitizationService, PowerShellSanitizationService>();

        // Script Builders
        services.AddTransient<CommonScriptBuilder>();
        services.AddTransient<CallQueueScriptBuilder>();
        services.AddTransient<AutoAttendantScriptBuilder>();
        services.AddTransient<HolidayScriptBuilder>();
        services.AddTransient<ResourceAccountScriptBuilder>();
        services.AddTransient<DashboardScriptBuilder>();
        services.AddTransient<IDocumentationScriptBuilder, DocumentationScriptBuilder>();
        services.AddTransient<BulkOperationsScriptBuilder>();

        // Dry-run preview (issue #68): read-only plan generation + exportable plan. Neither touches the
        // tenant, executes PowerShell, nor generates script text — the plan derives purely from the same
        // configuration inputs the frozen script builders consume.
        services.AddTransient<IDryRunPlanBuilder, DryRunPlanBuilder>();
        services.AddTransient<IDryRunPlanExporter, DryRunPlanExporter>();
        services.AddTransient<ITenantAsCodeService, TenantAsCodeService>();
        services.AddTransient<ITenantDocumentationSnapshotParser, TenantDocumentationSnapshotParser>();
        services.AddTransient<ITenantHealthEvaluationService, TenantHealthEvaluationService>();
        services.AddTransient<ITenantHealthEnrichmentParser, TenantHealthEnrichmentParser>();
        services.AddTransient<ITenantHealthQueryBuilder, TenantHealthQueryBuilder>();

        // Tenant dashboard (issue #64): read-only topology assembly + session-lifetime cache.
        // The assembler is a pure transformation; the cache is a singleton so the retrieved snapshot
        // survives navigation between pages ("results cached in memory for the session").
        services.AddTransient<ITenantTopologyAssembler, TenantTopologyAssembler>();
        services.AddSingleton<ITenantTopologyCache, TenantTopologyCache>();

        // ViewModels (transient - new instance per navigation)
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<HealthCheckViewModel>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<GetStartedViewModel>();
        services.AddTransient<VariablesViewModel>();
        services.AddTransient<M365GroupsViewModel>();
        services.AddTransient<CallQueuesViewModel>();
        services.AddTransient<AutoAttendantsViewModel>();
        services.AddTransient<HolidaysViewModel>();
        services.AddTransient<DocumentationViewModel>();
        services.AddTransient<WizardViewModel>();
        services.AddTransient<BulkOperationsViewModel>();
        services.AddTransient<HistoryViewModel>();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
