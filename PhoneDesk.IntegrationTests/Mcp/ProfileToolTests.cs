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
        Assert.True(switched.GetProperty("signedOut").GetBoolean());
        Assert.Equal("fabrikam", store.Variables.Customer);
        Assert.Equal("other.com", host.Get<TenantConnectionService>().Identity.Expectation.Tenant);
        // Signed out: the connection of the customer before is not used for this one.
        Assert.False(host.Get<TenantConnectionService>().IsReady);
        Assert.Contains("fabrikam", host.Get<TenantProfileService>().Find("Contoso")!.ConfigurationJson);

        var listed = McpTestHost.Parse(tools.List());
        Assert.Equal("Fabrikam", listed.GetProperty("activeProfile").GetString());
        Assert.Equal(2, listed.GetProperty("profiles").GetArrayLength());

        Assert.Contains("no profile", McpTestHost.Parse(await tools.Switch("Nobody", Cancellation)).GetProperty("error").GetString());
        Assert.Equal("Contoso", McpTestHost.Parse(tools.Delete("Contoso")).GetProperty("deleted").GetString());
        Assert.Equal(1, McpTestHost.Parse(tools.List()).GetProperty("profiles").GetArrayLength());
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
