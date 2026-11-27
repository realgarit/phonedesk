using System.Management.Automation.Language;
using System.Text.Json;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Operations;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// Changes to a call queue or an auto attendant that exists. The read and the save scripts run in
/// a real PowerShell engine against stand-ins that keep the object, so a save is checked by a new read.
/// </summary>
public sealed class ObjectEditTests
{
    private const string QueueId = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string AttendantId = "bbbbbbbb-1111-2222-3333-444444444444";
    private const string AccountId = "cccccccc-1111-2222-3333-444444444444";
    private const string GroupId = "dddddddd-1111-2222-3333-444444444444";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static string Run(params string[] scripts) => McpHolidayCallHandlingTests.RunPowerShell(scripts.Prepend(FakeTeams).ToArray());

    private static Dictionary<string, string?> Wanted(params (string Key, string Value)[] values)
        => values.ToDictionary(value => value.Key, value => (string?)value.Value);

    [Fact]
    public void CallQueue_SendsOnlyWhatDiffersAndReportsEachSetting()
    {
        var state = ObjectEdit.Parse(EditObjectKind.CallQueue, Run(ObjectEdit.GetReadCommand(EditObjectKind.CallQueue, "Support CQ")));
        Assert.Equal(QueueId, state.Id);
        Assert.Equal("Attendant", state.Values["routingMethod"]);
        Assert.Equal("true", state.Values["allowOptOut"]);
        Assert.Equal("DisconnectWithBusy", state.Values["overflowAction"]);

        // The same value, in another case, is not a change. The alert time the stand-in refuses is one.
        var assessment = ObjectEdit.Assess(state, Wanted(
            ("routingMethod", "longestidle"), ("language", "de-DE"), ("agentAlertTime", "99"),
            ("greetingText", "Guten Tag, \"Contoso\" & $Partner's Hotline"),
            ("timeoutAction", "Forward"), ("timeoutTarget", "support@contoso.com"), ("allowOptOut", "no")));
        Assert.Empty(assessment.Blockers);
        Assert.Equal(new[] { "routingMethod", "agentAlertTime", "greetingText", "allowOptOut", "timeoutAction", "timeoutTarget" }, assessment.Changes.Select(change => change.Key));
        Assert.Equal("LongestIdle", assessment.Changes[0].New);
        // An action and its target are one step.
        Assert.Equal(new[] { "routingMethod", "agentAlertTime", "greetingText", "allowOptOut", "timeoutAction" }, ObjectEdit.Steps(assessment));

        var script = ObjectEdit.GetApplyCommand(assessment);
        Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);
        // No value is part of the script text.
        Assert.DoesNotContain("Partner", script);
        Assert.DoesNotContain("support@contoso.com", script);

        var output = Run(script, ObjectEdit.GetReadCommand(EditObjectKind.CallQueue, QueueId));
        var outcomes = ObjectEdit.ParseOutcome(assessment, output).ToDictionary(outcome => outcome.Key, outcome => outcome);
        Assert.True(outcomes["routingMethod"].Saved);
        Assert.True(outcomes["greetingText"].Saved);
        Assert.True(outcomes["timeoutAction"].Saved);
        Assert.True(outcomes["timeoutTarget"].Saved);
        // One setting failed: the others were saved all the same, and this one says why.
        Assert.False(outcomes["agentAlertTime"].Saved);
        Assert.Contains("not accepted", outcomes["agentAlertTime"].Message);

        var after = ObjectEdit.Parse(EditObjectKind.CallQueue, output);
        Assert.Equal("LongestIdle", after.Values["routingMethod"]);
        Assert.Equal("Guten Tag, \"Contoso\" & $Partner's Hotline", after.Values["greetingText"]);
        Assert.Equal("false", after.Values["allowOptOut"]);
        Assert.Equal("Forward", after.Values["timeoutAction"]);
        // The sign-in name was resolved to the ID of the account.
        Assert.Equal(AccountId, after.Values["timeoutTarget"]);
        Assert.Equal("30", after.Values["agentAlertTime"]);
        Assert.Equal(new[] { "agentAlertTime", "timeoutTarget" }, ObjectEdit.NotAsWanted(assessment, after).Select(change => change.Key));
    }

    [Fact]
    public void Values_AreCheckedBeforeAnythingIsSent()
    {
        var state = ObjectEdit.Parse(EditObjectKind.CallQueue, Run(ObjectEdit.GetReadCommand(EditObjectKind.CallQueue, QueueId)));

        var bad = ObjectEdit.Assess(state, Wanted(
            ("routingMethod", "Fastest"), ("agentAlertTime", "5"), ("allowOptOut", "maybe"), ("agentGroups", "the support group"),
            ("overflowAction", "Forward"), ("colour", "blue")));
        Assert.Equal(new[] { "unknownField", "invalid", "invalid", "invalid", "invalid", "targetMissing" }, bad.Blockers.Select(blocker => blocker.Code));
        Assert.False(bad.CanApply);
        Assert.Throws<InvalidOperationException>(() => ObjectEdit.GetApplyCommand(bad));

        // Nothing differs: there is nothing to save.
        var same = ObjectEdit.Assess(state, Wanted(("routingMethod", "Attendant"), ("agentGroups", GroupId.ToUpperInvariant())));
        Assert.Empty(same.Changes);
        Assert.False(same.CanApply);

        // A name two objects have, and a name none has.
        var twice = Assert.Throws<InvalidDataException>(() => ObjectEdit.Parse(EditObjectKind.CallQueue, Run(ObjectEdit.GetReadCommand(EditObjectKind.CallQueue, "Twin CQ"))));
        Assert.Contains("2 call queues are named", twice.Message);
        Assert.Contains("No call queue", Assert.Throws<InvalidDataException>(() => ObjectEdit.Parse(EditObjectKind.CallQueue, Run(ObjectEdit.GetReadCommand(EditObjectKind.CallQueue, "Nobody")))).Message);
    }

    [Fact]
    public void AutoAttendant_ChangesHoursGreetingsAndTheAfterHoursAction()
    {
        var state = ObjectEdit.Parse(EditObjectKind.AutoAttendant, Run(ObjectEdit.GetReadCommand(EditObjectKind.AutoAttendant, "Main AA")));
        Assert.Equal("Mon 08:00-12:00,13:00-17:00; Tue 08:00-17:00; Wed closed; Thu closed; Fri closed; Sat closed; Sun closed", state.Values["businessHours"]);
        Assert.Equal("Willkommen", state.Values["greetingText"]);
        Assert.Equal(ObjectEdit.Disconnect, state.Values["afterHoursAction"]);
        // The call flow in opening hours has a menu with keys: its action is not changed here.
        Assert.Contains("menu with keys", state.Locked["defaultAction"]);
        Assert.Contains(state.Notes, note => note.Contains("shared", StringComparison.Ordinal));

        var locked = ObjectEdit.Assess(state, Wanted(("defaultAction", "Disconnect")));
        Assert.Equal("locked", Assert.Single(locked.Blockers).Code);

        var assessment = ObjectEdit.Assess(state, Wanted(
            ("businessHours", "mon 08:00-17:00; Sat 09:00-12:00; Sun 00:00-24:00"),
            ("timeZone", "UTC"), ("afterHoursGreetingText", "Wir sind geschlossen."),
            ("afterHoursAction", "Transfer"), ("afterHoursTarget", "support@contoso.com")));
        Assert.Empty(assessment.Blockers);
        // The hours changed for Monday, Tuesday, Saturday and Sunday; the warning says the schedule is shared.
        Assert.Contains("Mon 08:00-17:00; Tue closed", assessment.Changes.Single(change => change.Key == "businessHours").New);
        Assert.Contains("more than one auto attendant", Assert.Single(assessment.Warnings));

        var script = ObjectEdit.GetApplyCommand(assessment);
        Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);
        var output = Run(script, ObjectEdit.GetReadCommand(EditObjectKind.AutoAttendant, AttendantId));

        Assert.All(ObjectEdit.ParseOutcome(assessment, output), outcome => Assert.True(outcome.Saved, outcome.Message));
        var after = ObjectEdit.Parse(EditObjectKind.AutoAttendant, output);
        Assert.Equal("Mon 08:00-17:00; Tue closed; Wed closed; Thu closed; Fri closed; Sat 09:00-12:00; Sun 00:00-24:00", after.Values["businessHours"]);
        Assert.Equal("UTC", after.Values["timeZone"]);
        Assert.Equal("Wir sind geschlossen.", after.Values["afterHoursGreetingText"]);
        Assert.Equal(ObjectEdit.Transfer, after.Values["afterHoursAction"]);
        Assert.Equal(AccountId, after.Values["afterHoursTarget"]);
        // The greeting in opening hours was not part of the change and is as it was.
        Assert.Equal("Willkommen", after.Values["greetingText"]);
    }

    [Fact]
    public void AutoAttendant_ApprovalHoldsOnlyForTheScheduleAndItsUsersThatWereChecked()
    {
        var state = ObjectEdit.Parse(EditObjectKind.AutoAttendant, Run(ObjectEdit.GetReadCommand(EditObjectKind.AutoAttendant, AttendantId)));
        Assert.Equal("sched-1", state.Scope["scheduleId"]);
        Assert.Equal("flow-after", state.Scope["callFlowId"]);
        Assert.Contains("another-attendant", state.Scope["scheduleUsers"]);
        var wanted = Wanted(("businessHours", "Mon 08:00-17:00"), ("afterHoursGreetingText", "Geschlossen."));
        var assessment = ObjectEdit.Assess(state, wanted);
        var approved = ObjectEdit.ApprovedState(assessment);
        var script = ObjectEdit.GetApplyCommand(assessment);

        // Another auto attendant starts to use the schedule: the same hours are another change now.
        const string OneMoreUser = "$global:pdFakeSchedule.AssociatedConfigurationIds += 'third-attendant'";
        var widened = ObjectEdit.Parse(EditObjectKind.AutoAttendant, Run(OneMoreUser, ObjectEdit.GetReadCommand(EditObjectKind.AutoAttendant, AttendantId)));
        Assert.NotEqual(approved, ObjectEdit.ApprovedState(ObjectEdit.Assess(widened, wanted)));
        // And a script that was approved before does not write to the schedule.
        var refused = ObjectEdit.ParseOutcome(assessment, Run(OneMoreUser, script)).ToDictionary(outcome => outcome.Key, outcome => outcome);
        Assert.False(refused["businessHours"].Saved);
        Assert.Contains("Other auto attendants use the schedule", refused["businessHours"].Message);
        Assert.True(refused["afterHoursGreetingText"].Saved);

        // The auto attendant was given another schedule and call flow with the same hours.
        const string OtherSchedule = "$global:pdFakeAttendant.CallHandlingAssociations = @([pscustomobject]@{ Type = 'AfterHours'; ScheduleId = 'sched-2'; CallFlowId = 'flow-other' })";
        var moved = ObjectEdit.ParseOutcome(assessment, Run(OtherSchedule, script));
        Assert.All(moved, outcome => Assert.False(outcome.Saved));
        Assert.Contains(moved, outcome => outcome.Message.Contains("another schedule", StringComparison.Ordinal));
        Assert.Contains(moved, outcome => outcome.Message.Contains("another after-hours call flow", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Mon 08:00-17:10", "steps of 15 minutes")]
    [InlineData("Mon 17:00-08:00", "ends before it starts")]
    [InlineData("Mon 08:00-12:00,11:00-13:00", "overlap")]
    [InlineData("Montag 08:00-12:00 Uhr", "not a time range")]
    [InlineData("08:00-12:00", "does not start with a day")]
    public void OpeningHours_AreRefusedWhenTeamsWouldNotTakeThem(string text, string reason)
        => Assert.Contains(reason, Assert.Throws<ArgumentException>(() => ObjectEdit.BusinessHours.Parse(text)).Message);

    [Fact]
    public async Task Tools_PlanTheChangeReadAgainAndRefuseAPlanForAnObjectThatChanged()
    {
        using var host = new McpTestHost().Connected();
        var tools = host.Tool<EditTools>();
        var read = ObjectEdit.GetReadCommand(EditObjectKind.CallQueue, "Support CQ");
        var readById = ObjectEdit.GetReadCommand(EditObjectKind.CallQueue, QueueId);
        string State(string routing) => ObjectEdit.ReadMarker + JsonSerializer.Serialize(new
        {
            id = QueueId,
            name = "Support CQ",
            values = new Dictionary<string, string> { ["routingMethod"] = routing, ["agentAlertTime"] = "30" },
            locked = new Dictionary<string, string>(),
            notes = Array.Empty<string>()
        });
        host.PowerShell.Respond(read, State("Attendant"));

        var settings = McpTestHost.Parse(await tools.GetCallQueueSettings("Support CQ", Cancellation));
        Assert.Equal("Attendant", settings.GetProperty("settings")[0].GetProperty("value").GetString());

        var changes = new Dictionary<string, string> { ["routingMethod"] = "Serial", ["agentAlertTime"] = "30" };
        var planned = McpTestHost.Parse(await tools.EditCallQueue("Support CQ", changes, cancellationToken: Cancellation));
        Assert.Equal("planned", planned.GetProperty("status").GetString());
        // The alert time is already 30: one change.
        Assert.Equal("routingMethod", Assert.Single(planned.GetProperty("changes").EnumerateArray()).GetProperty("setting").GetString());

        // Someone changed the routing in Teams after the plan.
        host.PowerShell.Respond(read, State("RoundRobin"));
        var stale = McpTestHost.Parse(await tools.EditCallQueue("Support CQ", changes, planned.GetProperty("planId").GetString(), Cancellation));
        Assert.Contains("changed", stale.GetProperty("error").GetString());

        // A new plan runs, and the result is taken from a new read.
        host.PowerShell.Respond(read, State("Attendant"));
        var again = McpTestHost.Parse(await tools.EditCallQueue("Support CQ", changes, cancellationToken: Cancellation));
        host.PowerShell.DefaultResult = new() { Output = ObjectEdit.PropertyMarker + "routingMethod\nSUCCESS: saved" };
        host.PowerShell.Respond(readById, State("Serial"));
        var applied = McpTestHost.Parse(await tools.EditCallQueue("Support CQ", changes, again.GetProperty("planId").GetString(), Cancellation));
        Assert.Equal("applied", applied.GetProperty("status").GetString());
        var setting = applied.GetProperty("settings")[0];
        Assert.True(setting.GetProperty("saved").GetBoolean());
        Assert.True(setting.GetProperty("shownByTenant").GetBoolean());
        Assert.Equal("Serial", setting.GetProperty("now").GetString());

        // The save went through, but the object cannot be read afterwards: nothing is reported as confirmed.
        host.PowerShell.Respond(read, State("Serial"));
        var third = McpTestHost.Parse(await tools.EditCallQueue("Support CQ", new() { ["routingMethod"] = "RoundRobin" }, cancellationToken: Cancellation));
        host.PowerShell.Respond(readById, "ERROR: The service did not answer.");
        var unconfirmed = McpTestHost.Parse(await tools.EditCallQueue("Support CQ", new() { ["routingMethod"] = "RoundRobin" }, third.GetProperty("planId").GetString(), Cancellation));
        Assert.Contains("no setting is confirmed", unconfirmed.GetProperty("notConfirmed").GetString());
        Assert.False(unconfirmed.GetProperty("settings")[0].TryGetProperty("shownByTenant", out _));
        host.PowerShell.Respond(read, State("Attendant"));

        Assert.Contains("not one of", McpTestHost.Parse(await tools.EditCallQueue("Support CQ", new() { ["routingMethod"] = "Fastest" }, cancellationToken: Cancellation)).GetProperty("error").GetString());
        Assert.True(McpTestHost.Parse(await tools.EditCallQueue("Support CQ", new() { ["routingMethod"] = "Attendant" }, cancellationToken: Cancellation)).GetProperty("nothingToChange").GetBoolean());
    }

    /// <summary>Stand-ins for the Teams cmdlets. They keep one call queue and one auto attendant and change them like Teams does.</summary>
    private const string FakeTeams = """
        $global:pdFakeQueue = [pscustomobject]@{
            Identity = 'aaaaaaaa-1111-2222-3333-444444444444'; Name = 'Support CQ'; RoutingMethod = 'Attendant'; AgentAlertTime = 30; LanguageId = 'de-DE'
            WelcomeTextToSpeechPrompt = 'Willkommen'; WelcomeMusicAudioFileId = $null; AllowOptOut = $true; PresenceBasedRouting = $false; ConferenceMode = $true
            DistributionLists = @('dddddddd-1111-2222-3333-444444444444'); OverflowThreshold = 50; OverflowAction = 'DisconnectWithBusy'; OverflowActionTarget = $null
            TimeoutThreshold = 1200; TimeoutAction = 'Disconnect'; TimeoutActionTarget = $null
        }
        function Get-CsCallQueue { [CmdletBinding()] param($Identity, $NameFilter)
            if ($Identity -eq $global:pdFakeQueue.Identity -or $NameFilter -eq 'Support CQ') { return $global:pdFakeQueue }
            if ($NameFilter -eq 'Twin CQ') { return @([pscustomobject]@{ Name = 'Twin CQ'; Identity = '1' }, [pscustomobject]@{ Name = 'Twin CQ'; Identity = '2' }) }
        }
        function Set-CsCallQueue { [CmdletBinding()] param($Identity, $RoutingMethod, $AgentAlertTime, $LanguageId, $WelcomeTextToSpeechPrompt, $AllowOptOut, $PresenceBasedRouting, $ConferenceMode, $DistributionLists, $OverflowThreshold, $OverflowAction, $OverflowActionTarget, $TimeoutThreshold, $TimeoutAction, $TimeoutActionTarget)
            if ($PSBoundParameters.ContainsKey('AgentAlertTime') -and $AgentAlertTime -eq 99) { throw 'The agent alert time 99 is not accepted.' }
            foreach ($pdKey in @($PSBoundParameters.Keys)) {
                if ($pdKey -in @('Identity', 'WarningAction', 'ErrorAction')) { continue }
                $pdNew = $PSBoundParameters[$pdKey]
                if ($pdKey -like '*ActionTarget') { $pdNew = [pscustomobject]@{ Id = [string]$pdNew } }
                $global:pdFakeQueue.$pdKey = $pdNew
            }
        }
        function Get-CsOnlineUser { [CmdletBinding()] param($Identity)
            if ($Identity -in @('support@contoso.com', 'cccccccc-1111-2222-3333-444444444444')) {
                return [pscustomobject]@{ Identity = 'cccccccc-1111-2222-3333-444444444444'; UserPrincipalName = 'support@contoso.com'; AccountType = 'ResourceAccount' }
            }
            throw "The account '$Identity' was not found."
        }
        function New-CsAutoAttendantPrompt { [CmdletBinding()] param($TextToSpeechPrompt, $AudioFilePrompt) [pscustomobject]@{ TextToSpeechPrompt = $TextToSpeechPrompt; AudioFilePrompt = $AudioFilePrompt } }
        function New-CsAutoAttendantCallableEntity { [CmdletBinding()] param($Identity, $Type) [pscustomobject]@{ Id = $Identity; Type = $Type } }
        function New-CsAutoAttendantMenuOption { [CmdletBinding()] param($Action, $DtmfResponse, $CallTarget) [pscustomobject]@{ Action = $Action; DtmfResponse = $DtmfResponse; CallTarget = $CallTarget } }
        function New-CsAutoAttendantMenu { [CmdletBinding()] param($Name, $MenuOptions) [pscustomobject]@{ Name = $Name; MenuOptions = @($MenuOptions) } }
        function New-CsOnlineTimeRange { [CmdletBinding()] param($Start, $End) [pscustomobject]@{ Start = [TimeSpan]$Start; End = [TimeSpan]$End } }
        $global:pdFakeSchedule = [pscustomobject]@{
            Id = 'sched-1'; Name = 'After Hours Schedule'; AssociatedConfigurationIds = @('bbbbbbbb-1111-2222-3333-444444444444', 'another-attendant')
            WeeklyRecurrentSchedule = [pscustomobject]@{
                MondayHours = @((New-CsOnlineTimeRange -Start '08:00' -End '12:00'), (New-CsOnlineTimeRange -Start '13:00' -End '17:00'))
                TuesdayHours = @((New-CsOnlineTimeRange -Start '08:00' -End '17:00'))
                WednesdayHours = @(); ThursdayHours = @(); FridayHours = @(); SaturdayHours = @(); SundayHours = @()
            }
        }
        function Get-CsOnlineSchedule { [CmdletBinding()] param($Id) $global:pdFakeSchedule }
        function Set-CsOnlineSchedule { [CmdletBinding()] param($Instance) $global:pdFakeSchedule = $Instance }
        $global:pdFakeAttendant = [pscustomobject]@{
            Identity = 'bbbbbbbb-1111-2222-3333-444444444444'; Name = 'Main AA'; LanguageId = 'de-DE'; TimeZoneId = 'W. Europe Standard Time'
            DefaultCallFlow = [pscustomobject]@{
                Id = 'flow-default'; Greetings = @((New-CsAutoAttendantPrompt -TextToSpeechPrompt 'Willkommen'))
                Menu = [pscustomobject]@{ Name = 'Main menu'; MenuOptions = @(
                    (New-CsAutoAttendantMenuOption -Action TransferCallToTarget -DtmfResponse Tone1 -CallTarget ([pscustomobject]@{ Id = 'x' })),
                    (New-CsAutoAttendantMenuOption -Action TransferCallToTarget -DtmfResponse Tone2 -CallTarget ([pscustomobject]@{ Id = 'y' }))) }
            }
            CallFlows = @([pscustomobject]@{ Id = 'flow-after'; Greetings = @(); Menu = [pscustomobject]@{ Name = 'Closed'; MenuOptions = @((New-CsAutoAttendantMenuOption -Action DisconnectCall -DtmfResponse Automatic)) } })
            CallHandlingAssociations = @([pscustomobject]@{ Type = 'AfterHours'; ScheduleId = 'sched-1'; CallFlowId = 'flow-after' })
        }
        function Get-CsAutoAttendant { [CmdletBinding()] param($Identity, $NameFilter)
            if ($Identity -eq $global:pdFakeAttendant.Identity -or $NameFilter -eq 'Main AA') { return $global:pdFakeAttendant }
        }
        function Set-CsAutoAttendant { [CmdletBinding()] param($Instance) $global:pdFakeAttendant = $Instance }
        """;
}
