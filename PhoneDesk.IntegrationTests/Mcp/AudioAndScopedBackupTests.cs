using System.Management.Automation.Language;
using System.Text.Json;
using PhoneDesk.Mcp;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// Audio files are uploaded for the application that plays them, and a holiday change can run on a
/// backup of only the objects it touches when the full tenant backup cannot be taken.
/// </summary>
public sealed class AudioAndScopedBackupTests
{
    private const string AaId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Y2026 = "11111111-0000-0000-0000-000000000001";
    private const string Y2027 = "22222222-0000-0000-0000-000000000002";
    private const string Flow2026 = "f1111111-0000-0000-0000-000000000001";
    private const string AudioId = "a0a0a0a0-0000-0000-0000-00000000000a";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void ImportScript_UploadsForTheNamedApplicationAndTheCallQueueScriptIsUnchanged()
    {
        using var host = new McpTestHost();
        var commands = host.Get<IPowerShellCommandService>();
        // Built for the system the test runs on: a Windows path has no file name part on Linux and macOS.
        var path = Path.Combine(Path.GetTempPath(), "audio", "weihnachten.wav");

        var callQueue = commands.GetImportAudioFileCommand(path);
        var attendant = commands.GetImportAudioFileCommand(path, AudioFiles.AutoAttendant);

        // The script without an application is the one that was always sent.
        Assert.Equal(callQueue, commands.GetImportAudioFileCommand(path, AudioFiles.CallQueue));
        Assert.Contains("# Import the audio file (using HuntGroup ApplicationId for Call Queues)", callQueue);
        Assert.Contains("Import-CsOnlineAudioFile -ApplicationId HuntGroup -FileName 'weihnachten.wav'", callQueue);
        Assert.Contains("Import-CsOnlineAudioFile -ApplicationId OrgAutoAttendant -FileName 'weihnachten.wav'", attendant);
        Assert.DoesNotContain("HuntGroup", attendant);
        Parser.ParseInput(attendant, out _, out var errors);
        Assert.Empty(errors);
        Assert.Throws<ArgumentException>(() => commands.GetImportAudioFileCommand(path, "Teams; Remove-Item"));
    }

    [Theory]
    [InlineData("autoAttendant", "OrgAutoAttendant")]
    [InlineData("OrgAutoAttendant", "OrgAutoAttendant")]
    [InlineData("call queue", "HuntGroup")]
    [InlineData("huntgroup", "HuntGroup")]
    public void Application_IsReadFromTheTeamsNameOrThePlainName(string value, string expected)
        => Assert.Equal(expected, AudioFiles.NormalizeApplication(value));

    [Fact]
    public void ListScript_ReturnsEveryFileWithItsIdAlsoWhenTwoHaveTheSameName()
    {
        var output = McpHolidayCallHandlingTests.RunPowerShell(
            """
            function global:Get-CsOnlineAudioFile {
                [CmdletBinding()] param([string]$ApplicationId)
                if ($ApplicationId -ne 'OrgAutoAttendant') { throw "wrong application" }
                [pscustomobject]@{ Id = 'a0a0a0a0-0000-0000-0000-00000000000a'; FileName = 'weihnachten.wav'; MarkedForDeletion = $false }
                [pscustomobject]@{ Id = 'b0b0b0b0-0000-0000-0000-00000000000b'; FileName = 'weihnachten.wav'; MarkedForDeletion = $true }
            }
            """,
            AudioFiles.GetListCommand("autoAttendant"));

        var files = AudioFiles.Parse(output);
        Assert.Equal(2, files.Count);
        Assert.All(files, file => Assert.Equal("weihnachten.wav", file.FileName));
        Assert.Equal(2, files.Select(file => file.Id).Distinct().Count());
        Assert.True(files[1].MarkedForDeletion);
        Assert.Equal("OrgAutoAttendant", files[0].Application);
    }

    [Fact]
    public async Task ImportTool_NamesTheApplicationReusesTheSameContentAndUploadsOtherContentWithTheSameName()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<ProvisioningTools>();
        var path = Path.Combine(host.DataDirectory, "weihnachten.wav");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4 }, Cancellation);

        var missing = McpTestHost.Parse(await tools.ImportAudioFile(path, "teams", cancellationToken: Cancellation));
        Assert.Contains("is not an audio application", missing.GetProperty("error").GetString());

        var planned = McpTestHost.Parse(await tools.ImportAudioFile(path, "autoAttendant", cancellationToken: Cancellation));
        var plan = planned.GetProperty("result");
        Assert.Equal("OrgAutoAttendant", planned.GetProperty("application").GetString());
        Assert.Contains("for auto attendants (application OrgAutoAttendant)", plan.GetProperty("summary").GetString());
        Assert.Contains(planned.GetProperty("file").GetProperty("sha256").GetString()!, plan.GetProperty("summary").GetString());
        var script = plan.GetProperty("script").GetString()!;
        Assert.Contains("-ApplicationId OrgAutoAttendant", script);

        host.PowerShell.Respond(script, "SUCCESS: Audio file imported successfully\nAUDIOFILEID: " + AudioId);
        var applied = McpTestHost.Parse(await tools.ImportAudioFile(
            path, "autoAttendant", planId: plan.GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Equal(AudioId, applied.GetProperty("audioFileId").GetString());

        // The same content for the same application: the ID comes back and nothing is uploaded.
        var again = McpTestHost.Parse(await tools.ImportAudioFile(path, "OrgAutoAttendant", cancellationToken: Cancellation));
        Assert.Equal("alreadyUploaded", again.GetProperty("status").GetString());
        Assert.Equal(AudioId, again.GetProperty("audioFileId").GetString());

        // The same content for a call queue is another upload: a file works only for its application.
        var forQueue = McpTestHost.Parse(await tools.ImportAudioFile(path, "callQueue", cancellationToken: Cancellation));
        Assert.Equal("planned", forQueue.GetProperty("result").GetProperty("status").GetString());
        Assert.Contains("-ApplicationId HuntGroup", forQueue.GetProperty("result").GetProperty("script").GetString());

        // The same file name with other content is a new upload, and a plan is for one content.
        var replacement = McpTestHost.Parse(await tools.ImportAudioFile(path, "autoAttendant", forceNewUpload: true, cancellationToken: Cancellation));
        await File.WriteAllBytesAsync(path, new byte[] { 9, 9, 9 }, Cancellation);
        var stale = McpTestHost.Parse(await tools.ImportAudioFile(
            path, "autoAttendant", forceNewUpload: true,
            planId: replacement.GetProperty("result").GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Contains("changed after this plan was made", stale.GetProperty("error").GetString());
        var other = McpTestHost.Parse(await tools.ImportAudioFile(path, "autoAttendant", cancellationToken: Cancellation));
        Assert.Equal("planned", other.GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public async Task HolidayChange_RunsOnAScopedBackupWhenTheFullBackupFailsAndNotWithoutOne()
    {
        var state = State(true);
        using var host = new McpTestHost(skipBackup: false).Connected();
        var commands = host.Get<IPowerShellCommandService>();
        // A warning in the tenant read stops the full backup, as an agentless call queue does.
        host.PowerShell.Respond(commands.GetRetrieveTenantTopologyCommand(), "WARNING: partial read");
        Respond(host, state);
        var tools = host.Tool<HolidayChangeTools>();
        var ranges = new[] { "2027-12-24 12:00 to 2027-12-25 00:00", "2027-12-25" };

        var planned = McpTestHost.Parse(await tools.EditHolidayScheduleDates(Y2027, ranges, cancellationToken: Cancellation));
        var script = planned.GetProperty("plan").GetProperty("script").GetString()!;
        host.PowerShell.Respond(script, "SUCCESS: Schedule 'Feiertage 2027' has the new dates");
        var applied = McpTestHost.Parse(await tools.EditHolidayScheduleDates(
            Y2027, ranges, planId: planned.GetProperty("plan").GetProperty("planId").GetString(), cancellationToken: Cancellation));

        Assert.Contains(script, host.PowerShell.Executed);
        Assert.StartsWith("failed:", applied.GetProperty("fullBackup").GetString());
        Assert.Contains("could not be taken", applied.GetProperty("backupNote").GetString());
        var file = applied.GetProperty("backupFile").GetString()!;
        Assert.Contains("phonedesk-holiday-backup-", file);
        var document = HolidayBackup.Deserialize(await File.ReadAllTextAsync(file, Cancellation));
        Assert.Equal("Main AA", Assert.Single(document.AutoAttendants).Name);
        Assert.Contains(document.Schedules, schedule => schedule.Id == Y2027 && schedule.Windows.Count == 1);
        Assert.Equal(1, applied.GetProperty("scopedBackup").GetProperty("callFlows").GetInt32());

        // The explicit backup tool reports what it captured.
        var explicitBackup = McpTestHost.Parse(await tools.BackupHolidayCallHandling(new[] { AaId }, Cancellation));
        Assert.Equal(1, explicitBackup.GetProperty("backup").GetProperty("autoAttendants").GetInt32());
        Assert.Equal(2, explicitBackup.GetProperty("backup").GetProperty("schedules").GetInt32());

        // A change that has no scoped backup still stops when the full backup fails.
        var group = McpTestHost.Parse(await host.Tool<ProvisioningTools>().LookupOrCreateForTest(host, Cancellation));
        Assert.Contains("backup failed", group.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HolidayChange_DoesNotRunWhenTheScopedBackupMissesAnObject()
    {
        using var host = new McpTestHost(skipBackup: false).Connected();
        host.PowerShell.Respond(host.Get<IPowerShellCommandService>().GetRetrieveTenantTopologyCommand(), "WARNING: partial read");
        Respond(host, State(true));
        var tools = host.Tool<HolidayChangeTools>();
        var ranges = new[] { "2027-12-24" };

        var planned = McpTestHost.Parse(await tools.EditHolidayScheduleDates(Y2027, ranges, cancellationToken: Cancellation));
        var script = planned.GetProperty("plan").GetProperty("script").GetString()!;

        // The read for the backup returns the auto attendant without the schedule the change touches.
        host.PowerShell.Respond(HolidayCallHandling.GetReadCommand(new[] { AaId }, true, false), State(false));
        var refused = McpTestHost.Parse(await tools.EditHolidayScheduleDates(
            Y2027, ranges, planId: planned.GetProperty("plan").GetProperty("planId").GetString(), cancellationToken: Cancellation));

        Assert.False(refused.GetProperty("success").GetBoolean());
        Assert.Contains("The backup of the objects this change touches failed too", refused.GetProperty("error").GetString());
        Assert.Contains("schedule " + Y2027, refused.GetProperty("error").GetString());
        Assert.DoesNotContain(script, host.PowerShell.Executed);
    }

    private static void Respond(McpTestHost host, string state)
    {
        foreach (var ids in new string[]?[] { null, new[] { AaId } })
        {
            foreach (var include in new[] { true, false })
            {
                foreach (var check in new[] { true, false })
                {
                    host.PowerShell.Respond(HolidayCallHandling.GetReadCommand(ids, include, check), state);
                }
            }
        }
    }

    private static string State(bool withTarget)
    {
        var schedules = new List<object>
        {
            new { id = Y2026, name = "Feiertage 2026", type = "Fixed", windows = new[] { new { start = "2026-12-25T00:00:00", end = "2026-12-26T00:00:00" } }, usedBy = new[] { AaId } }
        };
        var links = new List<object> { new { type = "Holiday", scheduleId = Y2026, callFlowId = Flow2026, enabled = true, priority = 0 } };
        if (withTarget)
        {
            schedules.Add(new { id = Y2027, name = "Feiertage 2027", type = "Fixed", windows = new[] { new { start = "2027-12-25T00:00:00", end = "2027-12-26T00:00:00" } }, usedBy = new[] { AaId } });
            links.Add(new { type = "Holiday", scheduleId = Y2027, callFlowId = Flow2026, enabled = true, priority = 0 });
        }

        return HolidayCallHandling.JsonMarker + JsonSerializer.Serialize(new
        {
            autoAttendants = new[]
            {
                new
                {
                    id = AaId,
                    name = "Main AA",
                    authorizedUsers = Array.Empty<string>(),
                    callFlows = new[]
                    {
                        new
                        {
                            id = Flow2026,
                            name = "flow",
                            greetings = Array.Empty<object>(),
                            menuName = "menu",
                            menuPrompts = Array.Empty<object>(),
                            options = Array.Empty<object>(),
                            forceListenMenu = false,
                            dialByName = false
                        }
                    },
                    associations = links
                }
            },
            schedules,
            missingAuthorizedUsers = Array.Empty<object>(),
            uncheckedAuthorizedUsers = Array.Empty<object>(),
            disabledAuthorizedUsers = Array.Empty<object>()
        }) + "\nSUCCESS: Holiday call handling retrieved";
    }
}

internal static class ProvisioningToolsTestExtensions
{
    /// <summary>Plans and applies the creation of the agent group, a change without a scoped backup.</summary>
    public static async Task<string> LookupOrCreateForTest(this ProvisioningTools tools, McpTestHost host, CancellationToken cancellationToken)
    {
        host.Configured();
        var planned = McpTestHost.Parse(await tools.CreateM365Group(cancellationToken: cancellationToken));
        return await tools.CreateM365Group(planId: planned.GetProperty("planId").GetString(), cancellationToken: cancellationToken);
    }
}
