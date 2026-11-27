using System.Management.Automation.Language;
using System.Text.Json;
using PhoneDesk.Mcp;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Models;
using PhoneDesk.Operations;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// The run journal: what a run created, the rollback of exactly that, and resuming a run that did
/// not finish. The identity read runs in a real PowerShell engine against stand-ins.
/// </summary>
public sealed class RunJournalTests
{
    private const string GroupId = "11111111-2222-3333-4444-555555555555";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static readonly DateTime Now = new(2027, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private static RunRecord RecordWith(IPhoneManagerVariables v, out RunObject group, out RunObject account, out RunObject queue)
    {
        var record = RunJournal.Start(RunKind.GuidedSetup, "test", "tenant-1", "Contoso", "contoso - reception", Now);
        group = RunJournal.Record(record, RunObjectType.M365Group, v.M365Group, 1, $"SUCCESS: {v.M365Group} created successfully with ID: {GroupId}", Now)!;
        account = RunJournal.Record(record, RunObjectType.ResourceAccount, v.RacqUPN, 2, $"SUCCESS: Resource account {v.RacqUPN} created successfully", Now)!;
        queue = RunJournal.Record(record, RunObjectType.CallQueue, v.CqDisplayName, 4, $"SUCCESS: Call queue {v.CqDisplayName} is ready", Now)!;
        return record;
    }

    [Fact]
    public void OnlyAnObjectWithItsOwnCreatedLineBelongsToTheRun()
    {
        using var host = new McpTestHost().Configured();
        var v = host.Get<ConfigurationStore>().Variables;
        var record = RecordWith(v, out var group, out var account, out var queue);

        Assert.Equal(GroupId, group.ObjectId);
        Assert.Equal(string.Empty, account.ObjectId);
        // A script that used what was there says so: such an object is never the run's.
        Assert.Null(RunJournal.Record(record, RunObjectType.AutoAttendant, v.AaDisplayName, 7,
            $"Auto attendant {v.AaDisplayName} already existed\nSUCCESS: Auto attendant {v.AaDisplayName} is ready", Now));
        Assert.Null(RunJournal.Record(record, RunObjectType.ResourceAccount, v.RaaaUPN, 5, $"SUCCESS: Resource account {v.RaaaUPN} already exists", Now));
        Assert.Null(RunJournal.Record(record, RunObjectType.M365Group, "another-group", 1, $"SUCCESS: {v.M365Group} created successfully with ID: {GroupId}", Now));
        // The same step twice records the object once.
        Assert.Same(queue, RunJournal.Record(record, RunObjectType.CallQueue, v.CqDisplayName, 4, $"SUCCESS: Call queue {v.CqDisplayName} is ready", Now));
        Assert.Equal(3, record.Objects.Count);

        // What points to an object goes before the object.
        Assert.Equal(
            new[] { RunObjectType.CallQueue, RunObjectType.ResourceAccount, RunObjectType.M365Group },
            RunJournal.RollbackOrder(record).Select(item => item.Type));

        // The record survives the file.
        var store = new FileRunJournalStore(Path.Combine(host.DataDirectory, "journal"));
        store.Save(record);
        var read = store.Find(record.Id)!;
        Assert.Equal(RunState.Running, read.State);
        Assert.Equal(GroupId, read.Objects.Single(item => item.Type == RunObjectType.M365Group).ObjectId);
        Assert.True(read.CanRollBack);
    }

    [Fact]
    public void Rollback_RemovesOnlyWhatTheTenantStillShowsAsTheSameObject()
    {
        using var host = new McpTestHost().Configured();
        var v = host.Get<ConfigurationStore>().Variables;
        var commands = host.Get<IPowerShellCommandService>();
        var record = RecordWith(v, out _, out var account, out var queue);
        // A phone number is never an object of a run: an assignment does not prove the run added the number.
        Assert.Null(RunJournal.ObjectOfStep(GuidedSetupStep.LicenseAutoAttendantResourceAccount, v));
        Assert.DoesNotContain(RunJournal.ObjectsOfRow(v), item => item.Name == v.RaaAnr);

        // The IDs are read while the session knows which objects they are.
        var missing = record.Objects.Where(item => item.ObjectId.Length == 0).ToList();
        var script = commands.GetRunIdentityCommand(missing);
        Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);
        Assert.DoesNotContain(v.RacqUPN, script);
        var tenant = Tenant(v, queueIds: "'cq-1'", accountId: "account-1", groupIds: $"'{GroupId}'");
        Assert.True(RunJournal.StoreIds(missing, RunJournal.ParseIdentity(McpHolidayCallHandlingTests.RunPowerShell(tenant, script))));
        Assert.Equal("account-1", account.ObjectId);
        Assert.Equal("cq-1", queue.ObjectId);

        RollbackPlan Plan(string fakeTenant)
        {
            var ordered = RunJournal.RollbackOrder(record);
            return RunJournal.Assess(ordered, RunJournal.ParseIdentity(
                McpHolidayCallHandlingTests.RunPowerShell(fakeTenant, commands.GetRunIdentityCommand(ordered))));
        }

        // Everything is as the run left it: all three are removed.
        var all = Plan(tenant);
        Assert.All(all.Items, item => Assert.Equal(RollbackAction.Delete, item.Action));
        Assert.Equal(
            new[] { RunObjectType.CallQueue, RunObjectType.ResourceAccount, RunObjectType.M365Group },
            all.Items.Select(item => item.Object.Type));

        // Someone replaced the call queue, a second group got the name, and the account is gone.
        var changed = Plan(Tenant(v, queueIds: "'cq-other'", accountId: null, groupIds: $"'{GroupId}', 'another'"));
        var byType = changed.Items.ToDictionary(item => item.Object.Type, item => item);
        Assert.Equal(RunJournal.ReasonOtherId, byType[RunObjectType.CallQueue].Code);
        Assert.Equal(RollbackAction.AlreadyGone, byType[RunObjectType.ResourceAccount].Action);
        Assert.Equal(RunJournal.ReasonAmbiguous, byType[RunObjectType.M365Group].Code);
        Assert.False(changed.HasWork);

        // An object recorded without its ID is never removed by its name alone.
        queue.ObjectId = string.Empty;
        Assert.Equal(RunJournal.ReasonNoId, Plan(tenant).Items.Single(item => item.Object.Type == RunObjectType.CallQueue).Code);
        queue.ObjectId = "cq-1";

        // The script has each delete script of its own page behind a marker, in order.
        var rollback = RunJournal.GetRollbackCommand(all, commands);
        Parser.ParseInput(rollback, out _, out var rollbackErrors);
        Assert.Empty(rollbackErrors);
        Assert.Contains(commands.GetRemoveCallQueueCommand(v.CqDisplayName), rollback);
        Assert.Contains(commands.GetRemoveM365GroupCommand(GroupId, v.M365Group), rollback);
        Assert.True(rollback.IndexOf(RunJournal.StepMarker + "0", StringComparison.Ordinal) < rollback.IndexOf(RunJournal.StepMarker + "2", StringComparison.Ordinal));

        // The result is read per object: one failed delete leaves that object, and the record stays open.
        var outcomes = RunJournal.ApplyOutcome(all,
            $"{RunJournal.StepMarker}0\nSUCCESS: Call queue removed\n{RunJournal.StepMarker}1\nERROR: Failed to remove the resource account: denied\n" +
            $"{RunJournal.StepMarker}2\nSUCCESS: Group removed\n", Now);
        Assert.Equal(new[] { true, false, true }, outcomes.Select(outcome => outcome.Removed));
        RunJournal.CloseAfterRollback(record, Now);
        Assert.NotEqual(RunState.RolledBack, record.State);
        Assert.True(record.CanRollBack);
        account.Removed = true;
        RunJournal.CloseAfterRollback(record, Now);
        Assert.Equal(RunState.RolledBack, record.State);
    }

    [Fact]
    public async Task Server_RecordsARunResumesItInANewSessionAndRollsItBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "phonedesk-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            string runId;
            string group;
            using (var host = new McpTestHost(dataDirectory: directory, keepDataDirectory: true).Connected().Configured())
            {
                group = host.Get<ConfigurationStore>().Variables.M365Group;
                host.PowerShell.DefaultResult = new() { Output = $"SUCCESS: {group} created successfully with ID: {GroupId}" };
                var tools = host.Tool<GuidedSetupTools>();
                Assert.Equal("applied", (await PlanAndApplyAsync(planId => tools.RunStep(1, planId, Cancellation))).GetProperty("status").GetString());

                // Step 2 fails: the run is open, with the group it created.
                host.PowerShell.DefaultResult = new()
                {
                    Output = "ERROR: Insufficient privileges",
                    HadErrors = true,
                    Errors = new[] { new PowerShellErrorInfo { Message = "Insufficient privileges" } }
                };
                Assert.Equal("failed", (await PlanAndApplyAsync(planId => tools.RunStep(2, planId, Cancellation))).GetProperty("status").GetString());

                var listed = McpTestHost.Parse(host.Tool<RunTools>().List(onlyOpen: true)).GetProperty("runs")[0];
                runId = listed.GetProperty("id").GetString()!;
                Assert.Equal("failed", listed.GetProperty("state").GetString());
                Assert.Equal(2, listed.GetProperty("failedStep").GetInt32());
                Assert.Equal(group, listed.GetProperty("objects")[0].GetProperty("name").GetString());
            }

            // A new session: nothing is completed until the run is taken up again.
            using (var host = new McpTestHost(dataDirectory: directory, keepDataDirectory: true).Connected().Configured())
            {
                var tools = host.Tool<GuidedSetupTools>();
                Assert.Equal(1, McpTestHost.Parse(tools.Status()).GetProperty("nextStep").GetInt32());
                var resumed = McpTestHost.Parse(tools.Resume());
                Assert.True(resumed.TryGetProperty("restoredSteps", out _), resumed.GetRawText());
                Assert.Equal(new[] { 1 }, resumed.GetProperty("restoredSteps").EnumerateArray().Select(item => item.GetInt32()));
                Assert.Equal(2, resumed.GetProperty("nextStep").GetInt32());

                // The rollback is planned from a read of the tenant, and runs only with the plan.
                var runs = host.Tool<RunTools>();
                var record = host.Get<RunJournalService>().Find(runId)!;
                var ordered = RunJournal.RollbackOrder(record);
                var identity = host.Get<IPowerShellCommandService>().GetRunIdentityCommand(ordered);
                host.PowerShell.Respond(identity, RunJournal.IdentityMarker + JsonSerializer.Serialize(new[] { new { index = 0, ids = new[] { GroupId } } }));
                var planned = McpTestHost.Parse(await runs.Rollback(runId, cancellationToken: Cancellation));
                Assert.Equal("planned", planned.GetProperty("status").GetString());
                Assert.Equal("delete", planned.GetProperty("items")[0].GetProperty("action").GetString());

                host.PowerShell.DefaultResult = new() { Output = RunJournal.StepMarker + "0" + Environment.NewLine + "SUCCESS: M365 group removed" };
                var applied = McpTestHost.Parse(await runs.Rollback(runId, planned.GetProperty("planId").GetString(), Cancellation));
                Assert.Equal("applied", applied.GetProperty("status").GetString());
                Assert.True(applied.GetProperty("outcome")[0].GetProperty("removed").GetBoolean());
                Assert.Equal("rolledBack", applied.GetProperty("run").GetProperty("state").GetString());
                Assert.Equal(0, McpTestHost.Parse(runs.List(onlyOpen: true)).GetProperty("runs").GetArrayLength());

                // A run of another tenant is not touched.
                record.TenantId = "another-tenant";
                record.State = RunState.Failed;
                host.Get<IRunJournalStore>().Save(record);
                Assert.Contains("another tenant", McpTestHost.Parse(await runs.Rollback(runId, cancellationToken: Cancellation)).GetProperty("error").GetString());
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<JsonElement> PlanAndApplyAsync(Func<string?, Task<string>> call)
    {
        var plan = McpTestHost.Parse(await call(null));
        Assert.Equal("planned", plan.GetProperty("status").GetString());
        return McpTestHost.Parse(await call(plan.GetProperty("planId").GetString()));
    }

    /// <summary>A tenant that holds what the arguments say for the objects of the configuration.</summary>
    private static string Tenant(IPhoneManagerVariables v, string queueIds, string? accountId, string groupIds)
        => TenantTemplate
            .Replace("__GROUP__", v.M365Group, StringComparison.Ordinal)
            .Replace("__GROUPIDS__", groupIds, StringComparison.Ordinal)
            .Replace("__UPN__", v.RacqUPN, StringComparison.Ordinal)
            .Replace("__ACCOUNT__", accountId ?? string.Empty, StringComparison.Ordinal)
            .Replace("__QUEUE__", v.CqDisplayName, StringComparison.Ordinal)
            .Replace("__QUEUEIDS__", queueIds, StringComparison.Ordinal);

    private const string TenantTemplate = """
        function Get-MgGroup { [CmdletBinding()] param($Filter, [switch]$All)
            if ($Filter -eq "displayName eq '__GROUP__'") { @(__GROUPIDS__) | ForEach-Object { [pscustomobject]@{ Id = $_ } } }
        }
        function Get-MgUser { [CmdletBinding()] param($Filter, $Property)
            if ('__ACCOUNT__' -and $Filter -eq "userPrincipalName eq '__UPN__'") { [pscustomobject]@{ Id = '__ACCOUNT__'; UserPrincipalName = '__UPN__' } }
        }
        function Get-CsCallQueue { [CmdletBinding()] param($NameFilter)
            if ($NameFilter -eq '__QUEUE__') { @(__QUEUEIDS__) | ForEach-Object { [pscustomobject]@{ Name = '__QUEUE__'; Identity = $_ } } }
        }
        function Get-CsAutoAttendant { [CmdletBinding()] param($NameFilter) }
        """;
}
