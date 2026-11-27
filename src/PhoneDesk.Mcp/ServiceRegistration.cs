using Microsoft.Extensions.DependencyInjection;
using PhoneDesk.Operations;
using PhoneDesk.Planning;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services.ScriptBuilders;

namespace PhoneDesk.Mcp;

internal static class ServiceRegistration
{
    /// <summary>Registers the same Application and Infrastructure services the desktop app uses, without any UI service.</summary>
    public static IServiceCollection AddPhoneDeskMcp(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<McpEnvironment>();

        // Session-lifetime state.
        services.AddSingleton<ILoggingService, StderrLoggingService>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<IPowerShellContextService, PowerShellContextService>();
        services.AddSingleton<IMsalGraphAuthenticationService, MsalGraphAuthenticationService>();
        services.AddSingleton<IAuditLog, FileAuditLog>();
        services.AddSingleton<ITenantTopologyCache, TenantTopologyCache>();
        services.AddSingleton<ITenantHealthCheckCache, TenantHealthCheckCache>();
        services.AddSingleton<ITenantHealthPreferencesStore, TenantHealthPreferencesStore>();
        services.AddSingleton(ThrottleRetryOptions.Default);
        services.AddSingleton<IThrottleRetryPolicy, ThrottleRetryPolicy>();
        services.AddSingleton<ITenantCommandRunner, TenantCommandRunner>();

        // Script builders and pure services.
        services.AddTransient<IPowerShellSanitizationService, PowerShellSanitizationService>();
        services.AddTransient<IPowerShellCommandService, PowerShellCommandService>();
        services.AddTransient<IValidationService, ValidationService>();
        services.AddTransient<CommonScriptBuilder>();
        services.AddTransient<CallQueueScriptBuilder>();
        services.AddTransient<AutoAttendantScriptBuilder>();
        services.AddTransient<HolidayScriptBuilder>();
        services.AddTransient<ResourceAccountScriptBuilder>();
        services.AddTransient<DashboardScriptBuilder>();
        services.AddTransient<IDocumentationScriptBuilder, DocumentationScriptBuilder>();
        services.AddTransient<IDryRunPlanBuilder, DryRunPlanBuilder>();
        services.AddTransient<IDryRunPlanExporter, DryRunPlanExporter>();
        services.AddTransient<ITenantAsCodeService, TenantAsCodeService>();
        services.AddTransient<ITenantDocumentationSnapshotParser, TenantDocumentationSnapshotParser>();
        services.AddTransient<ITenantTopologyAssembler, TenantTopologyAssembler>();
        services.AddTransient<ITenantHealthEvaluationService, TenantHealthEvaluationService>();
        services.AddTransient<ITenantHealthEnrichmentParser, TenantHealthEnrichmentParser>();
        services.AddTransient<ITenantHealthQueryBuilder, TenantHealthQueryBuilder>();
        services.AddTransient<GuidedSetupScriptFactory>();
        services.AddTransient<BulkSetupScriptBuilder>();
        services.AddTransient<TenantSnapshotCollector>();

        // Headless operations and server state.
        // A restarted server works for the profile that was in use: its sign-ins are checked
        // against that customer from the first connect call on.
        services.AddSingleton(provider =>
        {
            var guard = new TenantIdentityGuard();
            if (provider.GetRequiredService<PhoneDesk.Operations.TenantProfileService>().Active is { } active)
            {
                guard.SetExpectation(active.ExpectedTenant, active.ExpectedAccount);
                // The sessions are named after the profiles, so the first one belongs to the profile in use.
                provider.GetRequiredService<IPowerShellContextService>().UseSession(active.Id);
                provider.GetRequiredService<IMsalGraphAuthenticationService>().UseProfile(active.Id);
            }

            return guard;
        });
        services.AddSingleton<TenantConnectionService>();
        services.AddSingleton<TenantHealthCheckService>();
        services.AddSingleton<ConfigurationStore>();
        services.AddSingleton<CreatedObjectLedger>();
        services.AddSingleton<GuidedSetupProgress>();
        services.AddSingleton<TenantGateway>();
        services.AddSingleton<IBundledModuleVersionService, BundledModuleVersionService>();
        services.AddSingleton<ChangeGate>();
        services.AddSingleton<CreatedObjectGuard>();
        services.AddSingleton<PhoneDesk.Operations.IRunJournalStore>(provider =>
            new FileRunJournalStore(Path.Combine(provider.GetRequiredService<McpEnvironment>().DataDirectory, "runs")));
        services.AddSingleton(provider => new PhoneDesk.Operations.RunJournalService(
            provider.GetRequiredService<PhoneDesk.Operations.IRunJournalStore>(),
            provider.GetRequiredService<IPowerShellCommandService>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<McpRunJournal>();
        services.AddSingleton<PhoneDesk.Operations.ITenantProfileStore>(provider =>
            new FileTenantProfileStore(Path.Combine(provider.GetRequiredService<McpEnvironment>().DataDirectory, "profiles.json")));
        services.AddSingleton(provider => new PhoneDesk.Operations.TenantProfileService(
            provider.GetRequiredService<PhoneDesk.Operations.ITenantProfileStore>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<HolidayCallHandlingReader>();
        services.AddSingleton<HolidayBackupWriter>();
        services.AddSingleton<AudioUploadLog>();

        return services;
    }
}
