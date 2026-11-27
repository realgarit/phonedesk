using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhoneDesk.Mcp;

// stdout carries the protocol. The transport opens the raw stdout stream itself, so sending
// Console.Out to stderr keeps stray console writes (for example from a PowerShell module)
// out of the protocol stream.
Console.SetOut(Console.Error);

// Same process-level switches as the desktop app: sign-in uses the system browser, not WAM.
Environment.SetEnvironmentVariable("MSAL_DISABLE_WAM", "true", EnvironmentVariableTarget.Process);
Environment.SetEnvironmentVariable("AZURE_IDENTITY_DISABLE_WAM", "true", EnvironmentVariableTarget.Process);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddPhoneDeskMcp();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "phonedesk", Version = PhoneDesk.Mcp.ServerVersion.Text };
        options.ServerInstructions = ServerInstructions.Text;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
