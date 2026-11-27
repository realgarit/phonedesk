using System.Text.Json;
using PhoneDesk.Mcp;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>Customer profiles through the MCP tools: a switch swaps the configuration, sets the expected tenant and signs out.</summary>
public sealed class ProfileToolTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Switch_SwapsTheConfigurationSetsTheExpectedTenantAndSignsOut()
    {
        using var host = new McpTestHost().Connected().Configured();
        var tools = host.Tool<ProfileTools>();
        var store = host.Get<ConfigurationStore>();

        // The configuration of the first customer becomes a profile.
        var created = McpTestHost.Parse(tools.Save("Contoso", "contoso.com", "admin@contoso.com"));
        Assert.True(created.GetProperty("created").GetBoolean());
        Assert.False(created.GetProperty("profile").GetProperty("inUse").GetBoolean());
        await tools.Switch("Contoso", Cancellation);

        // The second customer: another configuration, saved as its own profile.
        store.Patch(new Dictionary<string, JsonElement> { ["general.customer"] = McpTestHost.Json("fabrikam") });
        McpTestHost.Parse(tools.Save("Fabrikam", "fabrikam.com"));
        // The same name again updates that profile; it does not make a second one.
        var updated = McpTestHost.Parse(tools.Save("Fabrikam", "other.com"));
        Assert.False(updated.GetProperty("created").GetBoolean());
        Assert.Equal("other.com", updated.GetProperty("profile").GetProperty("expectedTenant").GetString());

        // Back on Contoso the edit above belongs to Contoso; the switch to Fabrikam keeps it there.
        host.Connected();
        var switched = McpTestHost.Parse(await tools.Switch("Fabrikam", Cancellation));
        Assert.Equal("Fabrikam", switched.GetProperty("activeProfile").GetProperty("name").GetString());
        Assert.Equal("fabrikam", store.Variables.Customer);
        Assert.Equal("other.com", host.Get<TenantConnectionService>().Identity.Expectation.Tenant);
        // The profile has its own session: the connection of the customer before is not used for this one.
        var fabrikamId = host.Get<TenantProfileService>().Find("Fabrikam")!.Id;
        Assert.Equal(fabrikamId, host.PowerShell.SessionKey);
        Assert.False(switched.GetProperty("teamsConnected").GetBoolean());
        Assert.False(host.Get<TenantConnectionService>().IsReady);
        Assert.Contains("fabrikam", host.Get<TenantProfileService>().Find("Contoso")!.ConfigurationJson);

        var listed = McpTestHost.Parse(tools.List());
        Assert.Equal("Fabrikam", listed.GetProperty("activeProfile").GetString());
        Assert.Equal(2, listed.GetProperty("profiles").GetArrayLength());

        Assert.Contains("no profile", McpTestHost.Parse(await tools.Switch("Nobody", Cancellation)).GetProperty("error").GetString());
        // The profile in use is not removed.
        Assert.Contains("in use", McpTestHost.Parse(tools.Delete("Fabrikam")).GetProperty("error").GetString());
        Assert.Equal("Contoso", McpTestHost.Parse(tools.Delete("Contoso")).GetProperty("deleted").GetString());
        Assert.Equal(1, McpTestHost.Parse(tools.List()).GetProperty("profiles").GetArrayLength());
    }

    [Fact]
    public async Task ARestartedServerChecksTheSignInsAgainstTheProfileInUse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "phonedesk-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var host = new McpTestHost(dataDirectory: directory, keepDataDirectory: true).Connected().Configured())
            {
                var tools = host.Tool<ProfileTools>();
                tools.Save("Contoso", "contoso.com", "admin@contoso.com");
                await tools.Switch("Contoso", Cancellation);

                // Switching to the profile in use keeps the edits made since: nothing older is loaded.
                host.Get<ConfigurationStore>().Patch(new Dictionary<string, JsonElement> { ["general.customerGroupName"] = McpTestHost.Json("hotline") });
                host.Connected();
                var again = McpTestHost.Parse(await tools.Switch("Contoso", Cancellation));
                Assert.True(again.GetProperty("alreadyInUse").GetBoolean());
                Assert.False(again.GetProperty("signedOut").GetBoolean());
                Assert.Equal("hotline", host.Get<ConfigurationStore>().Variables.CustomerGroupName);
                Assert.True(host.Get<TenantConnectionService>().IsReady);
                Assert.Contains("hotline", host.Get<TenantProfileService>().Find("Contoso")!.ConfigurationJson);
            }

            // A new server on the same data: the expected tenant is in place before the first sign-in.
            using var restarted = new McpTestHost(dataDirectory: directory, keepDataDirectory: true);
            var expectation = restarted.Get<TenantConnectionService>().Identity.Expectation;
            Assert.Equal("contoso.com", expectation.Tenant);
            Assert.Equal("admin@contoso.com", expectation.Account);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SwitchingBack_FindsTheConnectionsTheProfileStillHas()
    {
        using var host = new McpTestHost().Connected().Configured();
        var tools = host.Tool<ProfileTools>();
        tools.Save("Contoso", "contoso.com");
        tools.Save("Fabrikam", "fabrikam.com");
        // The session of a profile answers the identity read with what it is connected to.
        host.PowerShell.Respond(TenantIdentity.GetReadCommand(), TenantIdentity.JsonMarker + JsonSerializer.Serialize(new
        {
            teamsTenantId = McpTestHost.TenantId,
            teamsTenantName = "Contoso AG",
            teamsDomains = new[] { "contoso.com" },
            graphTenantId = McpTestHost.TenantId,
            graphAccount = "admin@contoso.com"
        }));

        var back = McpTestHost.Parse(await tools.Switch("Contoso", Cancellation));

        Assert.True(back.GetProperty("teamsConnected").GetBoolean());
        Assert.True(back.GetProperty("graphConnected").GetBoolean());
        Assert.Equal("Contoso AG", back.GetProperty("tenant").GetString());
        Assert.True(host.Get<TenantConnectionService>().IsReady);
        Assert.Empty(host.Get<TenantConnectionService>().Identity.Problems);
        Assert.False(back.TryGetProperty("identityProblems", out _));
    }

    [Fact]
    public async Task APlanNamesTheTenantItIsFor()
    {
        using var host = new McpTestHost().Connected().Configured();
        host.Get<ISessionManager>().UpdateTenantInfo(McpTestHost.TenantId, "Contoso AG");

        var plan = McpTestHost.Parse(await host.Tool<GuidedSetupTools>().RunStep(1, cancellationToken: Cancellation));

        Assert.Equal($"Contoso AG ({McpTestHost.TenantId})", plan.GetProperty("tenant").GetString());
    }
}
