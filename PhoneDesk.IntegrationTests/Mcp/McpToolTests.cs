using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using PhoneDesk.Holidays;
using PhoneDesk.Mcp;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

public sealed class McpToolTests
{
    private const string GroupId = "11111111-2222-3333-4444-555555555555";

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
            if (tool.Name!.StartsWith("delete_", StringComparison.Ordinal) || tool.Name is "release_phone_number" or "retire_holiday_schedules" or "rollback_setup_run")
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

    [Theory]
    [InlineData("AG", 6)]
    [InlineData("FR", 2)]
    [InlineData("SO", 2)]
    public void SwissHolidays_WithoutRegionListsTheRegionsAndNoHolidays(string canton, int regions)
    {
        using var host = new McpTestHost().Configured();
        var tools = host.Tool<ConfigurationTools>();

        foreach (var result in new[] { McpTestHost.Parse(tools.SwissHolidays(canton, 2026)), McpTestHost.Parse(tools.ApplySwissHolidays(canton, 2026)) })
        {
            Assert.True(result.GetProperty("regionRequired").GetBoolean());
            Assert.False(result.GetProperty("applied").GetBoolean());
            Assert.Equal(regions, result.GetProperty("regions").GetArrayLength());
            Assert.False(result.TryGetProperty("holidays", out _));
            foreach (var region in result.GetProperty("regions").EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(region.GetProperty("code").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(region.GetProperty("covers").GetString()));
            }
        }

        Assert.Empty(host.Get<ConfigurationStore>().Variables.HolidaySeries);
    }

    [Fact]
    public void SwissHolidays_EveryListedRegionGivesACompleteList()
    {
        using var host = new McpTestHost();
        var tools = host.Tool<ConfigurationTools>();
        foreach (var canton in Enum.GetValues<SwissCanton>())
        {
            foreach (var region in SwissHolidayRegions.For(canton))
            {
                Assert.Equal(HolidayResultCompleteness.Complete,
                    SwissHolidayCatalog.Compute(canton, region.Code, 2026).Completeness);
                var result = McpTestHost.Parse(tools.SwissHolidays(canton.ToString(), 2026, region.Code.ToUpperInvariant()));
                Assert.Equal(region.Code, result.GetProperty("region").GetString());
                Assert.True(result.GetProperty("holidays").GetArrayLength() > 5);
            }
        }
    }

    [Theory]
    [InlineData("AG", 2026, "Brugg", "aarau")]
    [InlineData("FR", 2026, "Murten", "fr-reformed")]
    [InlineData("SO", 2026, "Bucheggberg", "so-bucheggberg")]
    [InlineData("ZH", 2031, null, "2025 to 2030")]
    [InlineData("ZH", 2024, null, "2025 to 2030")]
    [InlineData("XX", 2026, null, "ZH")]
    [InlineData("3", 2026, null, "ZH")]
    public void SwissHolidays_UnknownRegionYearOrCantonIsAnErrorAndWritesNothing(string canton, int year, string? region, string expected)
    {
        using var host = new McpTestHost().Configured();
        var tools = host.Tool<ConfigurationTools>();

        foreach (var result in new[] { McpTestHost.Parse(tools.SwissHolidays(canton, year, region)), McpTestHost.Parse(tools.ApplySwissHolidays(canton, year, region)) })
        {
            Assert.False(result.GetProperty("success").GetBoolean());
            Assert.Contains(expected, result.GetProperty("error").GetString());
        }

        Assert.Empty(host.Get<ConfigurationStore>().Variables.HolidaySeries);
    }

    [Fact]
    public void SwissHolidays_CantonWithoutRegionsIgnoresARegionAndSaysSo()
    {
        using var host = new McpTestHost();
        var result = McpTestHost.Parse(host.Tool<ConfigurationTools>().SwissHolidays("zh", 2026, "Winterthur"));

        Assert.False(result.GetProperty("regionRequired").GetBoolean());
        Assert.Contains("Winterthur", result.GetProperty("note").GetString());
        Assert.Equal(9, result.GetProperty("holidays").GetArrayLength());
    }

    [Fact]
    public void SwissHolidaysApply_WritesTheRegionListOnceAndKeepsCategoriesAndOtherEntries()
    {
        using var host = new McpTestHost().Configured();
        var tools = host.Tool<ConfigurationTools>();
        var own = McpTestHost.Parse(tools.Update(new Dictionary<string, JsonElement>
        {
            ["holidayTemplate.series"] = McpTestHost.Json(new[] { new { date = "2026-12-24", time = "12:00:00", name = "Betriebsferien" } })
        }));
        Assert.True(own.GetProperty("applied").GetBoolean());

        var first = McpTestHost.Parse(tools.ApplySwissHolidays("SO", 2026, "so-other"));
        Assert.True(first.GetProperty("applied").GetBoolean());
        Assert.Equal(12, first.GetProperty("added").GetArrayLength());
        Assert.Equal(13, first.GetProperty("holidayEntriesNow").GetInt32());

        var second = McpTestHost.Parse(tools.ApplySwissHolidays("SO", 2026, "so-other"));
        Assert.Equal(0, second.GetProperty("added").GetArrayLength());
        Assert.Equal(12, second.GetProperty("alreadyPresent").GetInt32());

        using var next = new McpTestHost(dataDirectory: host.DataDirectory);
        var series = next.Get<ConfigurationStore>().Document.Configuration.HolidayTemplate.Series;
        Assert.Equal(13, series.Count);
        Assert.Contains(series, entry => entry.Name == "Betriebsferien");
        var labourDay = Assert.Single(series, entry => entry.Date == new DateOnly(2026, 5, 1));
        Assert.Equal(new TimeOnly(12, 0), labourDay.Time);
        Assert.Equal(HolidayCategory.Statutory, labourDay.Category);
        Assert.Contains(series, entry => entry.Category == HolidayCategory.SundayObservance);
    }

    [Fact]
    public void SwissHolidaysApply_CanLeaveOutAdditionalRestDays()
    {
        using var host = new McpTestHost().Configured();
        var tools = host.Tool<ConfigurationTools>();

        var result = McpTestHost.Parse(tools.ApplySwissHolidays("TI", 2026, includeAdditionalRestDays: false));

        var series = host.Get<ConfigurationStore>().Document.Configuration.HolidayTemplate.Series;
        Assert.Equal(series.Count, result.GetProperty("holidayEntriesNow").GetInt32());
        Assert.Equal(9, series.Count);
        Assert.DoesNotContain(series, entry => entry.Category == HolidayCategory.AdditionalCantonalRestDay);
    }

    [Fact]
    public void SwissHolidays_LabelsAdditionalRestDaysLikeTheDesktopPickerAndAsksToConfirmThem()
    {
        using var host = new McpTestHost().Configured();
        var tools = host.Tool<ConfigurationTools>();

        // The labels are the texts of the desktop picker.
        var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "PhoneDesk.Presentation", "Resources", "Localization", "Strings.en.json"))).RootElement;
        Assert.Equal(catalog.GetProperty("HolidayCategoryStatutory").GetString(), ConfigurationTools.Label(HolidayCategory.Statutory));
        Assert.Equal(catalog.GetProperty("HolidayCategoryAdditional").GetString(), ConfigurationTools.Label(HolidayCategory.AdditionalCantonalRestDay));
        Assert.Equal(catalog.GetProperty("HolidayCategorySunday").GetString(), ConfigurationTools.Label(HolidayCategory.SundayObservance));
        Assert.Equal(catalog.GetProperty("HolidayCategoryCustom").GetString(), ConfigurationTools.Label(HolidayCategory.Custom));

        var ticino = McpTestHost.Parse(tools.SwissHolidays("TI", 2026));
        Assert.Equal(6, ticino.GetProperty("review").GetProperty("additionalCantonalRestDays").GetArrayLength());
        Assert.Contains("Confirm with the customer", ticino.GetProperty("review").GetProperty("message").GetString());
        Assert.Contains(ticino.GetProperty("holidays").EnumerateArray(),
            holiday => holiday.GetProperty("name").GetString() == "San Giuseppe" &&
                       holiday.GetProperty("label").GetString()!.Contains("confirm with customer"));

        // Nothing to confirm: no review block.
        Assert.False(McpTestHost.Parse(tools.SwissHolidays("ZH", 2026)).TryGetProperty("review", out _));
        Assert.False(McpTestHost.Parse(tools.SwissHolidays("TI", 2026, includeAdditionalRestDays: false)).TryGetProperty("review", out _));

        var applied = McpTestHost.Parse(tools.ApplySwissHolidays("TI", 2026));
        Assert.Equal(6, applied.GetProperty("review").GetProperty("additionalCantonalRestDays").GetArrayLength());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PhoneDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("PhoneDesk.slnx was not found above the test folder.");
    }

    [Fact]
    public void SwissHolidaysApply_NeverRemovesEntries()
    {
        var method = typeof(ConfigurationTools).GetMethod(nameof(ConfigurationTools.ApplySwissHolidays))!;
        Assert.DoesNotContain(method.GetParameters(), parameter => parameter.Name!.Contains("replace", StringComparison.OrdinalIgnoreCase));

        using var host = new McpTestHost().Configured();
        var tools = host.Tool<ConfigurationTools>();
        tools.ApplySwissHolidays("ZH", 2026);
        tools.ApplySwissHolidays("ZH", 2027);
        tools.ApplySwissHolidays("TI", 2026);

        var series = host.Get<ConfigurationStore>().Document.Configuration.HolidayTemplate.Series;
        Assert.Contains(series, entry => entry.Name == "Karfreitag" && entry.Date.Year == 2026);
        Assert.Equal(9, series.Count(entry => entry.Date.Year == 2027));
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
        using var host = new McpTestHost(skipBackup: false).Connected().Configured();
        host.PowerShell.DefaultResult = new() { Output = $"SUCCESS: ttgrp-contoso-reception created successfully with ID: {GroupId}" };
        var tools = host.Tool<ProvisioningTools>();

        var plan = McpTestHost.Parse(await tools.CreateM365Group(cancellationToken: Cancellation));
        Assert.Equal("planned", plan.GetProperty("status").GetString());
        Assert.Empty(host.PowerShell.Executed);

        var applied = McpTestHost.Parse(await tools.CreateM365Group(
            planId: plan.GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Equal("applied", applied.GetProperty("status").GetString());
        Assert.Equal(plan.GetProperty("script").GetString(), host.PowerShell.Executed[^1]);
        // The group is recorded by its object ID, because that is what a delete uses.
        Assert.True(host.Get<CreatedObjectLedger>().Contains(McpTestHost.TenantId, TenantObjectType.M365Group, GroupId));
        Assert.False(host.Get<CreatedObjectLedger>().Contains(McpTestHost.TenantId, TenantObjectType.M365Group, "ttgrp-contoso-reception"));
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
        // The second change did not read the backup sources again.
        var tenantInfo = host.Get<IDocumentationScriptBuilder>().GetExportTenantInfoCommand();
        Assert.Equal(1, host.PowerShell.Executed.Count(script => script == tenantInfo));
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
    public async Task Backup_AcceptsAnInformationalCmdletWarningButNotAnUnknownOne()
    {
        using var host = new McpTestHost(skipBackup: false).Connected();
        var callQueues = host.Get<IDocumentationScriptBuilder>().GetExportCallQueuesCommand();
        var tools = host.Tool<HealthAndBackupTools>();

        host.PowerShell.Respond(callQueues,
            "WARNING: ConferenceMode is turned on for this callqueue. The call will be established using the traditional transfer mode or the new conference mode.\n" +
            "DOCDATA_CQ_START\nDOCDATA_CQ_END");
        var informational = McpTestHost.Parse(await tools.BackupTenant(Cancellation));
        Assert.True(informational.GetProperty("success").GetBoolean(), informational.GetRawText());

        host.PowerShell.Respond(callQueues, "WARNING: More results are available.\nDOCDATA_CQ_START\nDOCDATA_CQ_END");
        var unknown = McpTestHost.Parse(await tools.BackupTenant(Cancellation));
        Assert.False(unknown.GetProperty("success").GetBoolean());
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
        using var host = new McpTestHost(skipBackup: false).Connected().Configured();
        await PlanAndApplyAsync(planId => host.Tool<ProvisioningTools>().CreateCallQueue(planId: planId, cancellationToken: Cancellation));
        host.PowerShell.Respond(
            host.Get<IPowerShellCommandService>().GetRetrieveTenantTopologyCommand(),
            "TOPCQ: cq-contoso-reception|cq-id-2|Attendant|30||grp-1|ra-obj-1\n" + McpTestHost.TopologyOutput);
        var delete = host.Tool<DeleteTools>();

        var deleted = await PlanAndApplyAsync(planId => delete.DeleteCallQueue("cq-contoso-reception", planId, Cancellation));

        Assert.Equal("applied", deleted.GetProperty("status").GetString());
        Assert.False(host.Get<CreatedObjectLedger>().Contains(McpTestHost.TenantId, TenantObjectType.CallQueue, "cq-contoso-reception"));
    }

    [Fact]
    public async Task Delete_ThatReportsAnErrorButRemovedTheObjectCountsAsDone()
    {
        using var host = new McpTestHost(allowDeleteExisting: true).Connected();
        var commands = host.Get<IPowerShellCommandService>();
        host.Get<CreatedObjectLedger>().Record(McpTestHost.TenantId, TenantObjectType.AutoAttendant, "Main AA", "CreateAutoAttendant");
        var delete = host.Tool<DeleteTools>();

        var plan = McpTestHost.Parse(await delete.DeleteAutoAttendant("Main AA", cancellationToken: Cancellation));
        // The script removes the auto attendant and then fails in a later step.
        var remove = commands.GetRemoveAutoAttendantCommand("Main AA");
        host.PowerShell.Respond(remove, "ERROR: Object reference not set to an instance of an object.", hadErrors: true);
        host.PowerShell.AfterRun = script =>
        {
            if (script == remove)
            {
                host.PowerShell.Respond(
                    commands.GetRetrieveTenantTopologyCommand(),
                    "TOPGRP: Support Group|grp-1|support|Support team\nSUCCESS: Tenant topology retrieved\n");
            }
        };
        var result = McpTestHost.Parse(await delete.DeleteAutoAttendant("Main AA", plan.GetProperty("planId").GetString(), Cancellation));

        Assert.Equal("applied", result.GetProperty("status").GetString());
        Assert.Contains("the change was made", result.GetProperty("note").GetString());
        Assert.False(host.Get<CreatedObjectLedger>().Contains(McpTestHost.TenantId, TenantObjectType.AutoAttendant, "Main AA"));
    }

    [Fact]
    public async Task Delete_ThatFailsAndLeavesTheObjectStaysFailed()
    {
        using var host = new McpTestHost(allowDeleteExisting: true).Connected();
        host.PowerShell.Respond(
            host.Get<IPowerShellCommandService>().GetRemoveAutoAttendantCommand("Main AA"), "ERROR: access denied", hadErrors: true);
        var delete = host.Tool<DeleteTools>();

        var result = await PlanAndApplyAsync(planId => delete.DeleteAutoAttendant("Main AA", planId, Cancellation));

        Assert.Equal("failed", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReleasePhoneNumber_RemovesOnlyAnUnassignedNumberThisServerAdded()
    {
        using var host = new McpTestHost(skipBackup: false).Connected().Configured();
        var ledger = host.Get<CreatedObjectLedger>();
        var delete = host.Tool<DeleteTools>();

        // The assignment adds a Direct Routing number the tenant did not have before.
        var assign = await PlanAndApplyAsync(planId => host.Tool<ProvisioningTools>().AssignPhoneNumber(planId: planId, cancellationToken: Cancellation));
        Assert.Equal("applied", assign.GetProperty("status").GetString());
        Assert.True(ledger.Contains(McpTestHost.TenantId, TenantObjectType.PhoneNumber, "+41441234567"));

        host.PowerShell.Respond(
            host.Get<IDocumentationScriptBuilder>().GetExportPhoneNumbersCommand(),
            "DOCDATA_PHONE_START\n" +
            "DOCDATA_PHONE: +41441234567|DirectRouting||Unassigned|Activated||VoiceApplicationAssignment\n" +
            "DOCDATA_PHONE: +41442222222|OperatorConnect||Unassigned|Activated||UserAssignment\n" +
            "DOCDATA_PHONE: +41443333333|OperatorConnect|user-1|UserAssigned|Activated||UserAssignment\n" +
            "DOCDATA_PHONE: +41444444444|DirectRouting|ra-1|VoiceApplicationAssigned|Activated||VoiceApplicationAssignment\n" +
            "DOCDATA_PHONE_END");

        var assigned = McpTestHost.Parse(await delete.ReleasePhoneNumber("+41444444444", cancellationToken: Cancellation));
        Assert.Contains("is assigned", assigned.GetProperty("error").GetString());

        // A number from an operator or from Microsoft is never released, assigned or not, recorded or not.
        var operatorNumber = McpTestHost.Parse(await delete.ReleasePhoneNumber("+41443333333", cancellationToken: Cancellation));
        Assert.Contains("Only a Direct Routing number", operatorNumber.GetProperty("error").GetString());
        ledger.Record(McpTestHost.TenantId, TenantObjectType.PhoneNumber, "+41442222222", "AssignPhoneNumber");
        operatorNumber = McpTestHost.Parse(await delete.ReleasePhoneNumber("+41442222222", cancellationToken: Cancellation));
        Assert.Contains("Only a Direct Routing number", operatorNumber.GetProperty("error").GetString());
        ledger.Remove(McpTestHost.TenantId, TenantObjectType.PhoneNumber, "+41442222222");

        var missing = McpTestHost.Parse(await delete.ReleasePhoneNumber("+41449999999", cancellationToken: Cancellation));
        Assert.Contains("not in the tenant's phone number inventory", missing.GetProperty("error").GetString());

        var malformed = McpTestHost.Parse(await delete.ReleasePhoneNumber("0441234567\"; Remove-Item", cancellationToken: Cancellation));
        Assert.Contains("E.164", malformed.GetProperty("error").GetString());

        var released = await PlanAndApplyAsync(planId => delete.ReleasePhoneNumber("+41441234567", planId, Cancellation));
        Assert.Equal("applied", released.GetProperty("status").GetString());
        Assert.Contains(host.PowerShell.Executed, script => script.Contains("Remove-CsOnlineTelephoneNumber -TelephoneNumber \"+41441234567\" -ErrorAction Stop", StringComparison.Ordinal));
        Assert.Empty(ledger.List(McpTestHost.TenantId));
    }

    [Fact]
    public async Task AssignPhoneNumber_DoesNotClaimANumberTheTenantAlreadyHad()
    {
        using var host = new McpTestHost(skipBackup: false).Connected().Configured();
        host.PowerShell.Respond(
            host.Get<IDocumentationScriptBuilder>().GetExportPhoneNumbersCommand(),
            "DOCDATA_PHONE_START\nDOCDATA_PHONE: +41441234567|OperatorConnect||Unassigned|Activated||VoiceApplicationAssignment\nDOCDATA_PHONE_END");

        var assign = await PlanAndApplyAsync(planId => host.Tool<ProvisioningTools>().AssignPhoneNumber(planId: planId, cancellationToken: Cancellation));

        Assert.Equal("applied", assign.GetProperty("status").GetString());
        Assert.Empty(host.Get<CreatedObjectLedger>().List(McpTestHost.TenantId));
    }

    [Fact]
    public async Task Delete_OfAnObjectThatIsAlreadyGoneForgetsIt()
    {
        using var host = new McpTestHost().Connected();
        var ledger = host.Get<CreatedObjectLedger>();
        ledger.Record(McpTestHost.TenantId, TenantObjectType.HolidaySchedule, "hd-contoso-2027", "CreateHolidaySeries");

        var result = McpTestHost.Parse(await host.Tool<DeleteTools>().DeleteHolidaySchedule("hd-contoso-2027", cancellationToken: Cancellation));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("not found", result.GetProperty("error").GetString());
        Assert.Empty(ledger.List(McpTestHost.TenantId));
    }

    [Fact]
    public async Task Create_ThatFailsLateStillRecordsTheNewObjectAndConfirmsAnAutoAttendantInTheTenant()
    {
        using var host = new McpTestHost(skipBackup: false).Connected().Configured();
        var commands = host.Get<IPowerShellCommandService>();
        var tools = host.Tool<ProvisioningTools>();
        host.PowerShell.DefaultResult = new()
        {
            Output = "ERROR: Object reference not set to an instance of an object.",
            HadErrors = true,
            Errors = new[] { new PhoneDesk.Services.PowerShellErrorInfo { Message = "Object reference not set to an instance of an object." } }
        };

        // The call queue is not in the tenant afterwards: the failure stands and nothing is recorded.
        var queue = await PlanAndApplyAsync(planId => tools.CreateCallQueue(planId: planId, cancellationToken: Cancellation));
        Assert.Equal("failed", queue.GetProperty("status").GetString());
        Assert.Empty(host.Get<CreatedObjectLedger>().List(McpTestHost.TenantId));

        // A failed create that did leave the object in the tenant is recorded, so it can be cleaned up.
        host.PowerShell.AfterRun = script =>
        {
            if (script.Contains("racq-contoso-reception", StringComparison.Ordinal) && !script.Contains("TOPRA", StringComparison.Ordinal))
            {
                host.PowerShell.Respond(
                    commands.GetRetrieveTenantTopologyCommand(),
                    "TOPRA: racq|racq-contoso-reception@contoso.onmicrosoft.com|ra-obj-9||CallQueue|True\n" + McpTestHost.TopologyOutput);
            }
        };
        var account = await PlanAndApplyAsync(planId => tools.CreateResourceAccount("callQueue", planId: planId, cancellationToken: Cancellation));
        Assert.Equal("failed", account.GetProperty("status").GetString());
        Assert.True(host.Get<CreatedObjectLedger>().Contains(
            McpTestHost.TenantId, TenantObjectType.ResourceAccount, "racq-contoso-reception@contoso.onmicrosoft.com"));

        // The auto attendant is in the tenant afterwards: the module failed after the work was done.
        host.PowerShell.AfterRun = script =>
        {
            if (script.Contains("aa-contoso-main-reception", StringComparison.Ordinal))
            {
                host.PowerShell.Respond(
                    commands.GetRetrieveTenantTopologyCommand(),
                    "TOPAA: aa-contoso-main-reception|aa-id-9|de-DE|W. Europe Standard Time|||\n" + McpTestHost.TopologyOutput);
            }
        };
        var attendant = await PlanAndApplyAsync(planId => tools.CreateAutoAttendant(planId: planId, cancellationToken: Cancellation));
        Assert.Equal("applied", attendant.GetProperty("status").GetString());
        Assert.Contains("the change was made", attendant.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Delete_IsRefusedWhenAnotherObjectLaterTookTheName()
    {
        using var host = new McpTestHost(skipBackup: false).Connected().Configured();
        var topology = host.Get<IPowerShellCommandService>().GetRetrieveTenantTopologyCommand();
        var delete = host.Tool<DeleteTools>();
        await PlanAndApplyAsync(planId => host.Tool<ProvisioningTools>().CreateCallQueue(planId: planId, cancellationToken: Cancellation));

        // The created queue becomes visible: the server stores its immutable ID.
        host.PowerShell.Respond(topology, "TOPCQ: cq-contoso-reception|cq-created|Attendant|30||grp-1|ra-obj-1\n" + McpTestHost.TopologyOutput);
        Assert.Equal("planned", McpTestHost.Parse(await delete.DeleteCallQueue("cq-contoso-reception", cancellationToken: Cancellation)).GetProperty("status").GetString());
        Assert.Equal("cq-created", host.Get<CreatedObjectLedger>().Find(McpTestHost.TenantId, TenantObjectType.CallQueue, "cq-contoso-reception")!.ObjectId);

        // Someone deletes it and creates another queue with the same name.
        host.PowerShell.Respond(topology, "TOPCQ: cq-contoso-reception|cq-replacement|Attendant|30||grp-1|ra-obj-1\n" + McpTestHost.TopologyOutput);
        var result = McpTestHost.Parse(await delete.DeleteCallQueue("cq-contoso-reception", cancellationToken: Cancellation));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("different object ID", result.GetProperty("error").GetString());
        Assert.Empty(host.Get<CreatedObjectLedger>().List(McpTestHost.TenantId));
    }

    [Fact]
    public async Task Delete_IsRefusedForARecordWithoutAnObjectIdFromAnEarlierSession()
    {
        using var host = new McpTestHost().Connected();
        host.Get<CreatedObjectLedger>().Record(McpTestHost.TenantId, TenantObjectType.CallQueue, "Support CQ", "CreateCallQueue");

        var result = McpTestHost.Parse(await host.Tool<DeleteTools>().DeleteCallQueue("Support CQ", cancellationToken: Cancellation));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("without its object ID", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TenantDetails_AreNotServedFromAnotherTenantOrAfterDisconnect()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<TenantReadTools>();
        Assert.True(McpTestHost.Parse(await tools.Details(cancellationToken: Cancellation)).TryGetProperty("sections", out _));
        var readsForOneSnapshot = host.PowerShell.Executed.Count;

        // Another tenant in the same server process: the snapshot is read again.
        host.Get<ISessionManager>().UpdateTenantInfo("tenant-2", "Fabrikam");
        Assert.True(McpTestHost.Parse(await tools.Details(cancellationToken: Cancellation)).TryGetProperty("sections", out _));
        Assert.Equal(readsForOneSnapshot * 2, host.PowerShell.Executed.Count);

        host.Get<ISessionManager>().ResetSession();
        var afterDisconnect = McpTestHost.Parse(await tools.Details(cancellationToken: Cancellation));
        Assert.False(afterDisconnect.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Update_OfAnExistingAutoAttendantThatFailsStaysFailed()
    {
        using var host = new McpTestHost(skipBackup: false).Connected().Configured();
        // The auto attendant exists before the call: its existence afterwards proves nothing about the update.
        host.PowerShell.Respond(
            host.Get<IPowerShellCommandService>().GetRetrieveTenantTopologyCommand(),
            "TOPAA: aa-contoso-main-reception|aa-id-9|de-DE|W. Europe Standard Time|||\n" + McpTestHost.TopologyOutput);
        host.PowerShell.DefaultResult = new()
        {
            Output = "ERROR: Object reference not set to an instance of an object.",
            HadErrors = true,
            Errors = new[] { new PhoneDesk.Services.PowerShellErrorInfo { Message = "Object reference not set to an instance of an object." } }
        };

        var update = await PlanAndApplyAsync(planId => host.Tool<ProvisioningTools>().CreateAutoAttendant(planId: planId, cancellationToken: Cancellation));
        var step = await PlanAndApplyAsync(planId => host.Tool<GuidedSetupTools>().RunStep(1, planId, Cancellation));

        Assert.Equal("failed", update.GetProperty("status").GetString());
        Assert.False(update.TryGetProperty("note", out _));
        Assert.Equal("failed", step.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_DoesNotClaimAnObjectThatAlreadyExisted()
    {
        using var host = new McpTestHost(skipBackup: false).Connected().Configured();
        host.PowerShell.DefaultResult = new() { Output = "SUCCESS: ttgrp-contoso-reception already exists. The existing group is used (ID: 1)" };

        await PlanAndApplyAsync(planId => host.Tool<ProvisioningTools>().CreateM365Group(planId: planId, cancellationToken: Cancellation));

        Assert.Empty(host.Get<CreatedObjectLedger>().List(McpTestHost.TenantId));
    }

    [Fact]
    public async Task Create_DoesNotClaimAnObjectThatWasInTheBackupBeforeTheChange()
    {
        using var host = new McpTestHost(skipBackup: false).Connected().Configured();
        // A minimal create does not report an existing object with the same name.
        host.PowerShell.Respond(
            host.Get<IPowerShellCommandService>().GetRetrieveTenantTopologyCommand(),
            "TOPAA: aa-contoso-main-reception|aa-id-9|de-DE|W. Europe Standard Time|||\n" + McpTestHost.TopologyOutput);

        var created = await PlanAndApplyAsync(planId => host.Tool<ProvisioningTools>().CreateAutoAttendant(
            planId: planId, cancellationToken: Cancellation));

        Assert.Equal("applied", created.GetProperty("status").GetString());
        Assert.Empty(host.Get<CreatedObjectLedger>().List(McpTestHost.TenantId));
    }

    [Fact]
    public async Task Create_WithoutABackupClaimsNothing()
    {
        using var host = new McpTestHost(skipBackup: true).Connected().Configured();

        await PlanAndApplyAsync(planId => host.Tool<ProvisioningTools>().CreateCallQueue(planId: planId, cancellationToken: Cancellation));

        Assert.Empty(host.Get<CreatedObjectLedger>().List(McpTestHost.TenantId));
    }

    [Fact]
    public async Task Delete_IsRefusedWhenTheNameIsNotUnique()
    {
        using var host = new McpTestHost(allowDeleteExisting: true).Connected();
        host.PowerShell.Respond(
            host.Get<IPowerShellCommandService>().GetRetrieveTenantTopologyCommand(),
            "TOPCQ: Support CQ|cq-id-2|Attendant|30|||\n" + McpTestHost.TopologyOutput);

        var result = McpTestHost.Parse(await host.Tool<DeleteTools>().DeleteCallQueue("Support CQ", cancellationToken: Cancellation));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("2 objects named", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_OfAGroupChecksTheObjectIdNotTheName()
    {
        using var host = new McpTestHost().Connected();
        host.Get<CreatedObjectLedger>().Record(McpTestHost.TenantId, TenantObjectType.M365Group, GroupId, "CreateM365Group", "ttgrp-contoso-reception");
        host.PowerShell.Respond(
            host.Get<IPowerShellCommandService>().GetRetrieveTenantTopologyCommand(),
            $"TOPGRP: ttgrp-contoso-reception|{GroupId}|ttgrp-contoso-reception|\n" + McpTestHost.TopologyOutput);
        var delete = host.Tool<DeleteTools>();

        var otherGroup = McpTestHost.Parse(await delete.DeleteM365Group(
            "99999999-2222-3333-4444-555555555555", "ttgrp-contoso-reception", cancellationToken: Cancellation));
        var ownGroup = McpTestHost.Parse(await delete.DeleteM365Group(GroupId, "ttgrp-contoso-reception", cancellationToken: Cancellation));

        Assert.False(otherGroup.GetProperty("success").GetBoolean());
        Assert.Equal("planned", ownGroup.GetProperty("status").GetString());
    }

    [Fact]
    public void Files_ExportStaysInTheExportFolderAndImportReadsOnlyJson()
    {
        using var host = new McpTestHost().Configured();
        var tools = host.Tool<ConfigurationTools>();

        var outside = McpTestHost.Parse(tools.Export(Path.Combine(Path.GetTempPath(), "outside.json")));
        var exported = McpTestHost.Parse(tools.Export("contoso.json"));
        var again = McpTestHost.Parse(tools.Export("contoso.json"));
        var notJson = McpTestHost.Parse(tools.Import(Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts")));

        Assert.False(outside.GetProperty("success").GetBoolean());
        Assert.StartsWith(host.DataDirectory, exported.GetProperty("file").GetString());
        Assert.False(again.GetProperty("success").GetBoolean());
        Assert.False(notJson.GetProperty("success").GetBoolean());

        var preview = McpTestHost.Parse(tools.Import(exported.GetProperty("file").GetString()!));
        Assert.Equal(0, preview.GetProperty("changes").GetArrayLength());
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
    public async Task GuidedSetup_NamesTheObjectAStepWorksOnAndGivesARetryHintForTheDelayAfterANewAccount()
    {
        using var host = new McpTestHost().Connected().Configured();
        var tools = host.Tool<GuidedSetupTools>();
        Assert.Equal("applied", (await PlanAndApplyAsync(planId => tools.RunStep(1, planId, Cancellation))).GetProperty("status").GetString());
        Assert.Equal("applied", (await PlanAndApplyAsync(planId => tools.RunStep(2, planId, Cancellation))).GetProperty("status").GetString());

        host.PowerShell.DefaultResult = new()
        {
            Output = "ERROR: Failed to update usage location: Resource does not exist or one of its queried reference-property objects are not present.",
            HadErrors = true,
            Errors = new[] { new PhoneDesk.Services.PowerShellErrorInfo { Message = "Resource does not exist or one of its queried reference-property objects are not present." } }
        };
        var third = await PlanAndApplyAsync(planId => tools.RunStep(3, planId, Cancellation));

        Assert.Equal("failed", third.GetProperty("status").GetString());
        // Step 3 sets the usage location and license of the call queue resource account.
        Assert.StartsWith("racq-", third.GetProperty("target").GetString());
        Assert.Contains("Wait 30 to 60 seconds", third.GetProperty("retryHint").GetString());
    }

    [Fact]
    public void RetryHint_IsGivenOnlyForErrorsADelayCanCause()
    {
        Assert.NotNull(ChangeGate.RetryHint("Failed to assign phone number: User 'x' lacks appropriate licenses to assign a 'DirectRouting' number"));
        Assert.NotNull(ChangeGate.RetryHint("Failed to create call queue: userId was not found"));
        Assert.Null(ChangeGate.RetryHint("no license available"));
        Assert.Null(ChangeGate.RetryHint(null));
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
    public async Task BulkSetup_AcceptsTheTemplateRowAndClaimsEachGroupByItsOwnId()
    {
        using var host = new McpTestHost(skipBackup: false).Connected();
        var tools = host.Tool<BulkTools>();
        var template = McpTestHost.Parse(tools.Template()).GetProperty("template").GetString()!;
        var csv = template + "fabrikam,empfang,@fabrikam.onmicrosoft.com,haupt,de-DE,W. Europe Standard Time,CH,+41441234568,DirectRouting,08:00,12:00,13:00,17:00\n";
        host.PowerShell.DefaultResult = new()
        {
            Output = "SUCCESS: ttgrp-contoso-hauptnummer created successfully with ID: 11111111-2222-3333-4444-555555555555\n" +
                     "SUCCESS: ttgrp-fabrikam-empfang created successfully with ID: 66666666-2222-3333-4444-555555555555\n"
        };

        var applied = await PlanAndApplyAsync(planId => tools.BulkSetup(csv, planId, cancellationToken: Cancellation));

        Assert.Equal("applied", applied.GetProperty("status").GetString());
        var ledger = host.Get<CreatedObjectLedger>();
        Assert.True(ledger.Contains(McpTestHost.TenantId, TenantObjectType.M365Group, "66666666-2222-3333-4444-555555555555"));
        Assert.Equal(
            "ttgrp-fabrikam-empfang",
            ledger.List(McpTestHost.TenantId).Single(item => item.Key == "66666666-2222-3333-4444-555555555555").DisplayName);
        Assert.True(ledger.Contains(McpTestHost.TenantId, TenantObjectType.CallQueue, "cq-fabrikam-empfang"));
    }

    [Fact]
    public async Task BulkSetup_RecordsObjectsOfFailedRunsThatALaterRunReuses()
    {
        // A new resource account is not visible for some seconds, so the first run fails after it
        // created objects, and the runs after it report those objects as reused.
        using var host = new McpTestHost(skipBackup: false).Connected();
        var tools = host.Tool<BulkTools>();
        var header = McpTestHost.Parse(tools.Template()).GetProperty("template").GetString()!.Split('\n')[0].TrimEnd('\r');
        var csv = header + "\nfabrikam,empfang,@fabrikam.onmicrosoft.com,haupt,de-DE,W. Europe Standard Time,CH,+41441234568,DirectRouting,08:00,12:00,13:00,17:00\n";
        const string group = "ttgrp-fabrikam-empfang";
        const string groupId = "66666666-2222-3333-4444-555555555555";
        const string racq = "racq-fabrikam-empfang@fabrikam.onmicrosoft.com";
        const string raaa = "raaa-fabrikam-haupt-empfang@fabrikam.onmicrosoft.com";
        const string queue = "cq-fabrikam-empfang";
        const string attendant = "aa-fabrikam-haupt-empfang";
        var ledger = host.Get<CreatedObjectLedger>();

        host.PowerShell.DefaultResult = new()
        {
            Output = "[1/1] Processing: fabrikam - empfang\n" +
                     $"SUCCESS: {group} created successfully with ID: {groupId}\n" +
                     $"SUCCESS: Resource account {racq} created successfully\n" +
                     "ERROR: Failed to update usage location: Resource does not exist\n" +
                     "ERROR: Failed to create call queue: userId was not found\n" +
                     $"SUCCESS: Resource account {raaa} created successfully\n" +
                     "ERROR: Failed to assign phone number: The user is not found\n" +
                     $"SUCCESS: Auto attendant {attendant} is ready\n"
        };
        var first = await PlanAndApplyAsync(planId => tools.BulkSetup(csv, planId, cancellationToken: Cancellation));
        Assert.Equal("failed", first.GetProperty("status").GetString());
        Assert.True(ledger.Contains(McpTestHost.TenantId, TenantObjectType.M365Group, groupId));
        Assert.True(ledger.Contains(McpTestHost.TenantId, TenantObjectType.ResourceAccount, racq));
        Assert.True(ledger.Contains(McpTestHost.TenantId, TenantObjectType.ResourceAccount, raaa));
        Assert.True(ledger.Contains(McpTestHost.TenantId, TenantObjectType.AutoAttendant, attendant));
        Assert.False(ledger.Contains(McpTestHost.TenantId, TenantObjectType.CallQueue, queue));
        Assert.False(ledger.Contains(McpTestHost.TenantId, TenantObjectType.PhoneNumber, "+41441234568"));

        var reused =
            "[1/1] Processing: fabrikam - empfang\n" +
            $"SUCCESS: {group} already exists. The existing group is used (ID: {groupId})\n" +
            $"SUCCESS: Resource account {racq} already exists. The existing account is used.\n" +
            $"SUCCESS: Resource account {raaa} already exists. The existing account is used.\n" +
            $"INFO: Auto attendant {attendant} already existed and was updated to the current Configuration.\n" +
            $"SUCCESS: Auto attendant {attendant} is ready\n";
        host.PowerShell.DefaultResult = new()
        {
            Output = reused +
                     $"SUCCESS: Call queue {queue} is ready and associated with {racq}\n" +
                     "ERROR: Failed to assign phone number: User lacks appropriate licenses\n"
        };
        var second = await PlanAndApplyAsync(planId => tools.BulkSetup(csv, planId, cancellationToken: Cancellation));
        Assert.Equal("failed", second.GetProperty("status").GetString());
        Assert.True(ledger.Contains(McpTestHost.TenantId, TenantObjectType.CallQueue, queue));
        Assert.False(ledger.Contains(McpTestHost.TenantId, TenantObjectType.PhoneNumber, "+41441234568"));

        host.PowerShell.DefaultResult = new()
        {
            Output = reused +
                     $"INFO: Call queue {queue} already existed and was updated to the current Configuration.\n" +
                     $"SUCCESS: Call queue {queue} is ready and associated with {racq}\n" +
                     "SUCCESS: Phone number assigned successfully\n"
        };
        var third = await PlanAndApplyAsync(planId => tools.BulkSetup(csv, planId, cancellationToken: Cancellation));
        Assert.Equal("applied", third.GetProperty("status").GetString());
        Assert.True(ledger.Contains(McpTestHost.TenantId, TenantObjectType.PhoneNumber, "+41441234568"));
        Assert.Equal(6, ledger.List(McpTestHost.TenantId).Count);
    }

    [Fact]
    public async Task BulkSetup_DoesNotRecordObjectsARunOnlyReused()
    {
        using var host = new McpTestHost(skipBackup: false).Connected();
        var tools = host.Tool<BulkTools>();
        var header = McpTestHost.Parse(tools.Template()).GetProperty("template").GetString()!.Split('\n')[0].TrimEnd('\r');
        var csv = header + "\nfabrikam,empfang,@fabrikam.onmicrosoft.com,haupt,de-DE,W. Europe Standard Time,CH,+41441234568,DirectRouting,08:00,12:00,13:00,17:00\n";
        host.PowerShell.DefaultResult = new()
        {
            Output = "[1/1] Processing: fabrikam - empfang\n" +
                     "SUCCESS: ttgrp-fabrikam-empfang already exists. The existing group is used (ID: 66666666-2222-3333-4444-555555555555)\n" +
                     "SUCCESS: Resource account racq-fabrikam-empfang@fabrikam.onmicrosoft.com already exists. The existing account is used.\n" +
                     "INFO: Call queue cq-fabrikam-empfang already existed and was updated to the current Configuration.\n" +
                     "SUCCESS: Call queue cq-fabrikam-empfang is ready and associated with racq-fabrikam-empfang@fabrikam.onmicrosoft.com\n" +
                     "SUCCESS: Resource account raaa-fabrikam-haupt-empfang@fabrikam.onmicrosoft.com already exists. The existing account is used.\n" +
                     "INFO: Auto attendant aa-fabrikam-haupt-empfang already existed and was updated to the current Configuration.\n" +
                     "SUCCESS: Auto attendant aa-fabrikam-haupt-empfang is ready\n"
        };

        var applied = await PlanAndApplyAsync(planId => tools.BulkSetup(csv, planId, cancellationToken: Cancellation));

        Assert.Equal("applied", applied.GetProperty("status").GetString());
        Assert.Empty(host.Get<CreatedObjectLedger>().List(McpTestHost.TenantId));
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

        var teamsTooEarly = McpTestHost.Parse(await tools.ConnectTeams(cancellationToken: Cancellation));
        Assert.False(teamsTooEarly.GetProperty("success").GetBoolean());

        Assert.True(McpTestHost.Parse(await tools.CheckModules(Cancellation)).GetProperty("success").GetBoolean());
        Assert.True(McpTestHost.Parse(await tools.ConnectTeams(cancellationToken: Cancellation)).GetProperty("success").GetBoolean());
        var graph = McpTestHost.Parse(await tools.ConnectGraph(cancellationToken: Cancellation));

        Assert.True(graph.GetProperty("success").GetBoolean());
        Assert.Equal("tenant-1", graph.GetProperty("status").GetProperty("tenantId").GetString());
        Assert.Equal("admin@contoso.com", graph.GetProperty("status").GetProperty("graphAccount").GetString());
        // The token never appears in a script.
        Assert.DoesNotContain(host.PowerShell.Executed, script => script.Contains("test-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connection_ReportsAnOpenSignInAndContinuesWithItOnTheNextCall()
    {
        using var host = new McpTestHost();
        var commands = host.Get<IPowerShellCommandService>();
        host.PowerShell.Respond(commands.GetConnectTeamsCommand(),
            "SUCCESS: Connected to Microsoft Teams\nConnected to tenant: Contoso (tenant-1)\n");
        var teamsSignIn = host.PowerShell.Hold(commands.GetConnectTeamsCommand());
        var graphSignIn = host.GraphAuthentication.Hold();
        host.Get<ISessionManager>().UpdateModulesChecked(true);
        var connection = new PhoneDesk.Operations.TenantConnectionService(
            host.Get<PhoneDesk.Operations.ITenantCommandRunner>(),
            commands,
            host.GraphAuthentication,
            host.Get<ISessionManager>(),
            host.Get<ITenantTopologyCache>(),
            host.Get<ITenantHealthCheckCache>())
        {
            SignInWait = TimeSpan.FromMilliseconds(150)
        };

        // The user has not signed in yet: the call returns instead of blocking.
        var waiting = await connection.ConnectTeamsAsync(Cancellation);
        Assert.False(waiting.Success);
        Assert.True(waiting.WaitingForSignIn);

        // While the Teams window is open nothing else may queue behind it.
        Assert.True((await connection.CheckModulesAsync(Cancellation)).WaitingForSignIn);
        Assert.True((await connection.ConnectGraphAsync(Cancellation)).WaitingForSignIn);

        // The user signs in; the next call continues with the same sign-in, not a second window.
        teamsSignIn.SetResult();
        var teams = await connection.ConnectTeamsAsync(Cancellation);
        Assert.True(teams.Success);
        Assert.Equal(1, host.PowerShell.Executed.Count(script => script == commands.GetConnectTeamsCommand()));

        Assert.True((await connection.ConnectGraphAsync(Cancellation)).WaitingForSignIn);
        graphSignIn.SetResult((true, "test-token", "admin@contoso.com", null));
        var graph = await connection.ConnectGraphAsync(Cancellation);
        Assert.True(graph.Success);
        Assert.Equal(1, host.GraphAuthentication.Calls);
        Assert.True(connection.IsReady);
    }

    private static async Task<JsonElement> PlanAndApplyAsync(Func<string?, Task<string>> call)
    {
        var plan = McpTestHost.Parse(await call(null));
        Assert.Equal("planned", plan.GetProperty("status").GetString());
        return McpTestHost.Parse(await call(plan.GetProperty("planId").GetString()));
    }
}
