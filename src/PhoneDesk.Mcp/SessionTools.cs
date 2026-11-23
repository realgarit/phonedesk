using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace PhoneDesk.Mcp;

[McpServerToolType]
internal sealed class SessionTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SessionOperations _operations;

    public SessionTools(SessionOperations operations)
    {
        _operations = operations;
    }

    [McpServerTool(Name = "session_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows whether the PowerShell modules were checked, whether Microsoft Teams and Microsoft Graph are connected, and which tenant is connected.")]
    public string SessionStatus() => ToJson(_operations.GetStatus());

    [McpServerTool(Name = "check_modules", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Checks that the bundled Microsoft Teams and Microsoft Graph PowerShell modules are available. Run this once before connecting.")]
    public async Task<string> CheckModules(CancellationToken cancellationToken) =>
        ToJson(await _operations.CheckModulesAsync(cancellationToken));

    [McpServerTool(Name = "connect_teams", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Signs in to Microsoft Teams. A browser window opens on this computer and the user must complete the sign-in there. Tell the user to watch for the browser window before you call this.")]
    public async Task<string> ConnectTeams(CancellationToken cancellationToken) =>
        ToJson(await _operations.ConnectTeamsAsync(cancellationToken));

    [McpServerTool(Name = "connect_graph", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Signs in to Microsoft Graph. A browser window opens on this computer and the user must complete the sign-in there, with an account from the same organization as the Teams sign-in.")]
    public async Task<string> ConnectGraph(CancellationToken cancellationToken) =>
        ToJson(await _operations.ConnectGraphAsync(cancellationToken));

    [McpServerTool(Name = "get_topology", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads the tenant's phone system: auto attendants, call queues, resource accounts, groups, and objects that are not linked to anything. Read-only. Requires both connections.")]
    public async Task<string> GetTopology(
        [Description("True to query the tenant again instead of using this session's cached result.")] bool refresh,
        CancellationToken cancellationToken) =>
        ToJson(await _operations.GetTopologyAsync(refresh, cancellationToken));

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
