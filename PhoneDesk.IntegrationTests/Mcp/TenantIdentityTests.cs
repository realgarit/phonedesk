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
    public void RestartSignInTool_SaysThatATeamsSignInCannotBeGivenUp()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<SessionTools>();

        var teams = McpTestHost.Parse(tools.RestartSignIn("teams"));
        Assert.False(teams.GetProperty("success").GetBoolean());
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
