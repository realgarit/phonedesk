using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhoneDesk.Mcp;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services.ScriptBuilders;
using PhoneDesk.Topology;

// stdout carries the protocol. The transport opens the raw stdout stream itself, so sending
// Console.Out to stderr keeps stray console writes (for example from a PowerShell module)
// out of the protocol stream.
Console.SetOut(Console.Error);

// Same process-level switches as the desktop app: sign-in uses the system browser, not WAM.
Environment.SetEnvironmentVariable("MSAL_DISABLE_WAM", "true", EnvironmentVariableTarget.Process);
Environment.SetEnvironmentVariable("AZURE_IDENTITY_DISABLE_WAM", "true", EnvironmentVariableTarget.Process);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace);

var services = builder.Services;

services.AddSingleton<ILoggingService, StderrLoggingService>();
services.AddSingleton<ISessionManager, SessionManager>();
services.AddSingleton<IPowerShellContextService, PowerShellContextService>();
services.AddSingleton<IMsalGraphAuthenticationService, MsalGraphAuthenticationService>();
services.AddSingleton<ITenantTopologyCache, TenantTopologyCache>();

services.AddTransient<IPowerShellSanitizationService, PowerShellSanitizationService>();
services.AddTransient<IPowerShellCommandService, PowerShellCommandService>();
services.AddTransient<CommonScriptBuilder>();
services.AddTransient<CallQueueScriptBuilder>();
services.AddTransient<AutoAttendantScriptBuilder>();
services.AddTransient<HolidayScriptBuilder>();
services.AddTransient<ResourceAccountScriptBuilder>();
services.AddTransient<DashboardScriptBuilder>();
services.AddTransient<ITenantTopologyAssembler, TenantTopologyAssembler>();

services.AddSingleton<SessionOperations>();

services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
