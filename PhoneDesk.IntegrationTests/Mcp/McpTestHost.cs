using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PhoneDesk.Mcp;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// The MCP server's real service graph with the two tenant-facing services replaced: PowerShell
/// returns scripted output and Graph sign-in returns a fixed account. Files go to a temporary folder.
/// </summary>
internal sealed class McpTestHost : IDisposable
{
    public const string TenantId = "tenant-1";

    public const string TopologyOutput =
        "TOPRA: RA CQ One|racq-one@contoso.com|ra-obj-1|+41 21 000|CallQueue|True\n" +
        "TOPRA: RA AA One|raaa-one@contoso.com|ra-obj-2|+41 21 111|AutoAttendant|True\n" +
        "TOPAA: Main AA|aa-id-1|en-US|W. Europe Standard Time|ra-obj-2|sched-1|ra-obj-1\n" +
        "TOPCQ: Support CQ|cq-id-1|Attendant|30|agent-1|grp-1|ra-obj-1\n" +
        "TOPGRP: Support Group|grp-1|support|Support team\n" +
        "SUCCESS: Tenant topology retrieved\n";

    private readonly ServiceProvider _services;

    public McpTestHost(bool skipBackup = true, bool allowDeleteExisting = false, string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "phonedesk-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPhoneDeskMcp();
        Replace<McpEnvironment>(services, new McpEnvironment(DataDirectory, skipBackup, allowDeleteExisting));
        Replace<IPowerShellContextService>(services, PowerShell);
        Replace<IMsalGraphAuthenticationService>(services, GraphAuthentication);
        Replace<IAuditLog>(services, new FileAuditLog(Path.Combine(DataDirectory, "audit")));
        Replace<ITenantHealthPreferencesStore>(services, new TenantHealthPreferencesStore(DataDirectory));
        Replace<ILoggingService>(services, new TestLoggingService());
        _services = services.BuildServiceProvider();

        var commands = Get<IPowerShellCommandService>();
        var documentation = Get<IDocumentationScriptBuilder>();
        PowerShell.Respond(commands.GetRetrieveTenantTopologyCommand(), TopologyOutput);
        PowerShell.Respond(documentation.GetExportTenantInfoCommand(), "DOCDATA_TENANT:Contoso|tenant-1|CH|de-DE");
        PowerShell.Respond(documentation.GetExportResourceAccountsCommand(), "DOCDATA_RA_START\nDOCDATA_RA_END\nDOCDATA_ASSOC_START\nDOCDATA_ASSOC_END");
        PowerShell.Respond(documentation.GetExportAutoAttendantsCommand(), "DOCDATA_AA_START\nDOCDATA_AA_END");
        PowerShell.Respond(documentation.GetExportCallQueuesCommand(), "DOCDATA_CQ_START\nDOCDATA_CQ_END");
        PowerShell.Respond(documentation.GetExportSchedulesCommand(), "DOCDATA_SCHED_START\nDOCDATA_SCHED_END");
        PowerShell.Respond(documentation.GetExportPhoneNumbersCommand(), "DOCDATA_PHONE_START\nDOCDATA_PHONE_END");
        PowerShell.Respond(documentation.GetExportVoiceUsersCommand(), "DOCDATA_USER_START\nDOCDATA_USER_END");
        PowerShell.Respond(
            Get<ITenantHealthQueryBuilder>().GetTenantHealthEnrichmentCommand(),
            "HEALTH_RA_START\nHEALTH_RA: ra-obj-1|False|\nHEALTH_RA: ra-obj-2|True|CH\nHEALTH_RA_END\n" +
            "HEALTH_AA_START\nHEALTH_AA: aa-id-1|False\nHEALTH_AA_END\nHEALTH_PHONE_START\nHEALTH_PHONE_END\n" +
            "SUCCESS: Tenant health enrichment retrieved");
    }

    public string DataDirectory { get; }

    public FakePowerShell PowerShell { get; } = new();

    public FakeGraphAuthentication GraphAuthentication { get; } = new();

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public T Tool<T>() => ActivatorUtilities.CreateInstance<T>(_services);

    /// <summary>Marks the session as connected to the test tenant, as after a successful sign-in.</summary>
    public McpTestHost Connected()
    {
        var session = Get<ISessionManager>();
        session.UpdateModulesChecked(true);
        session.UpdateTeamsConnection(true);
        session.UpdateTenantInfo(TenantId, "Contoso");
        session.UpdateGraphConnection(true, "admin@contoso.com");
        return this;
    }

    /// <summary>Fills the configuration with a complete, valid test customer.</summary>
    public McpTestHost Configured()
    {
        Get<ConfigurationStore>().Patch(new Dictionary<string, JsonElement>
        {
            ["general.customer"] = Json("contoso"),
            ["general.customerGroupName"] = Json("reception"),
            ["general.msFallbackDomain"] = Json("contoso.onmicrosoft.com"),
            ["general.raaAnrName"] = Json("main"),
            ["general.customerLegalName"] = Json("Contoso AG"),
            ["general.languageId"] = Json("de-DE"),
            ["general.timeZoneId"] = Json("W. Europe Standard Time"),
            ["general.usageLocation"] = Json("CH"),
            ["general.raaAnr"] = Json("+41441234567"),
            ["general.phoneNumberType"] = Json("DirectRouting"),
            ["holidayTemplate.nameSuffix"] = Json("2027"),
            ["holidayTemplate.greetingPromptDe"] = Json("Wir sind heute nicht erreichbar.")
        });
        return this;
    }

    public static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Parse(string toolResult) => JsonDocument.Parse(toolResult).RootElement;

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary folder does not fail a test.
        }
    }

    private static void Replace<T>(IServiceCollection services, T instance) where T : class
    {
        foreach (var descriptor in services.Where(item => item.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }

        services.AddSingleton(instance);
    }

    internal sealed class FakeGraphAuthentication : IMsalGraphAuthenticationService
    {
        private TaskCompletionSource<(bool, string?, string?, string?)>? _held;

        public int Calls { get; private set; }

        /// <summary>Makes the next sign-in wait until the returned source is completed.</summary>
        public TaskCompletionSource<(bool, string?, string?, string?)> Hold()
            => _held = new TaskCompletionSource<(bool, string?, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(bool Success, string? AccessToken, string? Account, string? ErrorMessage)> AuthenticateAsync(
            IntPtr? parentWindowHandle = null)
        {
            Calls++;
            var held = _held;
            _held = null;
            return held is not null
                ? held.Task
                : Task.FromResult<(bool, string?, string?, string?)>((true, "test-token", "admin@contoso.com", null));
        }

        public Task SignOutAsync() => Task.CompletedTask;

        public Task<bool> HasCachedAccountAsync() => Task.FromResult(false);
    }
}

/// <summary>Scripted PowerShell: returns the output registered for a script, and records every script it ran.</summary>
internal sealed class FakePowerShell : IPowerShellContextService
{
    private readonly Dictionary<string, PowerShellExecutionResult> _responses = new(StringComparer.Ordinal);

    public List<string> Executed { get; } = new();

    public PowerShellExecutionResult DefaultResult { get; set; } = new() { Output = "SUCCESS: done" };

    /// <summary>Runs after a script, for example to change what the tenant returns from then on.</summary>
    public Action<string>? AfterRun { get; set; }

    public void Respond(string script, string output, bool hadErrors = false)
        => _responses[script] = new PowerShellExecutionResult
        {
            Output = output,
            HadErrors = hadErrors,
            Errors = hadErrors
                ? new[] { new PowerShellErrorInfo { Message = output } }
                : Array.Empty<PowerShellErrorInfo>()
        };

    public Task<string> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
        => Task.FromResult(Run(command).Output);

    public Task<string> ExecuteCommandAsync(
        string command, Dictionary<string, string>? environmentVariables, CancellationToken cancellationToken = default)
        => Task.FromResult(Run(command).Output);

    private readonly Dictionary<string, TaskCompletionSource> _held = new(StringComparer.Ordinal);

    /// <summary>Makes a script wait, like a sign-in the user has not finished. Complete the returned source to let it finish.</summary>
    public TaskCompletionSource Hold(string script)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _held[script] = source;
        return source;
    }

    public async Task<PowerShellExecutionResult> ExecuteCommandWithDetailsAsync(
        string command,
        Dictionary<string, string>? environmentVariables,
        IProgress<PowerShellProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_held.Remove(command, out var held))
        {
            await held.Task.WaitAsync(cancellationToken);
        }

        return Run(command);
    }

    public Task<bool> IsConnectedAsync(string service, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<string> GetConnectionStatusAsync(CancellationToken cancellationToken = default)
        => Task.FromResult("Connected");

    public void Dispose()
    {
    }

    private PowerShellExecutionResult Run(string command)
    {
        Executed.Add(command);
        var answer = _responses.TryGetValue(command, out var result) ? result : DefaultResult;
        AfterRun?.Invoke(command);
        return answer;
    }
}
