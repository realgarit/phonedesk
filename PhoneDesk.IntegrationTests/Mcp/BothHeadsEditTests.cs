using PhoneDesk.Mcp.Tools;
using PhoneDesk.TestShared;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// The MCP half of the both-heads check: the edit tools run their real scripts in a real PowerShell
/// engine against the simulated tenant, and the tenant must end in the state the desktop page
/// leaves too (<see cref="SimulatedTenant.ExpectedState"/>; the desktop half is in PhoneDesk.Tests).
/// </summary>
public sealed class BothHeadsEditTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task McpTools_LeaveTheTenantInTheStateTheDesktopPageLeaves()
    {
        using var tenant = new SimulatedTenantPowerShell();
        using var host = new McpTestHost(powerShell: tenant).Connected();
        var tools = host.Tool<EditTools>();

        var queuePlan = McpTestHost.Parse(await tools.EditCallQueue(
            SimulatedTenant.QueueName, new(SimulatedTenant.QueueChanges), cancellationToken: Cancellation));
        Assert.Equal("planned", queuePlan.GetProperty("status").GetString());
        var queue = McpTestHost.Parse(await tools.EditCallQueue(
            SimulatedTenant.QueueName, new(SimulatedTenant.QueueChanges), queuePlan.GetProperty("planId").GetString(), Cancellation));
        AssertEverySettingConfirmed(queue);

        var attendantPlan = McpTestHost.Parse(await tools.EditAutoAttendant(
            SimulatedTenant.AttendantName, new(SimulatedTenant.AttendantChanges), cancellationToken: Cancellation));
        Assert.Equal("planned", attendantPlan.GetProperty("status").GetString());
        var attendant = McpTestHost.Parse(await tools.EditAutoAttendant(
            SimulatedTenant.AttendantName, new(SimulatedTenant.AttendantChanges), attendantPlan.GetProperty("planId").GetString(), Cancellation));
        AssertEverySettingConfirmed(attendant);

        await SimulatedTenant.AssertExpectedStateAsync(tenant, Cancellation);
    }

    [Fact]
    public async Task McpPhoneNumberTools_LeaveTheInventoryInTheStateTheDesktopPageLeaves()
    {
        using var tenant = new SimulatedTenantPowerShell(SimulatedTenant.PhoneTenant);
        using var host = new McpTestHost(powerShell: tenant).Connected();
        var tools = host.Tool<PhoneNumberTools>();

        var assignPlan = McpTestHost.Parse(await tools.SetPhoneNumberAssignment(SimulatedTenant.FreeNumber, SimulatedTenant.Ben, cancellationToken: Cancellation));
        var assigned = McpTestHost.Parse(await tools.SetPhoneNumberAssignment(
            SimulatedTenant.FreeNumber, SimulatedTenant.Ben, planId: PlanId(assignPlan), cancellationToken: Cancellation));
        Assert.False(assigned.TryGetProperty("error", out _), assigned.ToString());

        var removePlan = McpTestHost.Parse(await tools.RemovePhoneNumberAssignment(SimulatedTenant.AnnaNumber, cancellationToken: Cancellation));
        var removed = McpTestHost.Parse(await tools.RemovePhoneNumberAssignment(SimulatedTenant.AnnaNumber, PlanId(removePlan), Cancellation));
        Assert.False(removed.TryGetProperty("error", out _), removed.ToString());

        await SimulatedTenant.AssertExpectedPhoneStateAsync(tenant, Cancellation);
    }

    private static string PlanId(System.Text.Json.JsonElement plan)
    {
        Assert.True(plan.TryGetProperty("plan", out var planned), plan.ToString());
        Assert.Equal("planned", planned.GetProperty("status").GetString());
        return planned.GetProperty("planId").GetString() ?? throw new InvalidOperationException(plan.ToString());
    }

    private static void AssertEverySettingConfirmed(System.Text.Json.JsonElement applied)
    {
        Assert.Equal("applied", applied.GetProperty("status").GetString());
        foreach (var setting in applied.GetProperty("settings").EnumerateArray())
        {
            Assert.True(setting.GetProperty("saved").GetBoolean(), setting.ToString());
            Assert.True(setting.GetProperty("shownByTenant").GetBoolean(), setting.ToString());
        }
    }
}
