using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text.Json;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// The call handling of holiday schedules that exist already: reading it per auto attendant,
/// copying it to a new schedule, the checks before a save and the read-back after it.
/// The two scripts run in a real PowerShell engine against stand-ins for the Teams cmdlets.
/// </summary>
public sealed class McpHolidayCallHandlingTests
{
    private const string AaId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Source = "11111111-0000-0000-0000-000000000001";
    private const string Target = "22222222-0000-0000-0000-000000000002";
    private const string Expired = "33333333-0000-0000-0000-000000000003";
    private const string Other = "44444444-0000-0000-0000-000000000004";
    private const string SourceFlow = "f1111111-0000-0000-0000-000000000001";
    private const string NewFlow = "f2222222-0000-0000-0000-000000000002";
    private const string GoneUser = "99999999-0000-0000-0000-000000000009";
    private const string KeptUser = "88888888-0000-0000-0000-000000000008";

    // The typographic apostrophe must come back from Teams as it was stored.
    private const string Greeting = "Nous sommes fermés aujourd’hui.";

    private static readonly DateTime Now = new(2026, 11, 25, 10, 0, 0);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void Scripts_ParseWithTheRealPowerShellParser()
    {
        var scripts = new[]
        {
            HolidayCallHandling.GetReadCommand(),
            HolidayCallHandling.GetReadCommand([AaId], includeAutoAttendants: true, checkAuthorizedUsers: true),
            HolidayCallHandling.GetReadCommand(null, includeAutoAttendants: false),
            HolidayCallHandling.GetCopyCommand(new CopyRequest(AaId, Source, Target), []),
            HolidayCallHandling.GetCopyCommand(new CopyRequest(AaId, Source, Target, Expired), [GoneUser])
        };
        foreach (var script in scripts)
        {
            Parser.ParseInput(script, out _, out var errors);
            Assert.True(errors.Length == 0, string.Join("; ", errors.Select(error => error.Message)) + "\n" + script);
            Assert.DoesNotContain("__", script);
        }
    }

    [Theory]
    [InlineData("Main AA")]
    [InlineData("x'; Remove-CsAutoAttendant -Identity y; '")]
    [InlineData("")]
    public void Scripts_TakeOnlyGuidsAsObjectIds(string value)
    {
        Assert.Throws<ArgumentException>(() => HolidayCallHandling.GetReadCommand([value]));
        Assert.Throws<ArgumentException>(() => HolidayCallHandling.GetCopyCommand(new CopyRequest(AaId, value, Target), []));
        Assert.Throws<ArgumentException>(() => HolidayCallHandling.GetCopyCommand(new CopyRequest(AaId, Source, Target), [value]));
    }

    [Fact]
    public void ReadScript_ReturnsCallHandlingSchedulesAndMissingAuthorizedUsers()
    {
        var output = RunPowerShell(FakeTeams, HolidayCallHandling.GetReadCommand([AaId], true, checkAuthorizedUsers: true));
        var state = HolidayCallHandling.ParseState(output);

        var attendant = Assert.Single(state.AutoAttendants);
        Assert.Equal(AaId, attendant.Id);
        Assert.Equal("Main AA", attendant.Name);
        Assert.Equal(2, attendant.HolidayLinks.Count());
        var flow = attendant.FindCallFlow(attendant.HolidayLinkFor(Source)!.CallFlowId)!;
        Assert.Equal(Greeting, Assert.Single(flow.Greetings).Text);
        var option = Assert.Single(flow.Options);
        Assert.Equal("TransferCallToTarget", option.Action);
        Assert.Equal("Automatic", option.Key);
        Assert.Equal("ExternalPstn", option.Target!.Type);
        Assert.Equal("tel:+41441112233", option.Target.Id);

        var source = state.FindSchedule(Source)!;
        Assert.True(source.IsFixed);
        Assert.Equal(new ScheduleWindow(new DateTime(2026, 12, 25), new DateTime(2026, 12, 26)), Assert.Single(source.Windows));
        Assert.Equal([AaId], source.UsedBy);
        Assert.False(state.FindSchedule(Target)!.IsExpired(Now));
        Assert.True(state.FindSchedule(Expired)!.IsExpired(Now));

        Assert.Equal(GoneUser, Assert.Single(state.MissingAuthorizedUsers).UserId);
        Assert.Empty(state.UncheckedAuthorizedUsers);
    }

    [Fact]
    public void CopyScript_AttachesTheTargetWithTheSameCallHandlingAndChangesNothingOnARetry()
    {
        var read = HolidayCallHandling.GetReadCommand([AaId]);
        var copy = HolidayCallHandling.GetCopyCommand(new CopyRequest(AaId, Source, Target), []);
        using var shell = PowerShell.Create();
        Run(shell, FakeTeams);
        var before = HolidayCallHandling.ParseState(Run(shell, read)).FindAutoAttendant(AaId)!;
        var expected = before.FindCallFlow(before.HolidayLinkFor(Source)!.CallFlowId)!;

        var first = Run(shell, copy);
        Assert.Contains("SUCCESS: Schedule " + Target, first);
        var after = HolidayCallHandling.ParseState(Run(shell, read)).FindAutoAttendant(AaId)!;
        var copied = after.FindCallFlow(after.HolidayLinkFor(Target)!.CallFlowId)!;
        Assert.Equal("Feiertage 2027", copied.Name);
        Assert.Empty(HolidayCallHandling.Differences(expected, copied));
        Assert.Equal(before.CallFlows.Count + 1, after.CallFlows.Count);
        Assert.NotNull(after.HolidayLinkFor(Source));

        var second = Run(shell, copy);
        Assert.Contains("Nothing was changed", second);
        Assert.Contains("SUCCESS:", second);
        Assert.Equal("1", Run(shell, "Write-Host $global:pdFakeSaves").Trim());
    }

    [Fact]
    public void CopyScript_TakesTheExpiredScheduleOffAndRemovesMissingAuthorizedUsers()
    {
        using var shell = PowerShell.Create();
        Run(shell, FakeTeams);
        var output = Run(shell, HolidayCallHandling.GetCopyCommand(new CopyRequest(AaId, Source, Target, Expired), [GoneUser]));
        Assert.Contains("SUCCESS:", output);

        var state = HolidayCallHandling.ParseState(Run(shell, HolidayCallHandling.GetReadCommand([AaId])));
        var attendant = state.FindAutoAttendant(AaId)!;
        Assert.NotNull(attendant.HolidayLinkFor(Target));
        Assert.Null(attendant.HolidayLinkFor(Expired));
        Assert.Equal(2, attendant.CallFlows.Count);
        Assert.NotNull(state.FindSchedule(Expired));
        Assert.Equal([KeptUser], attendant.AuthorizedUsers);
    }

    [Fact]
    public void CopyScript_ReportsAnErrorWhenTheSourceIsNotAttached()
    {
        var output = RunPowerShell(FakeTeams, HolidayCallHandling.GetCopyCommand(new CopyRequest(AaId, Other, Target), []));
        Assert.Contains("ERROR: Failed to copy the holiday call handling", output);
        Assert.DoesNotContain("SUCCESS:", output);
    }

    [Fact]
    public void Assess_RefusesTwoHolidaysThatStartAtTheSameTime()
    {
        var state = Parse(State(
            [Flow(SourceFlow, Greeting)],
            [Link(Source, SourceFlow)],
            [Schedule(Source, "2026", "2027-01-01T00:00:00", "2027-01-02T00:00:00"), Schedule(Target, "2027", "2027-01-01T00:00:00", "2027-01-03T00:00:00")]));

        var refused = HolidayCallHandling.Assess(state, new CopyRequest(AaId, Source, Target, AcceptOverlaps: true), Now);

        Assert.Equal("sameStartTime", Assert.Single(refused.Blockers).Code);
        Assert.True(Assert.Single(refused.Overlaps).SameStart);
        Assert.False(refused.CanSave);
    }

    [Fact]
    public void Assess_ShowsAnOverlapWithItsCallHandlingAndAcceptsItOnlyWhenAsked()
    {
        // A closure over several days with a single holiday inside it.
        var state = Parse(State(
            [Flow(SourceFlow, Greeting), Flow(NewFlow, "Betriebsferien")],
            [Link(Source, SourceFlow), Link(Other, NewFlow)],
            [
                Schedule(Source, "2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00"),
                Schedule(Other, "Betriebsferien", "2027-12-24T12:00:00", "2028-01-03T00:00:00"),
                Schedule(Target, "2027", "2027-12-25T00:00:00", "2027-12-26T00:00:00")
            ]));

        var refused = HolidayCallHandling.Assess(state, new CopyRequest(AaId, Source, Target), Now);
        var accepted = HolidayCallHandling.Assess(state, new CopyRequest(AaId, Source, Target, AcceptOverlaps: true), Now);

        Assert.Equal("overlap", Assert.Single(refused.Blockers).Code);
        var overlap = Assert.Single(refused.Overlaps);
        Assert.Equal(Other, overlap.OtherSchedule.Id);
        Assert.Contains("Betriebsferien", HolidayCallHandling.Describe(overlap.OtherCallFlow));
        Assert.True(accepted.CanSave);
    }

    [Fact]
    public void Assess_StopsAtTheCallFlowLimitUntilAnExpiredScheduleIsReplaced()
    {
        var flows = Enumerable.Range(0, HolidayCallHandling.AdditionalCallFlowLimit)
            .Select(index => Flow($"f0000000-0000-0000-0000-{index:D12}", "flow " + index))
            .ToList();
        var state = Parse(State(
            flows,
            [Link(Source, "f0000000-0000-0000-0000-000000000000"), Link(Expired, "f0000000-0000-0000-0000-000000000001")],
            [
                Schedule(Source, "2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00"),
                Schedule(Expired, "2020", "2020-12-25T00:00:00", "2020-12-26T00:00:00", AaId, Other),
                Schedule(Target, "2027", "2027-12-25T00:00:00", "2027-12-26T00:00:00")
            ]));

        var full = HolidayCallHandling.Assess(state, new CopyRequest(AaId, Source, Target), Now);
        var replaced = HolidayCallHandling.Assess(state, new CopyRequest(AaId, Source, Target, Expired), Now);
        var notExpired = HolidayCallHandling.Assess(state, new CopyRequest(AaId, Expired, Target, Source), Now);

        Assert.Equal("callFlowLimit", Assert.Single(full.Blockers).Code);
        Assert.Equal(Expired, Assert.Single(full.ExpiredSchedules).Id);
        Assert.True(replaced.CanSave);
        Assert.Equal(Expired, replaced.ReplacedSchedule!.Id);
        Assert.Contains(notExpired.Blockers, blocker => blocker.Code == "replaceNotExpired");
    }

    [Fact]
    public void Assess_NamesMissingAuthorizedUsersAndRemovesThemOnlyWhenAsked()
    {
        var state = Parse(State(
            [Flow(SourceFlow, Greeting)],
            [Link(Source, SourceFlow)],
            [Schedule(Source, "2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00"), Schedule(Target, "2027", "2027-12-25T00:00:00", "2027-12-26T00:00:00")],
            missingUsers: [GoneUser]));

        var refused = HolidayCallHandling.Assess(state, new CopyRequest(AaId, Source, Target), Now);
        var allowed = HolidayCallHandling.Assess(state, new CopyRequest(AaId, Source, Target, RemoveMissingAuthorizedUsers: true), Now);

        var blocker = Assert.Single(refused.Blockers);
        Assert.Equal("missingAuthorizedUsers", blocker.Code);
        Assert.Contains(GoneUser, blocker.Message);
        Assert.Empty(refused.AuthorizedUsersToRemove);
        Assert.True(allowed.CanSave);
        Assert.Equal([GoneUser], allowed.AuthorizedUsersToRemove);
    }

    [Fact]
    public void Differences_FindAChangedApostropheAndAChangedTarget()
    {
        var expected = Parse(State([Flow(SourceFlow, Greeting)], [], [])).AutoAttendants[0].CallFlows[0];
        var apostrophe = Parse(State([Flow(SourceFlow, Greeting.Replace('’', '\''))], [], [])).AutoAttendants[0].CallFlows[0];
        var target = Parse(State([Flow(SourceFlow, Greeting, "tel:+41449998877")], [], [])).AutoAttendants[0].CallFlows[0];

        Assert.Empty(HolidayCallHandling.Differences(expected, expected));
        Assert.Contains("greeting text", Assert.Single(HolidayCallHandling.Differences(expected, apostrophe)));
        Assert.Contains("target", Assert.Single(HolidayCallHandling.Differences(expected, target)));
    }

    [Fact]
    public async Task Copy_ShowsThePlanThenSavesAndVerifiesAgainstTheTenant()
    {
        using var host = HostWith(BeforeCopy());
        var copyScript = HolidayCallHandling.GetCopyCommand(new CopyRequest(AaId, Source, Target), []);
        host.PowerShell.Respond(copyScript, $"SUCCESS: Schedule {Target} is attached");
        host.PowerShell.AfterRun = script =>
        {
            if (script == copyScript)
            {
                RespondState(host, AfterCopy(Greeting));
            }
        };
        var tools = host.Tool<HolidayCallHandlingTools>();

        var planned = McpTestHost.Parse(await tools.CopyHolidayCallHandling(AaId, Source, Target, cancellationToken: Cancellation));
        var plan = planned.GetProperty("plan");
        Assert.Equal("planned", plan.GetProperty("status").GetString());
        Assert.Contains(Greeting, planned.GetProperty("preview").GetProperty("source").GetProperty("callHandling").GetString());
        Assert.Equal("+41 21 111", planned.GetProperty("preview").GetProperty("autoAttendant").GetProperty("phoneNumbers")[0].GetString());
        Assert.DoesNotContain(copyScript, host.PowerShell.Executed);

        var applied = McpTestHost.Parse(await tools.CopyHolidayCallHandling(
            AaId, Source, Target, planId: plan.GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Equal("applied", applied.GetProperty("status").GetString());
        var verification = applied.GetProperty("verification");
        Assert.True(verification.GetProperty("attached").GetBoolean());
        Assert.True(verification.GetProperty("sameAsSource").GetBoolean());
        Assert.Contains(copyScript, host.PowerShell.Executed);

        // A retry finds the schedule attached and changes nothing.
        var retry = McpTestHost.Parse(await tools.CopyHolidayCallHandling(AaId, Source, Target, cancellationToken: Cancellation));
        Assert.Equal("alreadyAttached", retry.GetProperty("status").GetString());
        Assert.Single(host.PowerShell.Executed, script => script == copyScript);
    }

    [Fact]
    public async Task Copy_ReportsADifferenceWhenTeamsHoldsAnotherGreeting()
    {
        using var host = HostWith(BeforeCopy());
        var copyScript = HolidayCallHandling.GetCopyCommand(new CopyRequest(AaId, Source, Target), []);
        host.PowerShell.Respond(copyScript, $"SUCCESS: Schedule {Target} is attached");
        host.PowerShell.AfterRun = script =>
        {
            if (script == copyScript)
            {
                RespondState(host, AfterCopy(Greeting.Replace('’', '\'')));
            }
        };
        var tools = host.Tool<HolidayCallHandlingTools>();

        var applied = await PlanAndApplyAsync(planId => tools.CopyHolidayCallHandling(AaId, Source, Target, planId: planId, cancellationToken: Cancellation));

        var verification = applied.GetProperty("verification");
        Assert.True(verification.GetProperty("attached").GetBoolean());
        Assert.False(verification.GetProperty("sameAsSource").GetBoolean());
        Assert.Contains("greeting text", verification.GetProperty("differences")[0].GetString());
    }

    [Fact]
    public async Task Copy_CountsASaveThatTeamsReportedAsAnErrorWhenTheTenantHasIt()
    {
        using var host = HostWith(BeforeCopy());
        var copyScript = HolidayCallHandling.GetCopyCommand(new CopyRequest(AaId, Source, Target), []);
        host.PowerShell.Respond(copyScript, "ERROR: Failed to copy the holiday call handling: Object reference not set", hadErrors: true);
        host.PowerShell.AfterRun = script =>
        {
            if (script == copyScript)
            {
                RespondState(host, AfterCopy(Greeting));
            }
        };
        var tools = host.Tool<HolidayCallHandlingTools>();

        var applied = await PlanAndApplyAsync(planId => tools.CopyHolidayCallHandling(AaId, Source, Target, planId: planId, cancellationToken: Cancellation));

        Assert.True(applied.GetProperty("success").GetBoolean());
        Assert.Contains("a check of the tenant shows the change was made", applied.GetProperty("note").GetString());
        Assert.True(applied.GetProperty("verification").GetProperty("sameAsSource").GetBoolean());
    }

    [Fact]
    public async Task Copy_StaysFailedWhenTheTenantDoesNotHaveTheSchedule()
    {
        using var host = HostWith(BeforeCopy());
        var copyScript = HolidayCallHandling.GetCopyCommand(new CopyRequest(AaId, Source, Target), []);
        host.PowerShell.Respond(copyScript, "SUCCESS: saved\nERROR: Teams refused the save", hadErrors: true);
        var tools = host.Tool<HolidayCallHandlingTools>();

        var applied = await PlanAndApplyAsync(planId => tools.CopyHolidayCallHandling(AaId, Source, Target, planId: planId, cancellationToken: Cancellation));

        Assert.False(applied.GetProperty("success").GetBoolean());
        Assert.Equal("failed", applied.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Copy_MakesNoPlanWhenABlockerIsFound()
    {
        using var host = HostWith(State(
            [Flow(SourceFlow, Greeting)],
            [Link(Source, SourceFlow)],
            [Schedule(Source, "2026", "2099-01-01T00:00:00", "2099-01-02T00:00:00", AaId), Schedule(Target, "2027", "2099-01-01T00:00:00", "2099-01-02T00:00:00")]));
        var tools = host.Tool<HolidayCallHandlingTools>();

        var result = McpTestHost.Parse(await tools.CopyHolidayCallHandling(AaId, Source, Target, cancellationToken: Cancellation));

        Assert.Equal("blocked", result.GetProperty("status").GetString());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("sameStartTime", result.GetProperty("blockers")[0].GetProperty("code").GetString());
        Assert.True(result.GetProperty("preview").GetProperty("overlaps")[0].GetProperty("sameStart").GetBoolean());
        Assert.False(result.TryGetProperty("plan", out _));
    }

    [Fact]
    public async Task Copy_RefusesANameInPlaceOfAnId()
    {
        using var host = HostWith(BeforeCopy());
        var result = McpTestHost.Parse(await host.Tool<HolidayCallHandlingTools>()
            .CopyHolidayCallHandling("Main AA", Source, Target, cancellationToken: Cancellation));
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("not the ID", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HolidayCallHandling_ShowsEachScheduleOfTheAutoAttendantByName()
    {
        using var host = HostWith(BeforeCopy());
        var result = McpTestHost.Parse(await host.Tool<HolidayCallHandlingTools>().HolidayCallHandlingOf("main aa", Cancellation));

        var attendant = result.GetProperty("autoAttendants")[0];
        Assert.Equal(AaId, attendant.GetProperty("id").GetString());
        Assert.Equal(1, attendant.GetProperty("additionalCallFlows").GetInt32());
        var holiday = attendant.GetProperty("holidays")[0];
        Assert.Equal(Source, holiday.GetProperty("schedule").GetProperty("id").GetString());
        Assert.Equal("Main AA", holiday.GetProperty("schedule").GetProperty("usedBy")[0].GetProperty("name").GetString());
        Assert.Contains("TransferCallToTarget to ExternalPstn", holiday.GetProperty("callHandling").GetString());
        Assert.Equal(Greeting, holiday.GetProperty("callFlow").GetProperty("greetings")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ListHolidaySchedules_ReadsSchedulesOnlyAndCanLeaveOutExpiredOnes()
    {
        using var host = HostWith(State(
            [],
            [],
            [
                Schedule(Source, "Feiertage 2099", "2099-12-25T00:00:00", "2099-12-26T00:00:00", AaId),
                Schedule(Expired, "Feiertage 2020", "2020-12-25T00:00:00", "2020-12-26T00:00:00")
            ]));
        var tools = host.Tool<HolidayCallHandlingTools>();

        var all = McpTestHost.Parse(await tools.ListHolidaySchedules(cancellationToken: Cancellation));
        var current = McpTestHost.Parse(await tools.ListHolidaySchedules(includeExpired: false, cancellationToken: Cancellation));

        Assert.Equal(2, all.GetProperty("total").GetInt32());
        Assert.True(all.GetProperty("schedules")[0].GetProperty("expired").GetBoolean());
        Assert.Equal(Source, Assert.Single(current.GetProperty("schedules").EnumerateArray()).GetProperty("id").GetString());
        Assert.Contains(HolidayCallHandling.GetReadCommand(null, includeAutoAttendants: false), host.PowerShell.Executed);
    }

    [Fact]
    public async Task Attach_RefusesAnOverlapAndReportsTheGreetingTeamsHoldsAfterTheSave()
    {
        const string name = "hd-contoso-2027";
        var schedules = new[]
        {
            Schedule(Source, "Betriebsferien", "2099-12-24T00:00:00", "2100-01-03T00:00:00", AaId),
            Schedule(Target, name, "2099-12-25T00:00:00", "2099-12-26T00:00:00")
        };
        using var host = HostWith(State([Flow(SourceFlow, "Betriebsferien")], [Link(Source, SourceFlow)], schedules));
        var tools = host.Tool<ProvisioningTools>();
        var script = host.Get<IPowerShellCommandService>().GetAttachHolidayToAutoAttendantCommand(name, "Main AA", Greeting);
        host.PowerShell.AfterRun = executed =>
        {
            if (executed == script)
            {
                // Teams holds the greeting with a doubled plain apostrophe.
                RespondState(host, State(
                    [Flow(SourceFlow, "Betriebsferien"), Flow(NewFlow, "Nous sommes fermés aujourd''hui.", targetId: null)],
                    [Link(Source, SourceFlow), Link(Target, NewFlow)],
                    schedules));
            }
        };

        var blocked = McpTestHost.Parse(await tools.AttachHolidaySchedule(name, "Main AA", Greeting, cancellationToken: Cancellation));
        Assert.Equal("blocked", blocked.GetProperty("status").GetString());
        Assert.Equal("overlap", blocked.GetProperty("blockers")[0].GetProperty("code").GetString());

        var applied = await PlanAndApplyAsync(planId => tools.AttachHolidaySchedule(
            name, "Main AA", Greeting, acceptOverlaps: true, planId: planId, cancellationToken: Cancellation));
        Assert.Equal("applied", applied.GetProperty("status").GetString());
        var verification = applied.GetProperty("verification");
        Assert.True(verification.GetProperty("attached").GetBoolean());
        Assert.False(verification.GetProperty("greetingAsSent").GetBoolean());
        Assert.Equal("Nous sommes fermés aujourd''hui.", verification.GetProperty("greetingInTeams").GetString());

        var retry = McpTestHost.Parse(await tools.AttachHolidaySchedule(name, "Main AA", Greeting, cancellationToken: Cancellation));
        Assert.Equal("alreadyAttached", retry.GetProperty("status").GetString());
        Assert.Single(host.PowerShell.Executed, executed => executed == script);
    }

    [Fact]
    public async Task Attach_RefusesAScheduleNameThatTwoSchedulesHave()
    {
        using var host = HostWith(State(
            [],
            [],
            [Schedule(Source, "Feiertage", "2099-12-25T00:00:00", "2099-12-26T00:00:00"), Schedule(Target, "Feiertage", "2099-01-01T00:00:00", "2099-01-02T00:00:00")]));

        var result = McpTestHost.Parse(await host.Tool<ProvisioningTools>()
            .AttachHolidaySchedule("Feiertage", "Main AA", Greeting, cancellationToken: Cancellation));

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("2 schedules are named", result.GetProperty("error").GetString());
    }

    private static string BeforeCopy()
        => State(
            [Flow(SourceFlow, Greeting)],
            [Link(Source, SourceFlow)],
            [
                Schedule(Source, "Feiertage 2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00", AaId),
                Schedule(Target, "Feiertage 2099", "2099-12-25T00:00:00", "2099-12-26T00:00:00")
            ]);

    private static string AfterCopy(string greetingInTeams)
        => State(
            [Flow(SourceFlow, Greeting), Flow(NewFlow, greetingInTeams)],
            [Link(Source, SourceFlow), Link(Target, NewFlow)],
            [
                Schedule(Source, "Feiertage 2026", "2026-12-25T00:00:00", "2026-12-26T00:00:00", AaId),
                Schedule(Target, "Feiertage 2099", "2099-12-25T00:00:00", "2099-12-26T00:00:00", AaId)
            ]);

    private static McpTestHost HostWith(string state)
    {
        var host = new McpTestHost().Connected();
        host.PowerShell.Respond(
            host.Get<IPowerShellCommandService>().GetRetrieveTenantTopologyCommand(),
            "TOPRA: RA AA One|raaa-one@contoso.com|ra-obj-2|+41 21 111|AutoAttendant|True\n" +
            $"TOPAA: Main AA|{AaId}|de-DE|W. Europe Standard Time|ra-obj-2||\n" +
            "SUCCESS: Tenant topology retrieved\n");
        RespondState(host, state);
        return host;
    }

    /// <summary>Makes every form of the read script return the state, as one tenant would.</summary>
    private static void RespondState(McpTestHost host, string state)
    {
        foreach (var ids in new string[]?[] { null, [AaId] })
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

    private static async Task<JsonElement> PlanAndApplyAsync(Func<string?, Task<string>> call)
    {
        var planned = McpTestHost.Parse(await call(null));
        var plan = planned.TryGetProperty("plan", out var nested) ? nested : planned;
        Assert.Equal("planned", plan.GetProperty("status").GetString());
        return McpTestHost.Parse(await call(plan.GetProperty("planId").GetString()));
    }

    private static HolidayCallHandlingState Parse(string output) => HolidayCallHandling.ParseState(output);

    private static string State(
        IEnumerable<object> flows,
        IEnumerable<object> links,
        IEnumerable<object> schedules,
        string[]? missingUsers = null)
        => HolidayCallHandling.JsonMarker + JsonSerializer.Serialize(new
        {
            autoAttendants = new[]
            {
                new { id = AaId, name = "Main AA", authorizedUsers = missingUsers ?? [], callFlows = flows, associations = links }
            },
            schedules,
            missingAuthorizedUsers = (missingUsers ?? []).Select(user => new { autoAttendantId = AaId, userId = user }),
            uncheckedAuthorizedUsers = Array.Empty<object>()
        }) + "\nSUCCESS: Holiday call handling retrieved";

    private static object Flow(string id, string greeting, string? targetId = "tel:+41441112233")
        => new
        {
            id,
            name = "call flow",
            greetings = new[] { new { type = "TextToSpeech", text = greeting, audioFile = (object?)null } },
            menuName = "menu",
            menuPrompts = Array.Empty<object>(),
            options = new[]
            {
                new
                {
                    action = targetId is null ? "DisconnectCall" : "TransferCallToTarget",
                    key = "Automatic",
                    voiceResponses = Array.Empty<string>(),
                    target = targetId is null
                        ? null
                        : new { id = targetId, type = "ExternalPstn", enableTranscription = (bool?)null, suppressSystemPrompt = (bool?)null, callPriority = 0 } as object,
                    prompt = (object?)null
                }
            },
            forceListenMenu = false,
            dialByName = false
        };

    private static object Link(string scheduleId, string callFlowId)
        => new { type = "Holiday", scheduleId, callFlowId, enabled = true, priority = 0 };

    private static object Schedule(string id, string name, string start, string end, params string[] usedBy)
        => new { id, name, type = "Fixed", windows = new[] { new { start, end } }, usedBy };

    private static string RunPowerShell(params string[] scripts)
    {
        using var shell = PowerShell.Create();
        return string.Join("\n", scripts.Select(script => Run(shell, script)));
    }

    /// <summary>Runs a script and returns what it wrote with Write-Host.</summary>
    private static string Run(PowerShell shell, string script)
    {
        shell.Commands.Clear();
        shell.Streams.Information.Clear();
        shell.Streams.Error.Clear();
        shell.AddScript(script).Invoke();
        Assert.True(shell.Streams.Error.Count == 0, string.Join("; ", shell.Streams.Error.Select(error => error.ToString())));
        return string.Join("\n", shell.Streams.Information.Select(record => record.MessageData?.ToString()));
    }

    /// <summary>
    /// Stand-ins for the Teams and Graph cmdlets the two scripts call. One auto attendant has last
    /// year's holiday schedule and an expired one attached; the new year's schedule exists unattached.
    /// </summary>
    private const string FakeTeams = """
        $global:pdFakeSaves = 0
        $pdFakeTarget = [pscustomobject]@{ Id = 'tel:+41441112233'; Type = 'ExternalPstn'; EnableTranscription = $null; EnableSharedVoicemailSystemPromptSuppression = $null; CallPriority = 0 }
        $pdFakeOption = [pscustomobject]@{ Action = 'TransferCallToTarget'; DtmfResponse = 'Automatic'; VoiceResponses = $null; CallTarget = $pdFakeTarget; Prompt = $null }
        $pdFakeMenu = [pscustomobject]@{ Name = 'Feiertage 2026'; Prompts = $null; MenuOptions = @($pdFakeOption); DialByNameEnabled = $false }
        $pdFakeGreeting = [pscustomobject]@{ ActiveType = 'TextToSpeech'; TextToSpeechPrompt = "Nous sommes fermés aujourd$([char]0x2019)hui."; AudioFilePrompt = $null }
        $pdFakeFlow = [pscustomobject]@{ Id = 'f1111111-0000-0000-0000-000000000001'; Name = 'Feiertage 2026'; Greetings = @($pdFakeGreeting); Menu = $pdFakeMenu; ForceListenMenuEnabled = $false; RingResourceAccountDelegates = $null; TimeoutThreshold = $null; TimeoutDisconnectPromptType = $null; TimeoutDisconnectPromptCustomText = $null; AiDisclaimerType = $null; AiDisclaimerCustomText = $null }
        $pdFakeOldMenu = [pscustomobject]@{ Name = 'Feiertage 2020'; Prompts = $null; MenuOptions = @([pscustomobject]@{ Action = 'DisconnectCall'; DtmfResponse = 'Automatic'; VoiceResponses = $null; CallTarget = $null; Prompt = $null }); DialByNameEnabled = $false }
        $pdFakeOldFlow = [pscustomobject]@{ Id = 'f3333333-0000-0000-0000-000000000003'; Name = 'Feiertage 2020'; Greetings = $null; Menu = $pdFakeOldMenu; ForceListenMenuEnabled = $false }
        $global:pdFakeAa = [pscustomobject]@{
            Identity = 'aaaaaaaa-0000-0000-0000-000000000001'
            Name = 'Main AA'
            AuthorizedUsers = @([guid]'99999999-0000-0000-0000-000000000009', [guid]'88888888-0000-0000-0000-000000000008')
            CallFlows = @($pdFakeFlow, $pdFakeOldFlow)
            CallHandlingAssociations = @(
                [pscustomobject]@{ Type = 'AfterHours'; ScheduleId = '55555555-0000-0000-0000-000000000005'; CallFlowId = 'f5555555-0000-0000-0000-000000000005'; Enabled = $true; Priority = 1 },
                [pscustomobject]@{ Type = 'Holiday'; ScheduleId = '11111111-0000-0000-0000-000000000001'; CallFlowId = 'f1111111-0000-0000-0000-000000000001'; Enabled = $true; Priority = 2 },
                [pscustomobject]@{ Type = 'Holiday'; ScheduleId = '33333333-0000-0000-0000-000000000003'; CallFlowId = 'f3333333-0000-0000-0000-000000000003'; Enabled = $true; Priority = 3 }
            )
        }
        $global:pdFakeSchedules = @(
            [pscustomobject]@{ Id = '11111111-0000-0000-0000-000000000001'; Name = 'Feiertage 2026'; Type = 'Fixed'; FixedSchedule = [pscustomobject]@{ DateTimeRanges = @([pscustomobject]@{ Start = [datetime]'2026-12-25'; End = [datetime]'2026-12-26' }) }; AssociatedConfigurationIds = @('aaaaaaaa-0000-0000-0000-000000000001') },
            [pscustomobject]@{ Id = '22222222-0000-0000-0000-000000000002'; Name = 'Feiertage 2027'; Type = 'Fixed'; FixedSchedule = [pscustomobject]@{ DateTimeRanges = @([pscustomobject]@{ Start = [datetime]'2027-12-25'; End = [datetime]'2027-12-26' }) }; AssociatedConfigurationIds = $null },
            [pscustomobject]@{ Id = '33333333-0000-0000-0000-000000000003'; Name = 'Feiertage 2020'; Type = 'Fixed'; FixedSchedule = [pscustomobject]@{ DateTimeRanges = @([pscustomobject]@{ Start = [datetime]'2020-12-25'; End = [datetime]'2020-12-26' }) }; AssociatedConfigurationIds = @('aaaaaaaa-0000-0000-0000-000000000001') },
            [pscustomobject]@{ Id = '55555555-0000-0000-0000-000000000005'; Name = 'After Hours Schedule'; Type = 'WeeklyRecurrence'; FixedSchedule = $null; AssociatedConfigurationIds = @('aaaaaaaa-0000-0000-0000-000000000001') }
        )
        function global:Get-CsAutoAttendant {
            [CmdletBinding()] param([string]$Identity, [int]$First, [int]$Skip)
            if ($Identity -and $Identity -ne $global:pdFakeAa.Identity) { throw "Auto attendant $Identity not found" }
            if ($Skip -gt 0) { return }
            $global:pdFakeAa
        }
        function global:Get-CsOnlineSchedule {
            [CmdletBinding()] param([string]$Id)
            if ($Id) { $global:pdFakeSchedules | Where-Object { $_.Id -eq $Id } } else { $global:pdFakeSchedules }
        }
        function global:New-CsAutoAttendantCallFlow {
            [CmdletBinding()] param([string]$Name, $Menu, $Greetings)
            [pscustomobject]@{ Id = 'f2222222-0000-0000-0000-000000000002'; Name = $Name; Greetings = $Greetings; Menu = $Menu; ForceListenMenuEnabled = $false; RingResourceAccountDelegates = $null; TimeoutThreshold = $null; TimeoutDisconnectPromptType = $null; TimeoutDisconnectPromptCustomText = $null; AiDisclaimerType = $null; AiDisclaimerCustomText = $null }
        }
        function global:New-CsAutoAttendantCallHandlingAssociation {
            [CmdletBinding()] param([string]$Type, [string]$ScheduleId, [string]$CallFlowId)
            [pscustomobject]@{ Type = $Type; ScheduleId = $ScheduleId; CallFlowId = $CallFlowId; Enabled = $true; Priority = 0 }
        }
        function global:Set-CsAutoAttendant {
            [CmdletBinding()] param($Instance)
            $global:pdFakeAa = $Instance
            $global:pdFakeSaves++
        }
        function global:Get-MgUser {
            [CmdletBinding()] param([string]$UserId, [string[]]$Property)
            if ($UserId -eq '99999999-0000-0000-0000-000000000009') {
                throw "Resource '$UserId' does not exist or one of its queried reference-property objects are not present."
            }
            [pscustomobject]@{ Id = $UserId }
        }
        """;
}
