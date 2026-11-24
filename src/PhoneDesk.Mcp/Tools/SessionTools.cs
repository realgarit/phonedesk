using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;

namespace PhoneDesk.Mcp.Tools;

[McpServerToolType]
internal sealed class SessionTools
{
    private readonly TenantConnectionService _connection;

    public SessionTools(TenantConnectionService connection)
    {
        _connection = connection;
    }

    [McpServerTool(Name = "session_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows whether the PowerShell modules were checked, whether Microsoft Teams and Microsoft Graph are connected, and which tenant is connected. Start here.")]
    public string SessionStatus() => McpJson.Run(() => _connection.GetStatus());

    [McpServerTool(Name = "check_modules", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Checks that the bundled Microsoft Teams and Microsoft Graph PowerShell modules are available. Run this once before connecting.")]
    public Task<string> CheckModules(CancellationToken cancellationToken)
        => McpJson.RunAsync(async () => await _connection.CheckModulesAsync(cancellationToken));

    [McpServerTool(Name = "connect_teams", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Signs in to Microsoft Teams. A browser window opens on this computer and the user must complete the sign-in there. Tell the user to watch for the browser window before you call this.")]
    public Task<string> ConnectTeams(CancellationToken cancellationToken)
        => McpJson.RunAsync(async () => await _connection.ConnectTeamsAsync(cancellationToken));

    [McpServerTool(Name = "connect_graph", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Signs in to Microsoft Graph. A browser window opens on this computer and the user must complete the sign-in there, with an account from the same organization as the Teams sign-in.")]
    public Task<string> ConnectGraph(CancellationToken cancellationToken)
        => McpJson.RunAsync(async () => await _connection.ConnectGraphAsync(cancellationToken));

    [McpServerTool(Name = "disconnect", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Signs out of Microsoft Teams and Microsoft Graph and forgets the cached tenant data of this session. Nothing in the tenant changes.")]
    public Task<string> Disconnect(CancellationToken cancellationToken)
        => McpJson.RunAsync(async () => await _connection.DisconnectAsync(cancellationToken));
}
