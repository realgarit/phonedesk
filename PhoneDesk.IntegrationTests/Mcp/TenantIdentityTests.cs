using System.Text.Json;
using PhoneDesk.Mcp;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// After a sign-in that reported success, the connections can still be wrong: another tenant than
/// the operator meant, or Microsoft Teams and Microsoft Graph in different tenants. The identity is
/// read from the tenant and checked, and nothing is sent while it is wrong.
/// </summary>
public sealed class TenantIdentityTests
{
    private const string Contoso = "00000000-0000-0000-0000-0000000000c0";
    private const string Fabrikam = "00000000-0000-0000-0000-0000000000fa";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void ReadScript_ReturnsTheTenantOfEachConnectionAndLeavesAMissingOneEmpty()
    {
        var both = TenantIdentity.Parse(McpHolidayCallHandlingTests.RunPowerShell(
            """
            function global:Get-CsTenant { [CmdletBinding()] param() [pscustomobject]@{ TenantId = '00000000-0000-0000-0000-0000000000c0'; DisplayName = 'Contoso AG'; VerifiedDomains = @([pscustomobject]@{ Name = 'contoso.com' }, [pscustomobject]@{ Name = 'contoso.onmicrosoft.com' }) } }
            function global:Get-MgContext { [CmdletBinding()] param() [pscustomobject]@{ TenantId = '00000000-0000-0000-0000-0000000000c0'; Account = 'admin@contoso.com' } }
            """,
            TenantIdentity.GetReadCommand()));
        Assert.Equal("Contoso AG", both.TeamsTenantName);
        Assert.Equal(new[] { "contoso.com", "contoso.onmicrosoft.com" }, both.TeamsDomains);
        Assert.Equal("admin@contoso.com", both.GraphAccount);
        Assert.Empty(TenantIdentity.Check(both, TenantExpectation.None));

        // Only Microsoft Teams is connected: the Graph part stays empty and is not a problem.
        var teamsOnly = TenantIdentity.Parse(McpHolidayCallHandlingTests.RunPowerShell(
            """
            function global:Get-CsTenant { [CmdletBinding()] param() [pscustomobject]@{ TenantId = '00000000-0000-0000-0000-0000000000c0'; DisplayName = 'Contoso AG'; VerifiedDomains = @() } }
            function global:Get-MgContext { [CmdletBinding()] param() throw 'Not connected' }
            """,
            TenantIdentity.GetReadCommand()));
        Assert.True(teamsOnly.TeamsKnown);
        Assert.False(teamsOnly.GraphKnown);
        Assert.Empty(TenantIdentity.Check(teamsOnly, TenantExpectation.None));
    }

    [Theory]
    [InlineData("contoso.com", null, null)]
    [InlineData("CONTOSO.onmicrosoft.com", null, null)]
    [InlineData("Contoso AG", null, null)]
    [InlineData(Contoso, "Admin@Contoso.com", null)]
    [InlineData("fabrikam.com", null, "unexpectedTenant")]
    [InlineData(Fabrikam, null, "unexpectedTenant")]
    [InlineData(null, "other@contoso.com", "unexpectedAccount")]
    public void Check_AcceptsTheTenantByIdDomainOrNameAndRefusesAnotherOne(string? tenant, string? account, string? problem)
    {
        var identity = new TenantIdentityInfo
        {
            TeamsTenantId = Contoso,
            TeamsTenantName = "Contoso AG",
            TeamsDomains = new[] { "contoso.com", "contoso.onmicrosoft.com" },
            GraphTenantId = Contoso,
            GraphAccount = "admin@contoso.com"
        };

        var problems = TenantIdentity.Check(identity, new TenantExpectation(tenant, account));

        Assert.Equal(problem, problems.SingleOrDefault()?.Code);
    }

    [Fact]
    public void Check_FindsTeamsAndGraphInDifferentTenants()
    {
        // The browser kept the session of the previous customer for one of the two sign-ins.
        var identity = new TenantIdentityInfo
        {
            TeamsTenantId = Contoso,
            TeamsTenantName = "Contoso AG",
            GraphTenantId = Fabrikam,
            GraphAccount = "admin@fabrikam.com"
        };

        var problem = Assert.Single(TenantIdentity.Check(identity, TenantExpectation.None));

        Assert.Equal("tenantsDiffer", problem.Code);
        Assert.Contains("admin@fabrikam.com", problem.Message);
    }

    [Fact]
    public async Task Server_SendsNothingWhileTheConnectionsAreNotTheExpectedOnes()
    {
        using var host = new McpTestHost().Connected().Configured();
        host.PowerShell.Respond(TenantIdentity.GetReadCommand(), Identity(Contoso, "Contoso AG", Contoso, "admin@contoso.com"));
        var session = host.Tool<SessionTools>();

        // The operator means Fabrikam, but the sign-ins reached Contoso.
        host.Get<TenantConnectionService>().Identity.SetExpectation("fabrikam.com", null);
        var identity = McpTestHost.Parse(await session.TenantIdentityOf(Cancellation));
        Assert.False(identity.GetProperty("asExpected").GetBoolean());
        Assert.Equal("unexpectedTenant", identity.GetProperty("problems")[0].GetProperty("code").GetString());
        Assert.Equal("Contoso AG", identity.GetProperty("connectedAs").GetProperty("teamsTenantName").GetString());

        var executedBefore = host.PowerShell.Executed.Count;
        var change = McpTestHost.Parse(await host.Tool<ProvisioningTools>().CreateM365Group(cancellationToken: Cancellation));
        var planned = change.GetProperty("planId").GetString();
        var refused = McpTestHost.Parse(await host.Tool<ProvisioningTools>().CreateM365Group(planId: planned, cancellationToken: Cancellation));
        Assert.False(refused.GetProperty("success").GetBoolean());
        Assert.Contains("Nothing is sent to the tenant", refused.GetProperty("error").GetString());
        var read = McpTestHost.Parse(await host.Tool<TenantReadTools>().ListAutoAttendants(cancellationToken: Cancellation));
        Assert.Contains("Nothing is sent to the tenant", read.GetProperty("error").GetString());
        Assert.Equal(executedBefore, host.PowerShell.Executed.Count);

        // With the right expectation the same connections are fine.
        host.Get<TenantConnectionService>().Identity.SetExpectation("contoso ag", "admin@contoso.com");
        var status = McpTestHost.Parse(session.SessionStatus());
        Assert.True(status.GetProperty("identity").GetProperty("asExpected").GetBoolean());
    }

    [Fact]
    public async Task Connect_ReportsAnUnexpectedTenantAsAFailureAlthoughTheSignInWorked()
    {
        using var host = new McpTestHost();
        var commands = host.Get<IPowerShellCommandService>();
        host.PowerShell.Respond(commands.GetConnectTeamsCommand(), "Connected to tenant: Contoso AG (" + Contoso + ")\nSUCCESS: Connected to Microsoft Teams");
        host.PowerShell.Respond(TenantIdentity.GetReadCommand(), Identity(Contoso, "Contoso AG", string.Empty, string.Empty));
        var session = host.Tool<SessionTools>();
        host.Get<ISessionManager>().UpdateModulesChecked(true);

        var connected = McpTestHost.Parse(await session.ConnectTeams(expectedTenant: "fabrikam.com", cancellationToken: Cancellation));

        Assert.False(connected.GetProperty("success").GetBoolean());
        Assert.Contains("Expected the tenant 'fabrikam.com'", connected.GetProperty("details").GetString());
        Assert.True(connected.GetProperty("status").GetProperty("teamsConnected").GetBoolean());
        Assert.False(connected.GetProperty("identity").GetProperty("asExpected").GetBoolean());

        // The expectation stays for the next call; an empty text removes it.
        var cleared = McpTestHost.Parse(await session.ConnectTeams(expectedTenant: "", cancellationToken: Cancellation));
        Assert.True(cleared.GetProperty("identity").GetProperty("asExpected").GetBoolean());
    }

    [Fact]
    public async Task RestartSignIn_DropsAGraphSignInThatHangsAndKeepsTheTeamsConnection()
    {
        using var host = new McpTestHost();
        var session = host.Get<ISessionManager>();
        session.UpdateModulesChecked(true);
        session.UpdateTeamsConnection(true);
        session.UpdateTenantInfo(Contoso, "Contoso AG");
        host.PowerShell.Respond(TenantIdentity.GetReadCommand(), Identity(Contoso, "Contoso AG", Contoso, "admin@contoso.com"));
        var connection = new TenantConnectionService(
            host.Get<ITenantCommandRunner>(),
            host.Get<IPowerShellCommandService>(),
            host.GraphAuthentication,
            session,
            host.Get<ITenantTopologyCache>(),
            host.Get<ITenantHealthCheckCache>(),
            host.Get<TenantIdentityGuard>())
        {
            SignInWait = TimeSpan.FromMilliseconds(50)
        };

        // The user closed the browser window: the sign-in never reports back.
        var hanging = host.GraphAuthentication.Hold();
        var waiting = await connection.ConnectGraphAsync(Cancellation);
        Assert.True(waiting.WaitingForSignIn);
        Assert.True(connection.GraphSignInOpen);

        Assert.True(connection.AbandonGraphSignIn());
        Assert.False(connection.GraphSignInOpen);
        Assert.False(connection.AbandonGraphSignIn());
        Assert.True(connection.GetStatus().TeamsConnected);

        // The next call opens a new sign-in, and that one connects.
        var connected = await connection.ConnectGraphAsync(Cancellation);
        Assert.True(connected.Success, connected.Message + connected.Details);
        Assert.Equal(2, host.GraphAuthentication.Calls);

        // The old window is finished later with another account: nothing changes.
        hanging.SetResult((true, "old-token", "someone@fabrikam.com", null));
        await Task.Delay(50, Cancellation);
        Assert.Equal("admin@contoso.com", connection.GetStatus().GraphAccount);
    }

    [Fact]
    public async Task RestartSignIn_WhileAConnectCallWaits_TheOldWindowConnectsNothing()
    {
        using var host = new McpTestHost();
        var session = host.Get<ISessionManager>();
        session.UpdateModulesChecked(true);
        var connection = new TenantConnectionService(
            host.Get<ITenantCommandRunner>(),
            host.Get<IPowerShellCommandService>(),
            host.GraphAuthentication,
            session,
            host.Get<ITenantTopologyCache>(),
            host.Get<ITenantHealthCheckCache>(),
            host.Get<TenantIdentityGuard>())
        {
            SignInWait = TimeSpan.FromSeconds(30)
        };

        // A connect call is still waiting when the sign-in is given up.
        var hanging = host.GraphAuthentication.Hold();
        var waitingCall = connection.ConnectGraphAsync(Cancellation);
        Assert.True(connection.GraphSignInOpen);
        Assert.True(connection.AbandonGraphSignIn());

        // The user finishes the old window after all: the waiting call must not connect with it.
        hanging.SetResult((true, "old-token", "someone@fabrikam.com", null));
        var outcome = await waitingCall;

        Assert.False(outcome.Success);
        Assert.Contains("was given up", outcome.Message);
        Assert.False(connection.GetStatus().GraphConnected);
        Assert.DoesNotContain(host.PowerShell.Executed, script => script.Contains("Connect-MgGraph", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TeamsSignIn_IsGivenUpWithItsSessionAndANewOneCanStart()
    {
        using var host = new McpTestHost();
        var commands = host.Get<IPowerShellCommandService>();
        var session = host.Get<ISessionManager>();
        session.UpdateModulesChecked(true);
        var connection = new TenantConnectionService(
            host.Get<ITenantCommandRunner>(), commands, host.GraphAuthentication, session,
            host.Get<ITenantTopologyCache>(), host.Get<ITenantHealthCheckCache>(), host.Get<TenantIdentityGuard>(), host.PowerShell)
        {
            SignInWait = TimeSpan.FromMilliseconds(50)
        };

        // The user closed the browser window: the Teams sign-in never reports back and holds its session.
        var hanging = host.PowerShell.Hold(commands.GetConnectTeamsCommand());
        Assert.True((await connection.ConnectTeamsAsync(Cancellation)).WaitingForSignIn);
        Assert.True((await connection.CheckModulesAsync(Cancellation)).WaitingForSignIn);

        Assert.True(connection.AbandonTeamsSignIn());
        Assert.Equal(1, host.PowerShell.SessionsAbandoned);
        Assert.False(connection.TeamsSignInOpen);
        Assert.False(connection.GetStatus().TeamsConnected);
        Assert.False(connection.GetStatus().GraphConnected);
        Assert.True(connection.GetStatus().ModulesChecked);

        // A connect call that was still waiting when the sign-in was given up does not take its result.
        var commandsHeld = host.PowerShell.Hold(commands.GetConnectTeamsCommand());
        var slowConnection = new TenantConnectionService(
            host.Get<ITenantCommandRunner>(), commands, host.GraphAuthentication, session,
            host.Get<ITenantTopologyCache>(), host.Get<ITenantHealthCheckCache>(), host.Get<TenantIdentityGuard>(), host.PowerShell)
        {
            SignInWait = TimeSpan.FromSeconds(30)
        };
        host.PowerShell.Respond(commands.GetConnectTeamsCommand(), "Connected to tenant: Fabrikam (" + Fabrikam + ")" + Environment.NewLine + "SUCCESS: Connected to Microsoft Teams");
        var waitingCall = slowConnection.ConnectTeamsAsync(Cancellation);
        Assert.True(slowConnection.TeamsSignInOpen);
        Assert.True(slowConnection.AbandonTeamsSignIn());
        commandsHeld.SetResult();
        var late = await waitingCall;
        Assert.False(late.Success);
        Assert.Contains("was given up", late.Message);
        Assert.False(session.TeamsConnected);

        // A new sign-in starts at once, and the old window that finishes later connects nothing.
        host.PowerShell.Respond(commands.GetConnectTeamsCommand(), "Connected to tenant: Contoso AG (" + Contoso + ")\nSUCCESS: Connected to Microsoft Teams");
        var fresh = await connection.ConnectTeamsAsync(Cancellation);
        Assert.True(fresh.Success, fresh.Message + fresh.Details);
        hanging.SetResult();
        Assert.False(connection.AbandonTeamsSignIn());
    }

    [Fact]
    public void RestartSignInTool_GivesUpWhatIsOpenAndSaysWhenNothingIs()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<SessionTools>();

        var teams = McpTestHost.Parse(tools.RestartSignIn("teams"));
        Assert.True(teams.GetProperty("success").GetBoolean());
        Assert.False(teams.GetProperty("restarted").GetBoolean());
        Assert.Contains("No Microsoft Teams sign-in is open", teams.GetProperty("message").GetString());
        var graph = McpTestHost.Parse(tools.RestartSignIn("Graph"));
        Assert.True(graph.GetProperty("success").GetBoolean());
        Assert.False(graph.GetProperty("restarted").GetBoolean());
        Assert.Contains("is not a sign-in", McpTestHost.Parse(tools.RestartSignIn("both")).GetProperty("error").GetString());
    }

    private static string Identity(string teamsId, string teamsName, string graphId, string graphAccount)
        => TenantIdentity.JsonMarker + JsonSerializer.Serialize(new
        {
            teamsTenantId = teamsId,
            teamsTenantName = teamsName,
            teamsDomains = new[] { "contoso.com", "contoso.onmicrosoft.com" },
            graphTenantId = graphId,
            graphAccount
        }) + "\nSUCCESS: Connection identity retrieved";
}
