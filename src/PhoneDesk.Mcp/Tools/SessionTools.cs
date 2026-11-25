using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;

namespace PhoneDesk.Mcp.Tools;

[McpServerToolType]
internal sealed class SessionTools
{
    private readonly TenantConnectionService _connection;
    private readonly TenantGateway _tenant;
    private readonly PhoneDesk.Services.Interfaces.IBundledModuleVersionService _moduleVersions;

    public SessionTools(
        TenantConnectionService connection,
        TenantGateway tenant,
        PhoneDesk.Services.Interfaces.IBundledModuleVersionService moduleVersions)
    {
        _connection = connection;
        _tenant = tenant;
        _moduleVersions = moduleVersions;
    }

    [McpServerTool(Name = "session_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows whether the PowerShell modules were checked, whether Microsoft Teams and Microsoft Graph are connected, which tenant is connected, and the versions of the server and of the bundled Microsoft Teams and Microsoft Graph PowerShell modules. Start here.")]
    public string SessionStatus() => McpJson.Run(() =>
    {
        // The status values stay at the top level; the versions are added beside them.
        var status = System.Text.Json.JsonSerializer.SerializeToNode(_connection.GetStatus(), McpJson.Options)!.AsObject();
        status["versions"] = System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            server = typeof(SessionTools).Assembly.GetName().Version?.ToString(3),
            microsoftTeamsModule = _moduleVersions.TeamsModuleVersion,
            microsoftGraphModules = _moduleVersions.GraphModuleVersion,
            powerShellSdk = _moduleVersions.PowerShellSdkVersion
        }, McpJson.Options);
        return status;
    });

    [McpServerTool(Name = "check_modules", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Checks that the bundled Microsoft Teams and Microsoft Graph PowerShell modules are available. Run this once before connecting.")]
    public Task<string> CheckModules(CancellationToken cancellationToken)
        => McpJson.RunAsync(async () => await _connection.CheckModulesAsync(cancellationToken));

    [McpServerTool(Name = "connect_teams", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Signs in to Microsoft Teams. A browser window opens on this computer and the user must complete the sign-in there. Tell the user to watch for the browser window before you call this. The call returns after about 45 seconds: when the result has waitingForSignIn=true, nothing failed. Ask the user to finish the sign-in and call connect_teams again; it continues with the same window.")]
    public Task<string> ConnectTeams(CancellationToken cancellationToken)
        => McpJson.RunAsync(async () => await _connection.ConnectTeamsAsync(cancellationToken));

    [McpServerTool(Name = "connect_graph", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Signs in to Microsoft Graph. A browser window opens on this computer and the user must complete the sign-in there, with an account from the same organization as the Teams sign-in. Run it after connect_teams succeeded. The call returns after about 45 seconds: when the result has waitingForSignIn=true, nothing failed. Ask the user to finish the sign-in and call connect_graph again; it continues with the same window.")]
    public Task<string> ConnectGraph(CancellationToken cancellationToken)
        => McpJson.RunAsync(async () => await _connection.ConnectGraphAsync(cancellationToken));

    [McpServerTool(Name = "disconnect", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Signs out of Microsoft Teams and Microsoft Graph and forgets the cached tenant data of this session. Nothing in the tenant changes.")]
    public Task<string> Disconnect(CancellationToken cancellationToken)
        => McpJson.RunAsync(async () =>
        {
            var outcome = await _connection.DisconnectAsync(cancellationToken);
            _tenant.ForgetReads();
            return outcome;
        });
}
