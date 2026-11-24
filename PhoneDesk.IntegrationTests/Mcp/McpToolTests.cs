using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using PhoneDesk.Mcp;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

public sealed class McpToolTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void ToolCatalog_HasUniqueSnakeCaseNamesAndDescriptions()
    {
        var tools = typeof(McpEnvironment).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(method => (Method: method, Tool: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(item => item.Tool is not null)
            .ToList();

        Assert.True(tools.Count >= 40, $"Expected the complete tool set, found {tools.Count}.");
        Assert.Equal(tools.Count, tools.Select(item => item.Tool!.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var (method, tool) in tools)
        {
            Assert.Matches("^[a-z0-9]+(_[a-z0-9]+)*$", tool!.Name!);
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.False(string.IsNullOrWhiteSpace(description), $"{tool.Name} has no description.");
            foreach (var parameter in method.GetParameters().Where(item => item.ParameterType != typeof(CancellationToken)))
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description),
                    $"{tool.Name}.{parameter.Name} has no description.");
            }

            // A delete tool must be marked destructive, and no other changing tool may be.
            if (tool.Name!.StartsWith("delete_", StringComparison.Ordinal))
            {
                Assert.False(tool.ReadOnly);
                Assert.True(tool.Destructive);
            }
            else if (!tool.ReadOnly)
            {
                Assert.False(tool.Destructive, $"{tool.Name} must not be marked destructive.");
            }
        }
    }

    [Fact]
    public void ServerAssembly_ReferencesNoUiFramework()
    {
        var forbidden = new[] { "Avalonia", "CommunityToolkit.Mvvm", "FluentAvalonia", "FluentIcons", "PhoneDesk.Presentation" };
        var references = typeof(McpEnvironment).Assembly.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty);
        Assert.DoesNotContain(references, name => forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Configuration_UpdateIsValidatedAndKeptForTheNextSession()
    {
        using var host = new McpTestHost().Configured();
        var description = McpTestHost.Parse(host.Tool<ConfigurationTools>().Get());

        Assert.Equal("ttgrp-contoso-reception", description.GetProperty("computedNames").GetProperty("m365Group").GetString());
        Assert.Equal("racq-contoso-reception@contoso.onmicrosoft.com",
            description.GetProperty("computedNames").GetProperty("callQueueResourceAccountUpn").GetString());
        Assert.True(description.GetProperty("validation").GetProperty("valid").GetBoolean());

        using var next = new McpTestHost(dataDirectory: host.DataDirectory);
        Assert.Equal("contoso", next.Get<ConfigurationStore>().Variables.Customer);
    }

    [Fact]
    public void Configuration_UnknownPathIsRejectedAndNothingChanges()
    {
        using var host = new McpTestHost().Configured();
        var result = McpTestHost.Parse(host.Tool<ConfigurationTools>().Update(new Dictionary<string, JsonElement>
        {
            ["general.customerName"] = McpTestHost.Json("other")
        }));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("general.customerName", result.GetProperty("error").GetString());
        Assert.Equal("contoso", host.Get<ConfigurationStore>().Variables.Customer);
    }

    [Fact]
    public async Task TenantTools_RefuseWithoutConnection()
    {
        using var host = new McpTestHost().Configured();
        var result = McpTestHost.Parse(await host.Tool<TenantReadTools>().Overview(cancellationToken: Cancellation));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Empty(host.PowerShell.Executed);
    }

    [Fact]
    public async Task TenantOverview_ReturnsCountsAndResolvesReferences()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<TenantReadTools>();

        var overview = McpTestHost.Parse(await tools.Overview(cancellationToken: Cancellation));
        Assert.Equal(1, overview.GetProperty("counts").GetProperty("autoAttendants").GetInt32());
        Assert.Equal(2, overview.GetProperty("counts").GetProperty("resourceAccounts").GetInt32());

        var queues = McpTestHost.Parse(await tools.ListCallQueues(cancellationToken: Cancellation));
        var queue = queues.GetProperty("rows")[0];
        Assert.Equal("Support Group", queue.GetProperty("agentGroups")[0].GetProperty("displayName").GetString());
        Assert.Equal("racq-one@contoso.com", queue.GetProperty("resourceAccounts")[0].GetProperty("userPrincipalName").GetString());

        // The second and third call used the session cache: the topology was read once.
        Assert.Single(host.PowerShell.Executed);
    }

    [Fact]
    public async Task Change_FirstCallOnlyPlans_SecondCallRunsTheSameScript()
    {
        using var host = new McpTestHost().Connected().Configured();
        var tools = host.Tool<ProvisioningTools>();

        var plan = McpTestHost.Parse(await tools.CreateM365Group(cancellationToken: Cancellation));
        Assert.Equal("planned", plan.GetProperty("status").GetString());
        Assert.Empty(host.PowerShell.Executed);

        var applied = McpTestHost.Parse(await tools.CreateM365Group(
            planId: plan.GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Equal("applied", applied.GetProperty("status").GetString());
        Assert.Equal(plan.GetProperty("script").GetString(), Assert.Single(host.PowerShell.Executed));
        Assert.True(host.Get<CreatedObjectLedger>().Contains(McpTestHost.TenantId, TenantObjectType.M365Group, "ttgrp-contoso-reception"));
    }

    [Fact]
    public async Task Change_PlanIsRejectedWhenTheScriptChangedOrThePlanWasUsed()
    {
        using var host = new McpTestHost().Connected().Configured();
        var tools = host.Tool<ProvisioningTools>();

        var plan = McpTestHost.Parse(await tools.CreateM365Group(cancellationToken: Cancellation));
        var planId = plan.GetProperty("planId").GetString();

        var changed = McpTestHost.Parse(await tools.CreateM365Group(name: "another-group", planId: planId, cancellationToken: Cancellation));
        Assert.False(changed.GetProperty("success").GetBoolean());

        // The failed attempt consumed the plan: it cannot be replayed with the original arguments.
        var replay = McpTestHost.Parse(await tools.CreateM365Group(planId: planId, cancellationToken: Cancellation));
        Assert.False(replay.GetProperty("success").GetBoolean());
        Assert.Empty(host.PowerShell.Executed);
    }

    [Fact]
    public async Task Change_TakesOneBackupPerSessionBeforeTheFirstChange()
    {
        using var host = new McpTestHost(skipBackup: false).Connected().Configured();
        var tools = host.Tool<ProvisioningTools>();

        var first = await PlanAndApplyAsync(planId => tools.CreateM365Group(planId: planId, cancellationToken: Cancellation));
        var backupFile = first.GetProperty("backupFile").GetString();
        Assert.True(File.Exists(backupFile));
        var readsForBackup = host.PowerShell.Executed.Count - 1;
        Assert.Equal(8, readsForBackup);
        // The backup ran before the change.
        Assert.Contains("ttgrp-contoso-reception", host.PowerShell.Executed[^1]);

        var second = await PlanAndApplyAsync(planId => tools.CreateCallQueue(planId: planId, cancellationToken: Cancellation));
        Assert.Equal(backupFile, second.GetProperty("backupFile").GetString());
        Assert.Equal(readsForBackup + 2, host.PowerShell.Executed.Count);
    }

    [Fact]
    public async Task Change_DoesNotRunWhenTheBackupFails()
    {
        using var host = new McpTestHost(skipBackup: false).Connected().Configured();
        host.PowerShell.Respond(host.Get<IPowerShellCommandService>().GetRetrieveTenantTopologyCommand(), "WARNING: partial read");
        var tools = host.Tool<ProvisioningTools>();

        var result = await PlanAndApplyAsync(planId => tools.CreateM365Group(planId: planId, cancellationToken: Cancellation));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("backup failed", result.GetProperty("error").GetString());
        Assert.DoesNotContain(host.PowerShell.Executed, script => script.Contains("ttgrp-contoso-reception", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_IsRefusedForAnObjectTheServerDidNotCreate()
    {
        using var host = new McpTestHost().Connected().Configured();

        var result = McpTestHost.Parse(await host.Tool<DeleteTools>().DeleteCallQueue("Support CQ", cancellationToken: Cancellation));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("was not created by this server", result.GetProperty("error").GetString());
        Assert.Empty(host.PowerShell.Executed);
    }

    [Fact]
    public async Task Delete_IsAllowedForAnObjectTheServerCreated_AndForgetsItAfterwards()
    {
        using var host = new McpTestHost().Connected().Configured();
        await PlanAndApplyAsync(planId => host.Tool<ProvisioningTools>().CreateCallQueue(planId: planId, cancellationToken: Cancellation));
        var delete = host.Tool<DeleteTools>();

        var deleted = await PlanAndApplyAsync(planId => delete.DeleteCallQueue("cq-contoso-reception", planId, Cancellation));

        Assert.Equal("applied", deleted.GetProperty("status").GetString());
        Assert.False(host.Get<CreatedObjectLedger>().Contains(McpTestHost.TenantId, TenantObjectType.CallQueue, "cq-contoso-reception"));
    }

    [Fact]
    public async Task Create_DoesNotClaimAnObjectThatAlreadyExisted()
    {
        using var host = new McpTestHost().Connected().Configured();
        host.PowerShell.DefaultResult = new() { Output = "SUCCESS: ttgrp-contoso-reception already exists. The existing group is used (ID: 1)" };

        await PlanAndApplyAsync(planId => host.Tool<ProvisioningTools>().CreateM365Group(planId: planId, cancellationToken: Cancellation));

        Assert.Empty(host.Get<CreatedObjectLedger>().List(McpTestHost.TenantId));
    }

    [Fact]
    public async Task GuidedSetup_RunsStepsInOrderAndRecordsFailures()
    {
        using var host = new McpTestHost().Connected().Configured();
        var tools = host.Tool<GuidedSetupTools>();

        var outOfOrder = McpTestHost.Parse(await tools.RunStep(2, cancellationToken: Cancellation));
        Assert.False(outOfOrder.GetProperty("success").GetBoolean());

        var first = await PlanAndApplyAsync(planId => tools.RunStep(1, planId, Cancellation));
        Assert.Equal("applied", first.GetProperty("status").GetString());

        host.PowerShell.DefaultResult = new()
        {
            Output = "ERROR: no license available",
            HadErrors = true,
            Errors = new[] { new PhoneDesk.Services.PowerShellErrorInfo { Message = "no license available" } }
        };
        var second = await PlanAndApplyAsync(planId => tools.RunStep(2, planId, Cancellation));
        Assert.Equal("failed", second.GetProperty("status").GetString());

        var status = McpTestHost.Parse(tools.Status());
        Assert.Equal(2, status.GetProperty("nextStep").GetInt32());
        Assert.Equal("completed", status.GetProperty("steps")[0].GetProperty("status").GetString());
        Assert.Equal("failed", status.GetProperty("steps")[1].GetProperty("status").GetString());
        Assert.False(status.GetProperty("steps")[2].GetProperty("canRun").GetBoolean());
    }

    [Fact]
    public async Task GuidedSetup_RefusesAnInvalidConfiguration()
    {
        using var host = new McpTestHost().Connected();

        var result = McpTestHost.Parse(await host.Tool<GuidedSetupTools>().RunStep(1, cancellationToken: Cancellation));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("not valid", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task BulkSetup_RejectsAnInvalidRowBeforePlanning()
    {
        using var host = new McpTestHost().Connected();
        var csv = "Customer,CustomerGroupName,MsFallbackDomain\ncontoso,,contoso.onmicrosoft.com\n";

        var result = McpTestHost.Parse(await host.Tool<BulkTools>().BulkSetup(csv, cancellationToken: Cancellation));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Row 1", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HealthCheck_ReturnsFindingsAndHonoursSuppression()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<HealthAndBackupTools>();

        var report = McpTestHost.Parse(await tools.RunHealthCheck(cancellationToken: Cancellation));
        Assert.True(report.TryGetProperty("activeFindingCount", out var activeCount), report.GetRawText());
        var active = activeCount.GetInt32();
        Assert.True(active > 0);

        var findingId = report.GetProperty("activeFindings")[0].GetProperty("findingId").GetString()!;
        var afterSuppress = McpTestHost.Parse(tools.SetFindingSuppressed(findingId, suppressed: true));
        Assert.Equal(active - 1, afterSuppress.GetProperty("lastRun").GetProperty("activeFindingCount").GetInt32());
    }

    [Fact]
    public async Task Connection_SignsInWithTheSameScriptsAndSuccessTestsAsTheDesktopApp()
    {
        using var host = new McpTestHost();
        var commands = host.Get<IPowerShellCommandService>();
        host.PowerShell.Respond(commands.GetCheckModulesCommand(),
            "MicrosoftTeams module is available\nMicrosoft.Graph.Authentication module is available\n" +
            "Microsoft.Graph.Users module is available\nMicrosoft.Graph.Users.Actions module is available\n" +
            "Microsoft.Graph.Groups module is available\nMicrosoft.Graph.Identity.DirectoryManagement module is available\n");
        host.PowerShell.Respond(commands.GetConnectTeamsCommand(),
            "SUCCESS: Connected to Microsoft Teams\nConnected to tenant: Contoso (tenant-1)\n");
        var tools = host.Tool<SessionTools>();

        var teamsTooEarly = McpTestHost.Parse(await tools.ConnectTeams(Cancellation));
        Assert.False(teamsTooEarly.GetProperty("success").GetBoolean());

        Assert.True(McpTestHost.Parse(await tools.CheckModules(Cancellation)).GetProperty("success").GetBoolean());
        Assert.True(McpTestHost.Parse(await tools.ConnectTeams(Cancellation)).GetProperty("success").GetBoolean());
        var graph = McpTestHost.Parse(await tools.ConnectGraph(Cancellation));

        Assert.True(graph.GetProperty("success").GetBoolean());
        Assert.Equal("tenant-1", graph.GetProperty("status").GetProperty("tenantId").GetString());
        Assert.Equal("admin@contoso.com", graph.GetProperty("status").GetProperty("graphAccount").GetString());
        // The token never appears in a script.
        Assert.DoesNotContain(host.PowerShell.Executed, script => script.Contains("test-token", StringComparison.Ordinal));
    }

    private static async Task<JsonElement> PlanAndApplyAsync(Func<string?, Task<string>> call)
    {
        var plan = McpTestHost.Parse(await call(null));
        Assert.Equal("planned", plan.GetProperty("status").GetString());
        return McpTestHost.Parse(await call(plan.GetProperty("planId").GetString()));
    }
}
