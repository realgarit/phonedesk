using System.Management.Automation.Language;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhoneDesk.Mcp;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// Things the desktop app keeps right through its controls (dropdowns, minimum values, a selected
/// row) and that the MCP server must check itself, because an AI client sends free text.
/// </summary>
public sealed class McpParityTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static JsonElement Update(McpTestHost host, string path, object? value)
        => McpTestHost.Parse(host.Tool<ConfigurationTools>().Update(new Dictionary<string, JsonElement>
        {
            [path] = McpTestHost.Json(value)
        }));

    [Theory]
    [InlineData("autoAttendantTemplate.defaultAction", "transferToTarget", "TransferToTarget")]
    [InlineData("autoAttendantTemplate.afterHoursAction", "TransferToCallQueue", "TransferToVoicemail")]
    [InlineData("callQueueTemplate.overflowAction", "Forward", "Disconnect")]
    [InlineData("callQueueTemplate.noAgentAction", "Queue", "QueueCall")]
    [InlineData("callQueueTemplate.greetingType", "TTS", "TextToSpeech")]
    [InlineData("callQueueTemplate.musicOnHoldType", "audio", "AudioFile")]
    public void Configuration_ATextOutsideAFixedSetIsRefusedWithTheValidTexts(string path, string value, string expected)
    {
        using var host = new McpTestHost().Configured();
        var before = host.Get<ConfigurationStore>().ToJson();

        var result = Update(host, path, value);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains(path, result.GetProperty("error").GetString());
        Assert.Contains(expected, result.GetProperty("error").GetString());
        Assert.Equal(before, host.Get<ConfigurationStore>().ToJson());
    }

    [Fact]
    public void Configuration_AWaitingTimeUnderTheMinimumIsRefused()
    {
        using var host = new McpTestHost().Configured();

        var result = Update(host, "callQueueTemplate.timeoutThreshold", 10);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("30", result.GetProperty("error").GetString());
        Assert.Null(host.Get<ConfigurationStore>().Variables.CqTimeoutThreshold);
        Assert.True(Update(host, "callQueueTemplate.timeoutThreshold", 30).GetProperty("applied").GetBoolean());
    }

    [Fact]
    public void Configuration_AHolidayEndTimeWithoutAnEndDateIsRefused()
    {
        using var host = new McpTestHost().Configured();

        var result = Update(host, "holidayTemplate.series", new[] { new { date = "2026-12-24", time = "12:00:00", endTime = "18:00:00" } });

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("endDate", result.GetProperty("error").GetString());
        Assert.Empty(host.Get<ConfigurationStore>().Variables.HolidaySeries);

        var withBoth = Update(host, "holidayTemplate.series",
            new[] { new { date = "2026-12-24", time = "12:00:00", endDate = "2026-12-24", endTime = "18:00:00" } });
        Assert.True(withBoth.GetProperty("applied").GetBoolean(), withBoth.GetRawText());
    }

    [Fact]
    public async Task Configuration_AnActionWithoutItsTargetStopsProvisioningUntilTheTargetIsSet()
    {
        using var host = new McpTestHost().Connected().Configured();

        var result = Update(host, "callQueueTemplate.timeoutAction", "TransferToVoicemail");

        Assert.True(result.GetProperty("applied").GetBoolean());
        var validation = result.GetProperty("validation");
        Assert.False(validation.GetProperty("valid").GetBoolean());
        Assert.Contains(validation.GetProperty("problems").EnumerateArray(),
            problem => problem.GetProperty("message").GetString()!.Contains("callQueueTemplate.timeoutActionTarget"));

        foreach (var refused in new[]
                 {
                     await host.Tool<ProvisioningTools>().CreateCallQueue(cancellationToken: Cancellation),
                     await host.Tool<ProvisioningTools>().CreateAutoAttendant(cancellationToken: Cancellation),
                     await host.Tool<GuidedSetupTools>().RunStep(1, cancellationToken: Cancellation)
                 })
        {
            var parsed = McpTestHost.Parse(refused);
            Assert.False(parsed.GetProperty("success").GetBoolean());
            Assert.Contains("timeoutActionTarget", parsed.GetProperty("error").GetString());
        }

        Assert.False(McpTestHost.Parse(host.Tool<GuidedSetupTools>().Status()).GetProperty("configurationValid").GetBoolean());
        Assert.Empty(host.PowerShell.Executed);

        var fixedNow = Update(host, "callQueueTemplate.timeoutActionTarget", "11111111-2222-3333-4444-555555555555");
        Assert.True(fixedNow.GetProperty("validation").GetProperty("valid").GetBoolean());
        var plan = McpTestHost.Parse(await host.Tool<ProvisioningTools>().CreateCallQueue(cancellationToken: Cancellation));
        Assert.Equal("planned", plan.GetProperty("status").GetString());
        Assert.Contains("-TimeoutAction SharedVoicemail", plan.GetProperty("script").GetString());
    }

    [Fact]
    public async Task CreateAutoAttendant_RunsTheSameValidationAsGuidedSetup()
    {
        using var host = new McpTestHost().Connected().Configured();
        Update(host, "autoAttendantTemplate.defaultGreetingType", "TextToSpeech");

        var result = McpTestHost.Parse(await host.Tool<ProvisioningTools>().CreateAutoAttendant(cancellationToken: Cancellation));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("greeting", result.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        using var empty = new McpTestHost().Connected();
        var withoutConfiguration = McpTestHost.Parse(await empty.Tool<ProvisioningTools>().CreateAutoAttendant(cancellationToken: Cancellation));
        Assert.False(withoutConfiguration.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Configuration_AnUnknownLanguageTimeZoneOrCountryIsAWarningNotAnError()
    {
        using var host = new McpTestHost().Configured();

        var result = McpTestHost.Parse(host.Tool<ConfigurationTools>().Update(new Dictionary<string, JsonElement>
        {
            ["general.timeZoneId"] = McpTestHost.Json("Europe/Zurich"),
            ["general.usageLocation"] = McpTestHost.Json("Switzerland")
        }));

        Assert.True(result.GetProperty("applied").GetBoolean());
        var warnings = result.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()!).ToList();
        Assert.Contains(warnings, warning => warning.Contains("general.timeZoneId") && warning.Contains("W. Europe Standard Time"));
        Assert.Contains(warnings, warning => warning.Contains("general.usageLocation") && warning.Contains("CH"));

        using var clean = new McpTestHost().Configured();
        Assert.False(McpTestHost.Parse(clean.Tool<ConfigurationTools>().Get()).TryGetProperty("warnings", out _));
    }

    [Fact]
    public void ConfigurationOptions_GivesEveryFixedTextAndEachOneIsAccepted()
    {
        using var host = new McpTestHost().Configured();
        var options = McpTestHost.Parse(host.Tool<ConfigurationTools>().Options()).GetProperty("options").EnumerateArray().ToList();

        Assert.Equal(ConfigurationChecks.Options.Count, options.Count);
        foreach (var option in options.Where(option => option.GetProperty("closed").GetBoolean()))
        {
            foreach (var value in option.GetProperty("values").EnumerateArray())
            {
                // Every listed text can be stored; whether the choice is complete is a separate check.
                var result = Update(host, option.GetProperty("path").GetString()!, value.GetString());
                Assert.True(result.GetProperty("applied").GetBoolean(), result.GetRawText());
            }
        }
    }

    [Fact]
    public async Task CreateCallQueue_ChecksTheGroupOfTheSessionInFrontOfTheUnchangedScript()
    {
        using var host = new McpTestHost().Connected().Configured();
        var variables = host.Get<ConfigurationStore>().Variables;
        var builderScript = host.Get<IPowerShellCommandService>().GetCreateCallQueueCommand(variables);

        var plan = McpTestHost.Parse(await host.Tool<ProvisioningTools>().CreateCallQueue(cancellationToken: Cancellation));
        var script = plan.GetProperty("script").GetString()!;

        Assert.EndsWith(builderScript, script);
        var check = script[..^builderScript.Length];
        Assert.Contains("$global:m365groupId = $null", check);
        Assert.Contains("-ne 'ttgrp-contoso-reception'", check);
        Assert.Contains("ttgrp-contoso-reception", plan.GetProperty("summary").GetString());

        // Guided Setup step 4 plans the same script, and both parse as PowerShell.
        var progress = host.Get<GuidedSetupProgress>();
        foreach (var step in new[] { 1, 2, 3 })
        {
            progress.Get(step).Status = GuidedStepStatus.Skipped;
        }

        var stepPlan = McpTestHost.Parse(await host.Tool<GuidedSetupTools>().RunStep(4, cancellationToken: Cancellation));
        Assert.Equal(script, stepPlan.GetProperty("script").GetString());
        Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("o'brien", "o''brien")]
    [InlineData("a’b‘c", "a’’b‘‘c")]
    [InlineData("line\nbreak", "linebreak")]
    public void AgentGroupCheck_KeepsANameInsideItsQuotes(string name, string expected)
    {
        Assert.Equal(expected, AgentGroupCheck.SingleQuoted(name));
        Parser.ParseInput(AgentGroupCheck.InFrontOf("Write-Host 'done'", name), out _, out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task LicenseUsageLocationAndNumber_GoToAResourceAccountOnly()
    {
        using var host = new McpTestHost().Connected().Configured();
        var tools = host.Tool<ProvisioningTools>();

        foreach (var refused in new[]
                 {
                     await tools.AssignLicense("callQueue", "anna.muster@contoso.com", cancellationToken: Cancellation),
                     await tools.SetUsageLocation("callQueue", "anna.muster@contoso.com", cancellationToken: Cancellation),
                     await tools.AssignPhoneNumber("anna.muster@contoso.com", cancellationToken: Cancellation)
                 })
        {
            var parsed = McpTestHost.Parse(refused);
            Assert.False(parsed.GetProperty("success").GetBoolean());
            Assert.Contains("not a resource account", parsed.GetProperty("error").GetString());
        }

        // An account the tenant lists, the configured account (not in the tenant yet) and one this server created.
        host.Get<CreatedObjectLedger>().Record(McpTestHost.TenantId, TenantObjectType.ResourceAccount, "racq-new@contoso.com", "CreateResourceAccount");
        foreach (var upn in new string?[] { "RACQ-ONE@contoso.com", null, "racq-new@contoso.com" })
        {
            var plan = McpTestHost.Parse(await tools.AssignLicense("callQueue", upn, cancellationToken: Cancellation));
            Assert.Equal("planned", plan.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task UsageLocationAndPhoneNumber_MustHaveTheirFormat()
    {
        using var host = new McpTestHost().Connected().Configured();
        var tools = host.Tool<ProvisioningTools>();

        var location = McpTestHost.Parse(await tools.SetUsageLocation("callQueue", usageLocation: "Switzerland", cancellationToken: Cancellation));
        Assert.False(location.GetProperty("success").GetBoolean());
        Assert.Contains("two-letter", location.GetProperty("error").GetString());

        var number = McpTestHost.Parse(await tools.AssignPhoneNumber(phoneNumber: "044 123 45 67", cancellationToken: Cancellation));
        Assert.False(number.GetProperty("success").GetBoolean());
        Assert.Contains("E.164", number.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateResourceAccount_CompletesAUpnWithoutADomainAndRefusesAMalformedOne()
    {
        using var host = new McpTestHost().Connected().Configured();
        var tools = host.Tool<ProvisioningTools>();

        var plan = McpTestHost.Parse(await tools.CreateResourceAccount("callQueue", "racq-contoso-second", "Second", cancellationToken: Cancellation));
        Assert.Equal("racq-contoso-second@contoso.onmicrosoft.com", plan.GetProperty("target").GetString());

        foreach (var bad in new[] { "racq second@contoso.com", "racq@", "racq@contoso" })
        {
            var result = McpTestHost.Parse(await tools.CreateResourceAccount("callQueue", bad, "Second", cancellationToken: Cancellation));
            Assert.False(result.GetProperty("success").GetBoolean(), bad);
        }

        using var noDomain = new McpTestHost().Connected();
        var withoutDomain = McpTestHost.Parse(await noDomain.Tool<ProvisioningTools>()
            .CreateResourceAccount("callQueue", "racq-contoso-second", "Second", cancellationToken: Cancellation));
        Assert.Contains("msFallbackDomain", withoutDomain.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TenantDetails_AreReadAgainAfterAChangeOfThisSession()
    {
        using var host = new McpTestHost().Connected().Configured();
        var read = host.Tool<TenantReadTools>();

        var first = McpTestHost.Parse(await read.Details("documentation.schedules", cancellationToken: Cancellation));
        Assert.True(first.TryGetProperty("retrievedAtUtc", out _));
        var readsForOneSnapshot = host.PowerShell.Executed.Count;
        await read.Details("documentation.schedules", cancellationToken: Cancellation);
        Assert.Equal(readsForOneSnapshot, host.PowerShell.Executed.Count);

        var tools = host.Tool<ProvisioningTools>();
        var plan = McpTestHost.Parse(await tools.CreateM365Group(cancellationToken: Cancellation));
        await tools.CreateM365Group(planId: plan.GetProperty("planId").GetString(), cancellationToken: Cancellation);
        var afterChange = host.PowerShell.Executed.Count;

        await read.Details("documentation.schedules", cancellationToken: Cancellation);
        Assert.Equal(afterChange + readsForOneSnapshot, host.PowerShell.Executed.Count);
    }

    [Fact]
    public async Task Disconnect_ForgetsTheSnapshotOfTheSession()
    {
        using var host = new McpTestHost().Connected();
        await host.Tool<TenantReadTools>().Details(cancellationToken: Cancellation);
        Assert.NotNull(host.Get<TenantGateway>().LatestSnapshot);

        await host.Tool<SessionTools>().Disconnect(Cancellation);
        host.Connected();

        Assert.Null(host.Get<TenantGateway>().LatestSnapshot);
    }

    [Theory]
    [InlineData("+41 21", true)]
    [InlineData("zürich", true)]
    [InlineData("müller & söhne", true)]
    [InlineData("o'brien", true)]
    [InlineData("42", true)]
    [InlineData("phoneNumber", false)]
    [InlineData("nested", false)]
    [InlineData("\\u00FC", false)]
    public void TenantDetailsSearch_ComparesValuesNotJsonText(string search, bool expected)
    {
        var row = JsonNode.Parse(
            """{"phoneNumber":"+41 21 000","city":"Zürich","nested":{"owner":"Müller & Söhne"},"people":["O'Brien"],"count":42}""");

        Assert.Equal(expected, TenantReadTools.RowContains(row, search));
    }

    [Fact]
    public async Task Lists_FindAnObjectByItsIdAndReadOnInParts()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<TenantReadTools>();

        Assert.Equal(1, McpTestHost.Parse(await tools.ListResourceAccounts("ra-obj-2", cancellationToken: Cancellation)).GetProperty("total").GetInt32());
        Assert.Equal(1, McpTestHost.Parse(await tools.ListGroups("grp-1", cancellationToken: Cancellation)).GetProperty("total").GetInt32());
        Assert.Equal(1, McpTestHost.Parse(await tools.ListAutoAttendants("aa-id-1", cancellationToken: Cancellation)).GetProperty("total").GetInt32());
        Assert.Equal(1, McpTestHost.Parse(await tools.ListCallQueues("cq-id-1", cancellationToken: Cancellation)).GetProperty("total").GetInt32());

        var firstPart = McpTestHost.Parse(await tools.ListResourceAccounts(limit: 1, cancellationToken: Cancellation));
        Assert.True(firstPart.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, firstPart.GetProperty("nextOffset").GetInt32());

        var secondPart = McpTestHost.Parse(await tools.ListResourceAccounts(limit: 1, offset: 1, cancellationToken: Cancellation));
        Assert.False(secondPart.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, secondPart.GetProperty("returned").GetInt32());
        Assert.NotEqual(
            firstPart.GetProperty("rows")[0].GetProperty("objectId").GetString(),
            secondPart.GetProperty("rows")[0].GetProperty("objectId").GetString());

        var wrongKind = McpTestHost.Parse(await tools.ListResourceAccounts(kind: "1", cancellationToken: Cancellation));
        Assert.False(wrongKind.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task AuditHistory_RefusesAnUnknownOutcomeAndFiltersByDay()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<TenantReadTools>();
        await tools.Overview(cancellationToken: Cancellation);

        foreach (var wrong in new[] { "failed", "error", "canceled", "1" })
        {
            var result = McpTestHost.Parse(tools.AuditHistory(outcome: wrong));
            Assert.False(result.GetProperty("success").GetBoolean(), wrong);
            Assert.Contains("failure", result.GetProperty("error").GetString());
        }

        Assert.True(McpTestHost.Parse(tools.AuditHistory(outcome: "Success")).GetProperty("total").GetInt32() > 0);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.True(McpTestHost.Parse(tools.AuditHistory(from: today.AddDays(-1).ToString("yyyy-MM-dd"))).GetProperty("total").GetInt32() > 0);
        Assert.Equal(0, McpTestHost.Parse(tools.AuditHistory(to: today.AddDays(-2).ToString("yyyy-MM-dd"))).GetProperty("total").GetInt32());
        Assert.False(McpTestHost.Parse(tools.AuditHistory(from: "01.03.2026")).GetProperty("success").GetBoolean());
        Assert.False(McpTestHost.Parse(tools.AuditHistory(from: "2026-03-02", to: "2026-03-01")).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task HealthFinding_OnlyAFindingOfTheLastCheckCanBeHiddenOrShown()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<HealthAndBackupTools>();

        var beforeAnyRun = McpTestHost.Parse(tools.SetFindingSuppressed("anything", suppressed: true));
        Assert.False(beforeAnyRun.GetProperty("success").GetBoolean());
        Assert.Contains("run_health_check", beforeAnyRun.GetProperty("error").GetString());

        var report = McpTestHost.Parse(await tools.RunHealthCheck(cancellationToken: Cancellation));
        var finding = report.GetProperty("activeFindings")[0];
        var findingId = finding.GetProperty("findingId").GetString()!;

        foreach (var wrong in new[] { finding.GetProperty("ruleId").GetString()!, "Main AA", findingId.ToUpperInvariant() + "x" })
        {
            Assert.False(McpTestHost.Parse(tools.SetFindingSuppressed(wrong, suppressed: true)).GetProperty("success").GetBoolean(), wrong);
        }

        // A finding that is not hidden cannot be shown again.
        Assert.False(McpTestHost.Parse(tools.SetFindingSuppressed(findingId, suppressed: false)).GetProperty("success").GetBoolean());

        Assert.True(McpTestHost.Parse(tools.SetFindingSuppressed(" " + findingId + " ", suppressed: true)).GetProperty("success").GetBoolean());
        Assert.True(McpTestHost.Parse(tools.SetFindingSuppressed(findingId, suppressed: false)).GetProperty("success").GetBoolean());
        Assert.Empty(host.Get<ITenantHealthPreferencesStore>().Load(McpTestHost.TenantId).SuppressedFindingIds);
    }

    [Fact]
    public async Task ListBackups_ShowsEveryBackupSoAnOlderOneCanBeCompared()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<HealthAndBackupTools>();
        Assert.Equal(0, McpTestHost.Parse(tools.ListBackups()).GetProperty("backups").GetArrayLength());

        var directory = host.Get<McpEnvironment>().BackupDirectory(McpTestHost.TenantId);
        var newest = McpTestHost.Parse(await tools.BackupTenant(Cancellation)).GetProperty("backup").GetProperty("file").GetString()!;
        var older = Path.Combine(directory, "phonedesk-topology-20260101T080000Z.json");
        File.Copy(newest, older);

        var backups = McpTestHost.Parse(tools.ListBackups()).GetProperty("backups").EnumerateArray().ToList();
        Assert.Equal(2, backups.Count);
        Assert.Equal(newest, backups[0].GetProperty("file").GetString());
        Assert.Equal(older, backups[1].GetProperty("file").GetString());
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero), backups[1].GetProperty("takenUtc").GetDateTimeOffset());

        var compared = McpTestHost.Parse(await tools.CompareWithBackup(older, cancellationToken: Cancellation));
        Assert.Equal(older, compared.GetProperty("comparedFile").GetString());
        Assert.Equal(0, compared.GetProperty("offset").GetInt32());
    }

    [Fact]
    public async Task DeleteGroup_RefusesANameThatBelongsToAnotherGroupThanTheId()
    {
        using var host = new McpTestHost(allowDeleteExisting: true).Connected();
        var tools = host.Tool<DeleteTools>();

        var wrongName = McpTestHost.Parse(await tools.DeleteM365Group("grp-1", "pdtest-other", cancellationToken: Cancellation));
        Assert.False(wrongName.GetProperty("success").GetBoolean());
        Assert.Contains("Support Group", wrongName.GetProperty("error").GetString());

        var rightName = McpTestHost.Parse(await tools.DeleteM365Group("grp-1", "support group", cancellationToken: Cancellation));
        Assert.Equal("planned", rightName.GetProperty("status").GetString());
    }

    [Fact]
    public async Task BulkSetup_CanLeaveOutInvalidRowsAndSaysWhichOnes()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<BulkTools>();
        var template = McpTestHost.Parse(tools.Template()).GetProperty("template").GetString()!;
        var csv = template + "fabrikam,,@fabrikam.onmicrosoft.com,haupt,de-DE,W. Europe Standard Time,CH,+41441234568,DirectRouting,08:00,12:00,13:00,17:00\n";

        var stopped = McpTestHost.Parse(await tools.BulkSetup(csv, cancellationToken: Cancellation));
        Assert.False(stopped.GetProperty("success").GetBoolean());
        Assert.Contains("skipInvalidRows", stopped.GetProperty("error").GetString());

        var plan = McpTestHost.Parse(await tools.BulkSetup(csv, skipInvalidRows: true, cancellationToken: Cancellation));
        Assert.Equal("planned", plan.GetProperty("status").GetString());
        Assert.Contains("Set up 1 phone system(s)", plan.GetProperty("summary").GetString());
        Assert.Contains("Row 2", plan.GetProperty("summary").GetString());
        Assert.DoesNotContain("fabrikam", plan.GetProperty("script").GetString());

        var allInvalid = "Customer,CustomerGroupName,MsFallbackDomain\ncontoso,,contoso.onmicrosoft.com\n";
        Assert.False(McpTestHost.Parse(await tools.BulkSetup(allInvalid, skipInvalidRows: true, cancellationToken: Cancellation))
            .GetProperty("success").GetBoolean());
    }

    [Fact]
    public void SessionStatus_KeepsItsValuesAndAddsTheVersions()
    {
        using var host = new McpTestHost().Connected();

        var status = McpTestHost.Parse(host.Tool<SessionTools>().SessionStatus());

        Assert.True(status.GetProperty("teamsConnected").GetBoolean());
        Assert.Equal(McpTestHost.TenantId, status.GetProperty("tenantId").GetString());
        var versions = status.GetProperty("versions");
        foreach (var name in new[] { "server", "microsoftTeamsModule", "microsoftGraphModules", "powerShellSdk" })
        {
            Assert.False(string.IsNullOrWhiteSpace(versions.GetProperty(name).GetString()), name);
        }
    }
}
