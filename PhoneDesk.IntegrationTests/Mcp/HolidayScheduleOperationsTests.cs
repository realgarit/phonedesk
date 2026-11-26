using System.Management.Automation.Language;
using System.Text.Json;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Operations;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// Changes to holiday schedules that exist: other dates, another greeting, retiring, and one
/// schedule for several auto attendants. The scripts run in a real PowerShell engine against
/// stand-ins for the Teams cmdlets; the tools run against a fake tenant.
/// </summary>
public sealed class HolidayScheduleOperationsTests
{
    private const string AaId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string SecondAa = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string Y2026 = "11111111-0000-0000-0000-000000000001";
    private const string Y2027 = "22222222-0000-0000-0000-000000000002";
    private const string Y2020 = "33333333-0000-0000-0000-000000000003";
    private const string Hours = "55555555-0000-0000-0000-000000000005";
    private const string Flow2026 = "f1111111-0000-0000-0000-000000000001";
    private const string Flow2020 = "f3333333-0000-0000-0000-000000000003";
    private const string NewFlow = "f2222222-0000-0000-0000-000000000002";
    private const string Audio = "a0a0a0a0-0000-0000-0000-00000000000a";

    private static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The cmdlets the change scripts need beside the ones of the read and copy scripts.</summary>
    private const string ChangeCmdlets = """
        function global:New-CsOnlineDateTimeRange {
            [CmdletBinding()] param([string]$Start, [string]$End)
            $pdFormat = 'd/M/yyyy H:mm'
            [pscustomobject]@{
                Start = [datetime]::ParseExact($Start, $pdFormat, [cultureinfo]::InvariantCulture)
                End = [datetime]::ParseExact($End, $pdFormat, [cultureinfo]::InvariantCulture)
            }
        }
        function global:Set-CsOnlineSchedule {
            [CmdletBinding()] param($Instance)
            $global:pdFakeSchedules = @($global:pdFakeSchedules | ForEach-Object { if ($_.Id -eq $Instance.Id) { $Instance } else { $_ } })
        }
        function global:Remove-CsOnlineSchedule {
            [CmdletBinding()] param([string]$Id)
            $global:pdFakeSchedules = @($global:pdFakeSchedules | Where-Object { $_.Id -ne $Id })
            if ($global:pdFakeRemoveThrows) { throw 'Object reference not set to an instance of an object.' }
        }
        function global:New-CsAutoAttendantPrompt {
            [CmdletBinding()] param([string]$TextToSpeechPrompt, $AudioFilePrompt)
            if ($AudioFilePrompt) { [pscustomobject]@{ ActiveType = 'AudioFile'; TextToSpeechPrompt = $null; AudioFilePrompt = $AudioFilePrompt } }
            else { [pscustomobject]@{ ActiveType = 'TextToSpeech'; TextToSpeechPrompt = $TextToSpeechPrompt; AudioFilePrompt = $null } }
        }
        function global:Get-CsOnlineAudioFile {
            [CmdletBinding()] param([string]$ApplicationId, [string]$Identity)
            if ($ApplicationId -ne 'OrgAutoAttendant') { throw "wrong application $ApplicationId" }
            [pscustomobject]@{ Id = $Identity; FileName = 'weihnachten.wav' }
        }
        # A save of the auto attendant updates which schedules it uses, as Teams does.
        function global:Set-CsAutoAttendant {
            [CmdletBinding()] param($Instance)
            $global:pdFakeAa = $Instance
            $global:pdFakeSaves++
            foreach ($pdFakeSchedule in $global:pdFakeSchedules) {
                $pdFakeUsed = @($Instance.CallHandlingAssociations | Where-Object { $_.ScheduleId -eq $pdFakeSchedule.Id })
                $pdFakeSchedule.AssociatedConfigurationIds = if ($pdFakeUsed.Count -gt 0) { @($Instance.Identity) } else { $null }
            }
        }
        """;

    private static readonly string Fakes = McpHolidayCallHandlingTests.FakeTeams + "\n" + ChangeCmdlets;

    private static HolidayCallHandlingState Read(string output) => HolidayCallHandling.ParseState(output);

    private static string ReadScript => HolidayCallHandling.GetReadCommand(new[] { AaId }, true, checkAuthorizedUsers: true);

    [Fact]
    public void Scripts_ParseWithTheRealPowerShellParser()
    {
        var before = Read(McpHolidayCallHandlingTests.RunPowerShell(Fakes, ReadScript));
        var scripts = new[]
        {
            HolidayScheduleOperations.GetSetDatesCommand(Y2027, before.FindSchedule(Y2027)!.Windows, HolidayScheduleOperations.ParseWindows("2027-12-24 12:00 to 2027-12-25 00:00\n2027-12-31 to 2028-01-02")),
            HolidayScheduleOperations.GetSetGreetingCommand(new GreetingChange(AaId, Y2026, "It's \"closed\" $today `now`", null)),
            HolidayScheduleOperations.GetSetGreetingCommand(new GreetingChange(AaId, Y2026, null, Audio)),
            HolidayScheduleOperations.GetRetireCommand(HolidayScheduleOperations.AssessRetire(before, new[] { Y2020 }, Now, false))
        };
        foreach (var script in scripts)
        {
            Parser.ParseInput(script, out _, out var errors);
            Assert.True(errors.Length == 0, string.Join("; ", errors.Select(error => error.Message)) + "\n" + script);
            Assert.DoesNotContain("__", script);
        }
    }

    [Theory]
    [InlineData("2027-12-25", "2027-12-25T00:00:00", "2027-12-26T00:00:00")]
    [InlineData("24.12.2027 12:00 to 25.12.2027 00:00", "2027-12-24T12:00:00", "2027-12-25T00:00:00")]
    [InlineData("2027-12-31 to 2028-01-02", "2027-12-31T00:00:00", "2028-01-03T00:00:00")]
    public void DateRanges_AreReadAsWholeDaysHalfDaysAndAcrossAYear(string text, string start, string end)
    {
        var window = Assert.Single(HolidayScheduleOperations.ParseWindows(text));
        Assert.Equal(DateTime.Parse(start, System.Globalization.CultureInfo.InvariantCulture), window.Start);
        Assert.Equal(DateTime.Parse(end, System.Globalization.CultureInfo.InvariantCulture), window.End);
        Assert.Throws<FormatException>(() => HolidayScheduleOperations.ParseWindows("next Monday"));
        Assert.Throws<FormatException>(() => HolidayScheduleOperations.ParseWindows("2027-12-24 12:00"));
    }

    [Fact]
    public void SetDates_SavesAHalfDayAndARangeAcrossTheYearAndRefusesAStalePreview()
    {
        var before = Read(McpHolidayCallHandlingTests.RunPowerShell(Fakes, ReadScript));
        var wanted = HolidayScheduleOperations.ParseWindows("2027-12-24 12:00 to 2027-12-25 00:00\n2027-12-25\n2027-12-31 to 2028-01-02");
        var assessment = HolidayScheduleOperations.AssessDates(before, Y2027, wanted, false);
        Assert.True(assessment.CanSave, string.Join(" ", assessment.Blockers.Select(blocker => blocker.Message)));
        Assert.Equal(2, assessment.Added.Count);
        Assert.Single(assessment.Kept);
        Assert.Empty(assessment.Removed);

        var script = HolidayScheduleOperations.GetSetDatesCommand(Y2027, assessment.Schedule!.Windows, assessment.NewWindows);
        var output = McpHolidayCallHandlingTests.RunPowerShell(Fakes, script, ReadScript);
        Assert.Contains("SUCCESS: Schedule 'Feiertage 2027' has the new dates", output);
        var saved = Read(output).FindSchedule(Y2027)!;
        Assert.Equal(wanted.OrderBy(window => window.Start), saved.Windows.OrderBy(window => window.Start));

        // The same script again: the schedule no longer holds the dates the preview showed.
        var stale = McpHolidayCallHandlingTests.RunPowerShell(Fakes, script, script);
        Assert.Contains("are not the ones the preview showed. Nothing was changed.", stale);
    }

    [Fact]
    public void DateCheck_RefusesTheSameStartAsAnotherHolidayAndNamesEveryAffectedAutoAttendant()
    {
        var state = Read(State(
            new[] { Attendant(AaId, "Main AA", new[] { Link(Y2026, Flow2026), Link(Y2027, NewFlow) }, new[] { Flow(Flow2026, "a"), Flow(NewFlow, "b") }) },
            new[]
            {
                Schedule(Y2026, "Betriebsferien", "2027-12-24T00:00:00", "2028-01-03T00:00:00", AaId),
                Schedule(Y2027, "Feiertage 2027", "2027-08-01T00:00:00", "2027-08-02T00:00:00", AaId)
            }));

        var sameStart = HolidayScheduleOperations.AssessDates(state, Y2027, HolidayScheduleOperations.ParseWindows("2027-08-01\n2027-12-24"), true);
        Assert.Contains(sameStart.Blockers, blocker => blocker.Code == "sameStartTime");

        var overlap = HolidayScheduleOperations.AssessDates(state, Y2027, HolidayScheduleOperations.ParseWindows("2027-08-01\n2027-12-25"), false);
        Assert.Equal("overlap", Assert.Single(overlap.Blockers).Code);
        Assert.Equal("Main AA", Assert.Single(overlap.AffectedAutoAttendants).Name);
        Assert.True(HolidayScheduleOperations.AssessDates(state, Y2027, HolidayScheduleOperations.ParseWindows("2027-08-01\n2027-12-25"), true).CanSave);

        Assert.Equal("noDates", Assert.Single(HolidayScheduleOperations.AssessDates(state, Y2027, Array.Empty<ScheduleWindow>(), false).Blockers).Code);
        Assert.True(HolidayScheduleOperations.AssessDates(state, Y2027, HolidayScheduleOperations.ParseWindows("2027-08-01"), false).NoChange);
    }

    [Fact]
    public void SetGreeting_SendsTheTextAsWrittenAndKeepsTheMenu()
    {
        const string text = "L’été, c'est \"fermé\" – $5 & `plus`; appelez le 044 111 22 33.";
        var script = HolidayScheduleOperations.GetSetGreetingCommand(new GreetingChange(AaId, Y2026, text, null));
        Assert.DoesNotContain("fermé", script);

        var output = McpHolidayCallHandlingTests.RunPowerShell(Fakes, script, ReadScript);
        var attendant = Read(output).FindAutoAttendant(AaId)!;
        var flow = attendant.FindCallFlow(attendant.HolidayLinkFor(Y2026)!.CallFlowId)!;

        Assert.Equal(text, Assert.Single(flow.Greetings).Text);
        Assert.Equal("tel:+41441112233", Assert.Single(flow.Options).Target!.Id);
        Assert.True(HolidayScheduleOperations.GreetingIs(flow, new GreetingChange(AaId, Y2026, text, null)));
    }

    [Fact]
    public void SetGreeting_TakesAnAudioFileOfTheAutoAttendantApplicationById()
    {
        var change = new GreetingChange(AaId, Y2026, null, Audio);
        var output = McpHolidayCallHandlingTests.RunPowerShell(Fakes, HolidayScheduleOperations.GetSetGreetingCommand(change), ReadScript);
        var attendant = Read(output).FindAutoAttendant(AaId)!;
        var flow = attendant.FindCallFlow(attendant.HolidayLinkFor(Y2026)!.CallFlowId)!;

        Assert.Equal(Audio, Assert.Single(flow.Greetings).AudioFile!.Id);
        Assert.True(HolidayScheduleOperations.GreetingIs(flow, change));
        Assert.False(HolidayScheduleOperations.GreetingIs(flow, new GreetingChange(AaId, Y2026, "text", null)));

        var before = Read(McpHolidayCallHandlingTests.RunPowerShell(Fakes, ReadScript));
        Assert.Equal("oneGreeting", Assert.Single(HolidayScheduleOperations.AssessGreeting(before, new GreetingChange(AaId, Y2026, "a", Audio)).Blockers).Code);
        Assert.Equal("oneGreeting", Assert.Single(HolidayScheduleOperations.AssessGreeting(before, new GreetingChange(AaId, Y2026, null, null)).Blockers).Code);
        Assert.Equal("notAttached", Assert.Single(HolidayScheduleOperations.AssessGreeting(before, new GreetingChange(AaId, Y2027, "a", null)).Blockers).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retire_TakesTheScheduleOffAndDeletesItAndLeavesTheOthers(bool removeCmdletThrowsAfterItsWork)
    {
        var before = Read(McpHolidayCallHandlingTests.RunPowerShell(Fakes, ReadScript));
        var assessment = HolidayScheduleOperations.AssessRetire(before, new[] { Y2020 }, Now, false);
        Assert.True(assessment.CanSave, string.Join(" ", assessment.Blockers.Select(blocker => blocker.Message)));
        var reference = Assert.Single(Assert.Single(assessment.Schedules).References);
        Assert.True(reference.CallFlowIsRemoved);

        var output = McpHolidayCallHandlingTests.RunPowerShell(
            Fakes + (removeCmdletThrowsAfterItsWork ? "\n$global:pdFakeRemoveThrows = $true\n" : "\n$global:pdFakeRemoveThrows = $false\n"),
            HolidayScheduleOperations.GetRetireCommand(assessment),
            ReadScript);
        Assert.Contains("RETIRED: " + Y2020, output);
        Assert.Contains("SUCCESS: 1 holiday schedule(s) were retired", output);

        var after = Read(output);
        var attendant = after.FindAutoAttendant(AaId)!;
        Assert.Null(after.FindSchedule(Y2020));
        Assert.Null(attendant.HolidayLinkFor(Y2020));
        Assert.Null(attendant.FindCallFlow(Flow2020));
        // What was not named stays: the other holiday with its call flow, and the opening hours.
        Assert.NotNull(attendant.HolidayLinkFor(Y2026));
        Assert.NotNull(attendant.FindCallFlow(Flow2026));
        Assert.Contains(attendant.Associations, link => link.Type == "AfterHours" && link.ScheduleId == Hours);
        var leftovers = HolidayScheduleOperations.RetireLeftovers(after, assessment);
        Assert.Empty(leftovers.RemainingSchedules);
        Assert.Empty(leftovers.RemainingLinks);
    }

    [Fact]
    public void RetireCheck_RefusesAScheduleWithAFutureDateOpeningHoursAndKeepsASharedCallFlow()
    {
        var before = Read(McpHolidayCallHandlingTests.RunPowerShell(Fakes, ReadScript));
        Assert.Equal("notExpired", Assert.Single(HolidayScheduleOperations.AssessRetire(before, new[] { Y2026 }, Now, false).Blockers).Code);
        Assert.True(HolidayScheduleOperations.AssessRetire(before, new[] { Y2026 }, Now, true).CanSave);
        Assert.Contains(HolidayScheduleOperations.AssessRetire(before, new[] { Hours }, Now, true).Blockers, blocker => blocker.Code == "notAHolidaySchedule");
        Assert.Throws<ArgumentException>(() => HolidayScheduleOperations.AssessRetire(before, new[] { "Feiertage 2020" }, Now, false));

        // Two holidays on one call flow: retiring one keeps the call flow for the other.
        var shared = Read(State(
            new[] { Attendant(AaId, "Main AA", new[] { Link(Y2020, Flow2020), Link(Y2026, Flow2020) }, new[] { Flow(Flow2020, "a") }) },
            new[]
            {
                Schedule(Y2020, "2020", "2020-12-25T00:00:00", "2020-12-26T00:00:00", AaId),
                Schedule(Y2026, "2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00", AaId)
            }));
        var keepsFlow = HolidayScheduleOperations.AssessRetire(shared, new[] { Y2020 }, Now, false);
        Assert.False(Assert.Single(Assert.Single(keepsFlow.Schedules).References).CallFlowIsRemoved);
    }

    [Fact]
    public void ReadScript_TellsAMissingUserFromADisabledOneAndFromALookupThatFailed()
    {
        var output = McpHolidayCallHandlingTests.RunPowerShell(
            Fakes + """

            function global:Get-MgUser {
                [CmdletBinding()] param([string]$UserId, [string[]]$Property)
                if ($UserId -eq '99999999-0000-0000-0000-000000000009') { throw "Insufficient privileges to complete the operation." }
                [pscustomobject]@{ Id = $UserId; AccountEnabled = $false; UserPrincipalName = 'left@contoso.com' }
            }
            """,
            ReadScript);
        var state = Read(output);

        Assert.Empty(state.MissingAuthorizedUsers);
        Assert.Equal("permission", Assert.Single(state.UncheckedAuthorizedUsers).Kind);
        var disabled = Assert.Single(state.DisabledAuthorizedUsers);
        Assert.Equal("88888888-0000-0000-0000-000000000008", disabled.UserId);

        // A disabled user can be removed by its ID; a user that could not be looked up cannot.
        var removeDisabled = HolidayCallHandling.Assess(state, new CopyRequest(AaId, Y2026, Y2027, RemoveAuthorizedUserIds: new[] { disabled.UserId }), Now);
        Assert.Equal(new[] { disabled.UserId }, removeDisabled.AuthorizedUsersToRemove);
        Assert.Empty(removeDisabled.Blockers);
        var removeUnchecked = HolidayCallHandling.Assess(state, new CopyRequest(AaId, Y2026, Y2027, RemoveAuthorizedUserIds: new[] { "99999999-0000-0000-0000-000000000009" }), Now);
        Assert.Equal("userNotRemovable", Assert.Single(removeUnchecked.Blockers).Code);
        Assert.Empty(removeUnchecked.AuthorizedUsersToRemove);
    }

    [Fact]
    public async Task EditDatesTool_ShowsThePlanSavesVerifiesAndRefusesAPlanAfterTheScheduleChanged()
    {
        var before = State(
            new[] { Attendant(AaId, "Main AA", new[] { Link(Y2027, NewFlow) }, new[] { Flow(NewFlow, "a") }) },
            new[] { Schedule(Y2027, "Feiertage 2027", "2027-12-25T00:00:00", "2027-12-26T00:00:00", AaId) });
        using var host = McpHolidayCallHandlingTests.HostWith(before);
        RespondState(host, before);
        var tools = host.Tool<HolidayChangeTools>();
        var ranges = new[] { "2027-12-24 12:00 to 2027-12-25 00:00", "2027-12-25" };

        var planned = McpTestHost.Parse(await tools.EditHolidayScheduleDates(Y2027, ranges, cancellationToken: Cancellation));
        Assert.Equal("planned", planned.GetProperty("plan").GetProperty("status").GetString());
        Assert.Equal("2027-12-24 12:00 to 2027-12-25 00:00", planned.GetProperty("preview").GetProperty("added")[0].GetString());
        Assert.Equal("Main AA", planned.GetProperty("preview").GetProperty("affectedAutoAttendants")[0].GetProperty("name").GetString());
        var script = planned.GetProperty("plan").GetProperty("script").GetString()!;

        // Someone changes the schedule in Teams: the plan is refused and nothing runs.
        RespondState(host, State(
            new[] { Attendant(AaId, "Main AA", new[] { Link(Y2027, NewFlow) }, new[] { Flow(NewFlow, "a") }) },
            new[] { Schedule(Y2027, "Feiertage 2027", "2027-12-26T00:00:00", "2027-12-27T00:00:00", AaId) }));
        var stale = McpTestHost.Parse(await tools.EditHolidayScheduleDates(
            Y2027, ranges, planId: planned.GetProperty("plan").GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Contains("The tenant changed after this plan was made", stale.GetProperty("error").GetString());
        Assert.DoesNotContain(script, host.PowerShell.Executed);

        RespondState(host, before);
        host.PowerShell.Respond(script, "SUCCESS: Schedule 'Feiertage 2027' has the new dates");
        host.PowerShell.AfterRun = executed =>
        {
            if (executed == script)
            {
                RespondState(host, State(
                    new[] { Attendant(AaId, "Main AA", new[] { Link(Y2027, NewFlow) }, new[] { Flow(NewFlow, "a") }) },
                    new[]
                    {
                        new
                        {
                            id = Y2027,
                            name = "Feiertage 2027",
                            type = "Fixed",
                            windows = new[]
                            {
                                new { start = "2027-12-24T12:00:00", end = "2027-12-25T00:00:00" },
                                new { start = "2027-12-25T00:00:00", end = "2027-12-26T00:00:00" }
                            },
                            usedBy = new[] { AaId }
                        }
                    }));
            }
        };
        var again = McpTestHost.Parse(await tools.EditHolidayScheduleDates(Y2027, ranges, cancellationToken: Cancellation));
        var applied = McpTestHost.Parse(await tools.EditHolidayScheduleDates(
            Y2027, ranges, planId: again.GetProperty("plan").GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Equal("applied", applied.GetProperty("status").GetString());
        Assert.True(applied.GetProperty("verification").GetProperty("datesAsSent").GetBoolean());

        var noChange = McpTestHost.Parse(await tools.EditHolidayScheduleDates(Y2027, ranges, cancellationToken: Cancellation));
        Assert.Equal("noChange", noChange.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RetireTool_NamesEveryLinkAndReportsWhatIsLeftAfterAPartialFailure()
    {
        var before = State(
            new[] { Attendant(AaId, "Main AA", new[] { Link(Y2020, Flow2020), Link(Y2026, Flow2026) }, new[] { Flow(Flow2020, "a"), Flow(Flow2026, "b") }) },
            new[]
            {
                Schedule(Y2020, "Feiertage 2020", "2020-12-25T00:00:00", "2020-12-26T00:00:00", AaId),
                Schedule(Y2026, "Feiertage 2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00", AaId)
            });
        using var host = McpHolidayCallHandlingTests.HostWith(before);
        RespondState(host, before);
        var tools = host.Tool<HolidayChangeTools>();

        var refused = McpTestHost.Parse(await tools.RetireHolidaySchedules(new[] { Y2026 }, cancellationToken: Cancellation));
        Assert.Equal("blocked", refused.GetProperty("status").GetString());

        var planned = McpTestHost.Parse(await tools.RetireHolidaySchedules(new[] { Y2020 }, cancellationToken: Cancellation));
        var link = planned.GetProperty("preview").GetProperty("schedules")[0].GetProperty("removedLinks")[0];
        Assert.Equal("Main AA", link.GetProperty("autoAttendant").GetString());
        Assert.True(link.GetProperty("callFlowIsRemoved").GetBoolean());
        var script = planned.GetProperty("plan").GetProperty("script").GetString()!;
        Assert.DoesNotContain(Y2026, script);

        // The link came off, but the schedule could not be deleted.
        host.PowerShell.Respond(script, "INFO: 1 holiday link(s) were taken off\nNOTRETIRED: " + Y2020 + " timeout\nERROR: Failed to retire the holiday schedules", hadErrors: true);
        host.PowerShell.AfterRun = executed =>
        {
            if (executed == script)
            {
                RespondState(host, State(
                    new[] { Attendant(AaId, "Main AA", new[] { Link(Y2026, Flow2026) }, new[] { Flow(Flow2026, "b") }) },
                    new[]
                    {
                        Schedule(Y2020, "Feiertage 2020", "2020-12-25T00:00:00", "2020-12-26T00:00:00"),
                        Schedule(Y2026, "Feiertage 2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00", AaId)
                    }));
            }
        };
        var failed = McpTestHost.Parse(await tools.RetireHolidaySchedules(
            new[] { Y2020 }, planId: planned.GetProperty("plan").GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        var verification = failed.GetProperty("verification");
        Assert.False(verification.GetProperty("retired").GetBoolean());
        Assert.Equal(Y2020, verification.GetProperty("remainingSchedules")[0].GetString());
        Assert.Equal(0, verification.GetProperty("remainingLinks").GetArrayLength());

        // The retry plans only what is left: no auto attendant is named in the script.
        host.PowerShell.AfterRun = null;
        var retry = McpTestHost.Parse(await tools.RetireHolidaySchedules(new[] { Y2020 }, cancellationToken: Cancellation));
        Assert.Contains("foreach ($pdAaId in @())", retry.GetProperty("plan").GetProperty("script").GetString()!);
        Assert.Equal(0, retry.GetProperty("preview").GetProperty("schedules")[0].GetProperty("removedLinks").GetArrayLength());
    }

    [Fact]
    public async Task RetireTool_ARetryThatNamesASchedulThatIsGoneRetiresOnlyTheRest()
    {
        // The first run deleted 2020 and failed at 2024: the second run names both again.
        const string y2024 = "44444444-0000-0000-0000-000000000004";
        var afterFirstRun = State(
            new[] { Attendant(AaId, "Main AA", new[] { Link(Y2026, Flow2026) }, new[] { Flow(Flow2026, "b") }) },
            new[]
            {
                Schedule(y2024, "Feiertage 2024", "2024-12-25T00:00:00", "2024-12-26T00:00:00"),
                Schedule(Y2026, "Feiertage 2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00", AaId)
            });
        using var host = McpHolidayCallHandlingTests.HostWith(afterFirstRun);
        RespondState(host, afterFirstRun);
        var tools = host.Tool<HolidayChangeTools>();

        var retry = McpTestHost.Parse(await tools.RetireHolidaySchedules(new[] { Y2020, y2024 }, cancellationToken: Cancellation));
        Assert.Equal("planned", retry.GetProperty("plan").GetProperty("status").GetString());
        var script = retry.GetProperty("plan").GetProperty("script").GetString()!;
        Assert.Contains(y2024, script);
        Assert.DoesNotContain(Y2020, script);
        Assert.True(retry.GetProperty("preview").GetProperty("schedules")[0].GetProperty("alreadyGone").GetBoolean());

        // When every named schedule is gone, the call succeeds and changes nothing.
        var done = McpTestHost.Parse(await tools.RetireHolidaySchedules(new[] { Y2020 }, cancellationToken: Cancellation));
        Assert.Equal("alreadyRetired", done.GetProperty("status").GetString());
        Assert.True(done.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Rollout_DoesNotCallAnItemVerifiedWhenThereIsNoSourceToCompareWith()
    {
        // The target is attached, but the named source schedule is not: nothing proves the call handling.
        var state = Read(State(
            new[] { Attendant(AaId, "Main AA", new[] { Link(Y2027, NewFlow) }, new[] { Flow(NewFlow, "a") }) },
            new[]
            {
                Schedule(Y2026, "Feiertage 2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00"),
                Schedule(Y2027, "Feiertage 2027", "2027-12-25T00:00:00", "2027-12-26T00:00:00", AaId)
            }));

        var result = Assert.Single(HolidayScheduleOperations.AssessRollout(
            state, Y2027, new[] { new RolloutItem(AaId, Y2026) }, false, false, _ => Now));

        Assert.Equal(RolloutOutcome.Different, result.Outcome);
        Assert.Contains("could not be compared", Assert.Single(result.Differences));
        Assert.Contains(result.Assessment.Blockers, blocker => blocker.Code == "sourceNotAttached");

        // A link to a call flow that does not exist is not verified either.
        var dangling = Read(State(
            new[] { Attendant(AaId, "Main AA", new[] { Link(Y2026, Flow2026), Link(Y2027, NewFlow) }, new[] { Flow(Flow2026, "a") }) },
            new[]
            {
                Schedule(Y2026, "Feiertage 2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00", AaId),
                Schedule(Y2027, "Feiertage 2027", "2027-12-25T00:00:00", "2027-12-26T00:00:00", AaId)
            }));
        Assert.Equal(RolloutOutcome.Different, Assert.Single(HolidayScheduleOperations.AssessRollout(
            dangling, Y2027, new[] { new RolloutItem(AaId, Y2026) }, false, false, _ => Now)).Outcome);
    }

    [Fact]
    public async Task Rollout_ReportsEachAutoAttendantFromTheTenantAndARetrySkipsWhatIsVerified()
    {
        // Two main numbers with different call handling: a direct transfer without a greeting, and a shared voicemail.
        object[] Attendants(bool firstAttached, bool secondAttached)
        {
            var first = new List<object> { Link(Y2026, Flow2026) };
            var firstFlows = new List<object> { Flow(Flow2026, string.Empty) };
            if (firstAttached)
            {
                first.Add(Link(Y2027, NewFlow));
                firstFlows.Add(Flow(NewFlow, string.Empty));
            }

            var second = new List<object> { Link(Y2026, Flow2020) };
            var secondFlows = new List<object> { Flow(Flow2020, "Bitte hinterlassen Sie eine Nachricht.", "bbbbbbbb-0000-0000-0000-00000000000b", "SharedVoicemail") };
            if (secondAttached)
            {
                second.Add(Link(Y2027, "f4444444-0000-0000-0000-000000000004"));
                secondFlows.Add(Flow("f4444444-0000-0000-0000-000000000004", "Bitte hinterlassen Sie eine Nachricht.", "bbbbbbbb-0000-0000-0000-00000000000b", "SharedVoicemail"));
            }

            return new[] { Attendant(AaId, "Main AA", first, firstFlows), Attendant(SecondAa, "Branch AA", second, secondFlows) };
        }

        var schedules = new[]
        {
            Schedule(Y2026, "Feiertage 2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00", AaId, SecondAa),
            Schedule(Y2027, "Feiertage 2027", "2027-12-25T00:00:00", "2027-12-26T00:00:00")
        };
        var ids = new[] { AaId, SecondAa };
        using var host = McpHolidayCallHandlingTests.HostWith(State(Attendants(false, false), schedules));
        RespondState(host, State(Attendants(false, false), schedules), ids);
        var tools = host.Tool<HolidayChangeTools>();
        var items = new[]
        {
            new RolloutItemInput { AutoAttendantId = AaId, SourceScheduleId = Y2026 },
            new RolloutItemInput { AutoAttendantId = SecondAa, SourceScheduleId = Y2026 }
        };

        var planned = McpTestHost.Parse(await tools.RolloutHolidayCallHandling(Y2027, items, cancellationToken: Cancellation));
        Assert.All(planned.GetProperty("items").EnumerateArray(), item => Assert.Equal("Planned", item.GetProperty("outcome").GetString()));
        var script = planned.GetProperty("plan").GetProperty("script").GetString()!;
        Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);

        // The script prints a success line and a Teams error; only the first auto attendant has the schedule.
        host.PowerShell.Respond(script, "SUCCESS: Schedule attached\nERROR: Failed to copy the holiday call handling: validation error", hadErrors: true);
        host.PowerShell.AfterRun = executed =>
        {
            if (executed == script)
            {
                RespondState(host, State(Attendants(true, false), schedules), ids);
            }
        };
        var partial = McpTestHost.Parse(await tools.RolloutHolidayCallHandling(
            Y2027, items, planId: planned.GetProperty("plan").GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Equal("failed", partial.GetProperty("status").GetString());
        var outcomes = partial.GetProperty("items").EnumerateArray().ToDictionary(
            item => item.GetProperty("autoAttendant").GetString()!, item => item.GetProperty("outcome").GetString());
        Assert.Equal("Verified", outcomes["Main AA"]);
        Assert.Equal("Failed", outcomes["Branch AA"]);
        Assert.Equal(1, partial.GetProperty("summary").GetProperty("failed").GetInt32());

        // The retry saves the second auto attendant only.
        host.PowerShell.AfterRun = null;
        var retry = McpTestHost.Parse(await tools.RolloutHolidayCallHandling(Y2027, items, cancellationToken: Cancellation));
        var retryScript = retry.GetProperty("plan").GetProperty("script").GetString()!;
        Assert.DoesNotContain(AaId, retryScript);
        Assert.Contains(SecondAa, retryScript);
        host.PowerShell.Respond(retryScript, "SUCCESS: Schedule attached");
        host.PowerShell.AfterRun = executed =>
        {
            if (executed == retryScript)
            {
                RespondState(host, State(Attendants(true, true), schedules), ids);
            }
        };
        var done = McpTestHost.Parse(await tools.RolloutHolidayCallHandling(
            Y2027, items, planId: retry.GetProperty("plan").GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Equal("applied", done.GetProperty("status").GetString());
        Assert.Equal(2, done.GetProperty("summary").GetProperty("verified").GetInt32());

        var nothingLeft = McpTestHost.Parse(await tools.RolloutHolidayCallHandling(Y2027, items, cancellationToken: Cancellation));
        Assert.Equal("alreadyAttached", nothingLeft.GetProperty("status").GetString());
    }

    private static void RespondState(McpTestHost host, string state, params string[] attendantIds)
    {
        var idSets = new List<string[]?> { null, new[] { AaId } };
        if (attendantIds.Length > 0)
        {
            idSets.Add(attendantIds);
        }

        foreach (var ids in idSets)
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

    private static string State(IEnumerable<object> attendants, IEnumerable<object> schedules)
        => HolidayCallHandling.JsonMarker + JsonSerializer.Serialize(new
        {
            autoAttendants = attendants,
            schedules,
            missingAuthorizedUsers = Array.Empty<object>(),
            uncheckedAuthorizedUsers = Array.Empty<object>(),
            disabledAuthorizedUsers = Array.Empty<object>()
        }) + "\nSUCCESS: Holiday call handling retrieved";

    private static object Attendant(string id, string name, IEnumerable<object> links, IEnumerable<object> flows)
        => new { id, name, authorizedUsers = Array.Empty<string>(), callFlows = flows, associations = links };

    private static object Link(string scheduleId, string callFlowId)
        => new { type = "Holiday", scheduleId, callFlowId, enabled = true, priority = 0 };

    private static object Flow(string id, string greeting, string targetId = "tel:+41441112233", string targetType = "ExternalPstn")
        => new
        {
            id,
            name = "call flow",
            greetings = greeting.Length == 0
                ? Array.Empty<object>()
                : new object[] { new { type = "TextToSpeech", text = greeting, audioFile = (object?)null } },
            menuName = "menu",
            menuPrompts = Array.Empty<object>(),
            options = new[]
            {
                new
                {
                    action = "TransferCallToTarget",
                    key = "Automatic",
                    voiceResponses = Array.Empty<string>(),
                    target = new { id = targetId, type = targetType, enableTranscription = (bool?)null, suppressSystemPrompt = (bool?)null, callPriority = 0 },
                    prompt = (object?)null
                }
            },
            forceListenMenu = false,
            dialByName = false
        };

    private static object Schedule(string id, string name, string start, string end, params string[] usedBy)
        => new { id, name, type = "Fixed", windows = new[] { new { start, end } }, usedBy };
}
