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
        var identity = _connection.Identity;
        status["identity"] = System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            expectedTenant = identity.Expectation.Tenant,
            expectedAccount = identity.Expectation.Account,
            connectedAs = identity.Identity,
            asExpected = identity.Problems.Count == 0,
            problems = identity.Problems,
            teamsSignInOpen = _connection.TeamsSignInOpen,
            graphSignInOpen = _connection.GraphSignInOpen
        }, McpJson.Options);
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
    public Task<string> ConnectTeams(
        [Description("The tenant the user means: its tenant ID, one of its domains (contoso.com) or its exact name. When the sign-in reaches another tenant, the result says so and no change is sent. Ask the user for it when you work for several customers. Omit to keep the value of an earlier call; give an empty text to remove it.")] string? expectedTenant = null,
        [Description("The account the user means for the Microsoft Graph sign-in, as a user principal name. Omit to keep the value of an earlier call; give an empty text to remove it.")] string? expectedAccount = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            Expect(expectedTenant, expectedAccount);
            return WithIdentity(await _connection.ConnectTeamsAsync(cancellationToken));
        });

    [McpServerTool(Name = "connect_graph", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Signs in to Microsoft Graph. A browser window opens on this computer and the user must complete the sign-in there, with an account from the same organization as the Teams sign-in. Run it after connect_teams succeeded. The call returns after about 45 seconds: when the result has waitingForSignIn=true, nothing failed. Ask the user to finish the sign-in and call connect_graph again; it continues with the same window.")]
    public Task<string> ConnectGraph(
        [Description("The tenant the user means: its tenant ID, one of its domains or its exact name. Omit to keep the value of an earlier call; give an empty text to remove it.")] string? expectedTenant = null,
        [Description("The account the user means, as a user principal name. When another account signs in, the result says so and no change is sent. Omit to keep the value of an earlier call; give an empty text to remove it.")] string? expectedAccount = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            Expect(expectedTenant, expectedAccount);
            return WithIdentity(await _connection.ConnectGraphAsync(cancellationToken));
        });

    [McpServerTool(Name = "tenant_identity", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads from the tenant who the two connections are: the tenant, its domains and its name for Microsoft Teams, and the tenant and the account for Microsoft Graph. It reports a problem when the two are in different tenants, or when the tenant or the account is not the one that was expected at connect_teams or connect_graph. With a problem the server sends no change and no read to the tenant until the user signs out (disconnect) and signs in correctly. Use it after a customer switch, before the first change. Read-only.")]
    public Task<string> TenantIdentityOf(CancellationToken cancellationToken)
        => McpJson.RunAsync(async () =>
        {
            var read = await _connection.RefreshIdentityAsync(cancellationToken);
            var identity = _connection.Identity;
            return new
            {
                success = read is not null,
                connectedAs = identity.Identity,
                expectedTenant = identity.Expectation.Tenant,
                expectedAccount = identity.Expectation.Account,
                asExpected = read is not null && identity.Problems.Count == 0,
                problems = identity.Problems,
                note = read is null ? "The identity could not be read. Connect first, and finish a sign-in that is open." : null
            };
        });

    [McpServerTool(Name = "restart_sign_in", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Gives up a sign-in that hangs, so a new browser window can be opened. For 'graph': the sign-in that is open is dropped and the next connect_graph opens a new window; a sign-in that is finished later in the old window connects nothing; the Microsoft Teams connection stays. For 'teams': the PowerShell session that holds the sign-in is given up and a new one takes its place; both connections are lost, so call connect_teams and then connect_graph again (the Graph sign-in is usually silent). Use it when a connect call keeps reporting waitingForSignIn although the user closed the browser window. Nothing in the tenant changes.")]
    public string RestartSignIn(
        [Description("'graph' or 'teams'.")] string service)
        => McpJson.Run(() =>
        {
            var name = (service ?? string.Empty).Trim().ToLowerInvariant();
            if (name == "graph")
            {
                var dropped = _connection.AbandonGraphSignIn();
                return new
                {
                    success = true,
                    restarted = dropped,
                    message = dropped
                        ? "The open Microsoft Graph sign-in was dropped. Call connect_graph: a new browser window opens. Ask the user to ignore the old window."
                        : "No Microsoft Graph sign-in was open. Call connect_graph to sign in.",
                    teamsConnected = _connection.GetStatus().TeamsConnected
                } as object;
            }

            if (name == "teams")
            {
                var open = _connection.TeamsSignInOpen;
                var restarted = open && _connection.AbandonTeamsSignIn();
                return new
                {
                    success = restarted || !open,
                    restarted,
                    message = restarted
                        ? "The Microsoft Teams sign-in was given up with its PowerShell session, and a new session was started. Call connect_teams: a new browser window opens. Then call connect_graph again, because the Microsoft Graph connection of the old session is gone too. Ask the user to ignore the old window."
                        : open
                            ? "The Microsoft Teams sign-in could not be given up. Ask the user to finish it in the browser, or restart the MCP server."
                            : "No Microsoft Teams sign-in is open. Call connect_teams to sign in.",
                    teamsConnected = _connection.GetStatus().TeamsConnected,
                    graphConnected = _connection.GetStatus().GraphConnected
                };
            }

            throw new ArgumentException($"'{service}' is not a sign-in. Use 'graph' or 'teams'.");
        });

    /// <summary>An argument that is omitted keeps the expectation; an empty text removes it.</summary>
    private void Expect(string? expectedTenant, string? expectedAccount)
    {
        if (expectedTenant is null && expectedAccount is null)
        {
            return;
        }

        var current = _connection.Identity.Expectation;
        _connection.Identity.SetExpectation(expectedTenant ?? current.Tenant, expectedAccount ?? current.Account);
    }

    private object WithIdentity(ConnectionOutcome outcome)
    {
        var identity = _connection.Identity;
        return new
        {
            success = outcome.Success,
            message = outcome.Message,
            details = outcome.Details,
            waitingForSignIn = outcome.WaitingForSignIn,
            status = outcome.Status,
            identity = new
            {
                expectedTenant = identity.Expectation.Tenant,
                expectedAccount = identity.Expectation.Account,
                connectedAs = identity.Identity,
                asExpected = identity.Problems.Count == 0,
                problems = identity.Problems
            }
        };
    }

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
