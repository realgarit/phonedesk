using System.Management.Automation.Language;
using System.Text.Json;
using PhoneDesk.Mcp;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// The phone number inventory and the assignment of a number to an employee or a resource
/// account. The scripts run in a real PowerShell engine against stand-ins for the Teams cmdlets.
/// </summary>
public sealed class PhoneNumberTests
{
    private const string Anna = "11111111-aaaa-0000-0000-000000000001";
    private const string Ben = "22222222-bbbb-0000-0000-000000000002";
    private const string Reception = "33333333-cccc-0000-0000-000000000003";
    private const string AnnaNumber = "+41441110001";
    private const string FreeNumber = "+41441110002";
    private const string ServiceNumber = "+41441110003";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A tenant with two employees, one resource account and three numbers.</summary>
    private const string FakeTeams = """
        $global:pdFakeUsers = @(
            [pscustomobject]@{ Identity = '11111111-aaaa-0000-0000-000000000001'; UserPrincipalName = 'anna@contoso.com'; DisplayName = 'Anna Muster'; AccountType = 'User'; LineUri = 'tel:+41441110001'; FeatureTypes = @('Teams', 'PhoneSystem') },
            [pscustomobject]@{ Identity = '22222222-bbbb-0000-0000-000000000002'; UserPrincipalName = 'ben@contoso.com'; DisplayName = 'Ben Beispiel'; AccountType = 'User'; LineUri = ''; FeatureTypes = @('Teams', 'PhoneSystem') },
            [pscustomobject]@{ Identity = '33333333-cccc-0000-0000-000000000003'; UserPrincipalName = 'raaa-contoso-main@contoso.com'; DisplayName = 'raaa-contoso-main'; AccountType = 'ResourceAccount'; LineUri = ''; FeatureTypes = @('VoiceApp') }
        )
        $global:pdFakeNumbers = @(
            [pscustomobject]@{ TelephoneNumber = '+41441110001'; NumberType = 'CallingPlan'; PstnAssignmentStatus = 'UserAssigned'; AssignedPstnTargetId = '11111111-aaaa-0000-0000-000000000001'; City = 'Zürich'; Capability = @('UserAssignment') },
            [pscustomobject]@{ TelephoneNumber = '+41441110002'; NumberType = 'CallingPlan'; PstnAssignmentStatus = 'Unassigned'; AssignedPstnTargetId = $null; City = 'Zürich'; Capability = @('UserAssignment') },
            [pscustomobject]@{ TelephoneNumber = '+41441110003'; NumberType = 'OperatorConnect'; PstnAssignmentStatus = 'Unassigned'; AssignedPstnTargetId = $null; City = ''; Capability = @('VoiceApplicationAssignment') }
        )
        $global:pdFailSetFor = @()
        function global:Get-CsOnlineUser {
            [CmdletBinding()] param([string]$Identity)
            $pdFound = $global:pdFakeUsers | Where-Object { $_.Identity -eq $Identity -or $_.UserPrincipalName -eq $Identity } | Select-Object -First 1
            if (-not $pdFound) { throw "User $Identity not found" }
            $pdFound
        }
        function global:Get-CsPhoneNumberAssignment {
            # The parameters of the real cmdlet: an unknown name fails the call, as it does in Teams.
            [CmdletBinding(PositionalBinding = $false)] param([string]$TelephoneNumber, [string]$TelephoneNumberContain, [string]$PstnAssignmentStatus, [int]$Top = 500, [int]$Skip = 0)
            if ($Top -gt 1000) { throw "Top cannot be greater than 1000." }
            $pdResult = @($global:pdFakeNumbers)
            if ($TelephoneNumber) { $pdResult = @($pdResult | Where-Object { $_.TelephoneNumber -eq $TelephoneNumber }) }
            if ($TelephoneNumberContain) { $pdResult = @($pdResult | Where-Object { ($_.TelephoneNumber -replace '\D', '') -like "*$TelephoneNumberContain*" }) }
            if ($PstnAssignmentStatus) { $pdResult = @($pdResult | Where-Object { $_.PstnAssignmentStatus -eq $PstnAssignmentStatus }) }
            $pdResult | Select-Object -Skip $Skip -First $Top
        }
        function global:Set-CsPhoneNumberAssignment {
            [CmdletBinding()] param([string]$Identity, [string]$PhoneNumber, [string]$PhoneNumberType)
            if ($global:pdFailSetFor -contains $Identity) { throw "The user lacks appropriate licenses." }
            $pdUser = $global:pdFakeUsers | Where-Object { $_.Identity -eq $Identity }
            $pdRow = $global:pdFakeNumbers | Where-Object { $_.TelephoneNumber -eq $PhoneNumber }
            if (-not $pdRow) {
                $pdRow = [pscustomobject]@{ TelephoneNumber = $PhoneNumber; NumberType = $PhoneNumberType; PstnAssignmentStatus = 'Unassigned'; AssignedPstnTargetId = $null; City = ''; Capability = @() }
                $global:pdFakeNumbers += $pdRow
            }
            $pdRow.AssignedPstnTargetId = $Identity
            $pdRow.PstnAssignmentStatus = if ($pdUser.AccountType -eq 'User') { 'UserAssigned' } else { 'VoiceApplicationAssigned' }
            $pdUser.LineUri = "tel:$PhoneNumber"
        }
        function global:Remove-CsPhoneNumberAssignment {
            [CmdletBinding()] param([string]$Identity, [string]$PhoneNumber, [string]$PhoneNumberType)
            $pdRow = $global:pdFakeNumbers | Where-Object { $_.TelephoneNumber -eq $PhoneNumber }
            $pdRow.AssignedPstnTargetId = $null
            $pdRow.PstnAssignmentStatus = 'Unassigned'
            ($global:pdFakeUsers | Where-Object { $_.Identity -eq $Identity }).LineUri = ''
        }
        """;

    private static PhoneNumberCheckState Check(string output) => PhoneNumbers.ParseCheck(output);

    [Fact]
    public void Scripts_ParseAndTakeNoFreeText()
    {
        var state = Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(AnnaNumber, "ben@contoso.com")));
        var scripts = new[]
        {
            PhoneNumbers.GetListCommand("4144", "unassigned"),
            PhoneNumbers.GetCheckCommand(AnnaNumber, "ben@contoso.com"),
            PhoneNumbers.GetChangeCommand(PhoneNumbers.AssessAssign(state, AnnaNumber, null, allowReassign: true)),
            PhoneNumbers.GetChangeCommand(PhoneNumbers.AssessUnassign(state, AnnaNumber))
        };
        foreach (var script in scripts)
        {
            Parser.ParseInput(script, out _, out var errors);
            Assert.True(errors.Length == 0, string.Join("; ", errors.Select(error => error.Message)) + "\n" + script);
            Assert.DoesNotContain("__", script);
        }

        Assert.Throws<ArgumentException>(() => PhoneNumbers.GetCheckCommand("044 111 00 01", null));
        Assert.Throws<ArgumentException>(() => PhoneNumbers.GetCheckCommand(AnnaNumber, "anna'; Remove-CsOnlineUser x; '"));
        Assert.Throws<ArgumentException>(() => PhoneNumbers.GetCheckCommand(AnnaNumber, "Anna Muster"));
        Assert.Throws<ArgumentException>(() => PhoneNumbers.GetListCommand(null, "everything"));
    }

    [Fact]
    public void List_ShowsEveryNumberWithItsOwnerAndFiltersOnDigitsAndFreeNumbers()
    {
        var all = PhoneNumbers.ParseList(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetListCommand()));
        Assert.Equal(3, all.Count);
        var assigned = all.Single(row => row.Number == AnnaNumber);
        Assert.True(assigned.IsAssigned);
        Assert.Equal("anna@contoso.com", assigned.Owner!.UserPrincipalName);
        Assert.Equal(AnnaNumber, assigned.Owner.PhoneNumber);
        Assert.Equal("CallingPlan", assigned.Type);

        var free = PhoneNumbers.ParseList(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetListCommand(status: "unassigned")));
        Assert.Equal(new[] { FreeNumber, ServiceNumber }, free.Select(row => row.Number));
        var byDigits = PhoneNumbers.ParseList(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetListCommand("+41 44 111 00 03")));
        Assert.Equal(ServiceNumber, Assert.Single(byDigits).Number);
        var assignedOnly = PhoneNumbers.ParseList(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetListCommand(status: "assigned")));
        Assert.Equal(AnnaNumber, Assert.Single(assignedOnly).Number);
    }

    [Fact]
    public void List_ReadsALargeInventoryPageByPage()
    {
        // 2300 numbers: Teams returns 1000 per call at most.
        var output = McpHolidayCallHandlingTests.RunPowerShell(
            FakeTeams + """

            $global:pdFakeNumbers = @(1..2300 | ForEach-Object {
                [pscustomobject]@{ TelephoneNumber = ('+4144{0:D7}' -f $_); NumberType = 'CallingPlan'; PstnAssignmentStatus = 'Unassigned'; AssignedPstnTargetId = $null; City = ''; Capability = @('UserAssignment'); AssignmentBlockedState = 'NotBlocked' }
            })
            """,
            PhoneNumbers.GetListCommand());
        var rows = PhoneNumbers.ParseList(output);

        Assert.Equal(2300, rows.Count);
        Assert.Equal(2300, rows.Select(row => row.Number).Distinct().Count());
    }

    [Fact]
    public void Check_RefusesABlockedNumberAndANumberThatBelongsToAPolicy()
    {
        var blocked = Check(McpHolidayCallHandlingTests.RunPowerShell(
            FakeTeams + "\n($global:pdFakeNumbers | Where-Object { $_.TelephoneNumber -eq '" + FreeNumber + "' }) | Add-Member -NotePropertyName AssignmentBlockedState -NotePropertyValue 'BlockedUntil'\n",
            PhoneNumbers.GetCheckCommand(FreeNumber, "ben@contoso.com")));
        Assert.Equal("assignmentBlocked", Assert.Single(PhoneNumbers.AssessAssign(blocked, FreeNumber, null, false).Blockers).Code);

        // A number of a shared calling routing policy has no account as owner.
        var policy = Check(McpHolidayCallHandlingTests.RunPowerShell(
            FakeTeams + "\n$pdPolicyRow = $global:pdFakeNumbers | Where-Object { $_.TelephoneNumber -eq '" + FreeNumber + "' }\n$pdPolicyRow.AssignedPstnTargetId = 'TeamsSharedCallingRoutingPolicy|Tag:SC1'\n$pdPolicyRow.PstnAssignmentStatus = 'PolicyAssigned'\n",
            PhoneNumbers.GetCheckCommand(FreeNumber, "ben@contoso.com")));
        Assert.True(policy.Number!.IsAssignedToAPolicy);
        Assert.Null(policy.Number.Owner);
        Assert.Equal("assignedToPolicy", Assert.Single(PhoneNumbers.AssessAssign(policy, FreeNumber, null, allowReassign: true).Blockers).Code);
        Assert.Equal("assignedToPolicy", Assert.Single(PhoneNumbers.AssessUnassign(policy, FreeNumber).Blockers).Code);
    }

    [Fact]
    public void Assign_GivesAFreeNumberToAnEmployeeAndTheTenantShowsIt()
    {
        var state = Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(FreeNumber, "ben@contoso.com")));
        var assessment = PhoneNumbers.AssessAssign(state, FreeNumber, null, allowReassign: false);
        Assert.True(assessment.CanSave, string.Join(" ", assessment.Blockers.Select(blocker => blocker.Message)));
        Assert.Equal(PhoneNumberChangeKind.Assign, assessment.Kind);
        Assert.Equal("CallingPlan", assessment.Type);

        var output = McpHolidayCallHandlingTests.RunPowerShell(
            FakeTeams, PhoneNumbers.GetChangeCommand(assessment), PhoneNumbers.GetCheckCommand(FreeNumber, Ben));
        Assert.Contains("SUCCESS: " + FreeNumber + " was assigned", output);
        Assert.Equal(string.Empty, PhoneNumbers.Verify(assessment, Check(output)));
    }

    [Fact]
    public void Unassign_TakesTheNumberOffAndKeepsItInTheInventory()
    {
        var state = Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(AnnaNumber, null)));
        var assessment = PhoneNumbers.AssessUnassign(state, AnnaNumber);
        Assert.Equal("anna@contoso.com", assessment.CurrentOwner!.UserPrincipalName);

        var output = McpHolidayCallHandlingTests.RunPowerShell(
            FakeTeams, PhoneNumbers.GetChangeCommand(assessment), PhoneNumbers.GetCheckCommand(AnnaNumber, null));
        var after = Check(output);
        Assert.NotNull(after.Number);
        Assert.False(after.Number!.IsAssigned);
        Assert.Equal(string.Empty, PhoneNumbers.Verify(assessment, after));
        Assert.True(PhoneNumbers.AssessUnassign(after, AnnaNumber).NoChange);
    }

    [Fact]
    public void Move_NeedsAConfirmationShowsBothAccountsAndMovesTheNumber()
    {
        var state = Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(AnnaNumber, "ben@contoso.com")));
        var refused = PhoneNumbers.AssessAssign(state, AnnaNumber, null, allowReassign: false);
        Assert.Equal("assignedElsewhere", Assert.Single(refused.Blockers).Code);
        Assert.Contains("Anna Muster", refused.Blockers[0].Message);

        var move = PhoneNumbers.AssessAssign(state, AnnaNumber, null, allowReassign: true);
        Assert.Equal(PhoneNumberChangeKind.Reassign, move.Kind);
        Assert.Equal("anna@contoso.com", move.CurrentOwner!.UserPrincipalName);
        Assert.Equal("ben@contoso.com", move.Target!.UserPrincipalName);

        var output = McpHolidayCallHandlingTests.RunPowerShell(
            FakeTeams, PhoneNumbers.GetChangeCommand(move), PhoneNumbers.GetCheckCommand(AnnaNumber, Ben));
        Assert.Contains("SUCCESS: " + AnnaNumber + " was moved to the new account", output);
        Assert.Equal(string.Empty, PhoneNumbers.Verify(move, Check(output)));
    }

    [Fact]
    public void Move_GivesTheNumberBackWhenTheNewAccountDoesNotTakeIt()
    {
        var state = Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(AnnaNumber, "ben@contoso.com")));
        var move = PhoneNumbers.AssessAssign(state, AnnaNumber, null, allowReassign: true);

        // Teams refuses the number for the new account, between the two steps of the move.
        var output = McpHolidayCallHandlingTests.RunPowerShell(
            FakeTeams + "\n$global:pdFailSetFor = @('" + Ben + "')\n",
            PhoneNumbers.GetChangeCommand(move),
            PhoneNumbers.GetCheckCommand(AnnaNumber, Ben));
        Assert.Contains("ERROR: Failed to move " + AnnaNumber, output);
        Assert.Contains("The number was given back to the account that had it.", output);
        Assert.DoesNotContain("SUCCESS: " + AnnaNumber + " was moved", output);

        var after = Check(output);
        Assert.Equal(Anna, after.Number!.AssignedToId);
        Assert.Contains("anna@contoso.com", PhoneNumbers.Verify(move, after));

        // When it cannot be given back either, the script says that the number has no account now.
        var lost = McpHolidayCallHandlingTests.RunPowerShell(
            FakeTeams + "\n$global:pdFailSetFor = @('" + Ben + "', '" + Anna + "')\n",
            PhoneNumbers.GetChangeCommand(move),
            PhoneNumbers.GetCheckCommand(AnnaNumber, Ben));
        Assert.Contains("The number is UNASSIGNED now.", lost);
        Assert.Equal(AnnaNumber + " is not assigned to any account.", PhoneNumbers.Verify(move, Check(lost)));
    }

    [Fact]
    public void Move_RunsNothingWhenAnotherAccountHasTheNumberThanThePreviewShowed()
    {
        var state = Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(AnnaNumber, "ben@contoso.com")));
        var move = PhoneNumbers.AssessAssign(state, AnnaNumber, null, allowReassign: true);

        // Between the preview and the run the number went to the resource account.
        var output = McpHolidayCallHandlingTests.RunPowerShell(
            FakeTeams + "\n($global:pdFakeNumbers | Where-Object { $_.TelephoneNumber -eq '" + AnnaNumber + "' }).AssignedPstnTargetId = '" + Reception + "'\n",
            PhoneNumbers.GetChangeCommand(move),
            PhoneNumbers.GetCheckCommand(AnnaNumber, Ben));
        Assert.Contains("is not assigned to the account the preview showed. Nothing was changed.", output);
        Assert.Equal(Reception, Check(output).Number!.AssignedToId);
    }

    [Fact]
    public void Check_RefusesANumberOfTheWrongKindAndAnOperatorNumberThatIsNotInTheInventory()
    {
        // A number for voice applications cannot go to an employee, and a user number not to a resource account.
        var serviceToUser = PhoneNumbers.AssessAssign(
            Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(ServiceNumber, "ben@contoso.com"))), ServiceNumber, null, false);
        Assert.Equal("capability", Assert.Single(serviceToUser.Blockers).Code);
        var userToAccount = PhoneNumbers.AssessAssign(
            Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(FreeNumber, Reception))), FreeNumber, null, false);
        Assert.Equal("capability", Assert.Single(userToAccount.Blockers).Code);
        Assert.True(PhoneNumbers.AssessAssign(
            Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(ServiceNumber, Reception))), ServiceNumber, null, false).CanSave);

        // Only a Direct Routing number can be new.
        const string unknown = "+41441119999";
        var newState = Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(unknown, "ben@contoso.com")));
        Assert.Null(newState.Number);
        Assert.Equal("numberNotInInventory", Assert.Single(PhoneNumbers.AssessAssign(newState, unknown, "CallingPlan", false).Blockers).Code);
        Assert.Equal("numberNotInInventory", Assert.Single(PhoneNumbers.AssessAssign(newState, unknown, null, false).Blockers).Code);
        var directRouting = PhoneNumbers.AssessAssign(newState, unknown, "DirectRouting", false);
        Assert.True(directRouting.CanSave);
        Assert.Equal("DirectRouting", directRouting.Type);

        // A wrong type for a number of the inventory, an account that does not exist, and a number the target has already.
        var wrongType = PhoneNumbers.AssessAssign(
            Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(FreeNumber, "ben@contoso.com"))), FreeNumber, "DirectRouting", false);
        Assert.Equal("typeMismatch", Assert.Single(wrongType.Blockers).Code);
        var noTarget = PhoneNumbers.AssessAssign(
            Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(FreeNumber, "nobody@contoso.com"))), FreeNumber, null, false);
        Assert.Equal("targetNotFound", Assert.Single(noTarget.Blockers).Code);
        var same = PhoneNumbers.AssessAssign(
            Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(AnnaNumber, "anna@contoso.com"))), AnnaNumber, null, false);
        Assert.True(same.NoChange);
        var replaces = PhoneNumbers.AssessAssign(
            Check(McpHolidayCallHandlingTests.RunPowerShell(FakeTeams, PhoneNumbers.GetCheckCommand(FreeNumber, "anna@contoso.com"))), FreeNumber, null, false);
        Assert.Contains(AnnaNumber + " becomes unassigned", Assert.Single(replaces.Warnings));
    }

    [Fact]
    public async Task Tool_AssignsToAnAutoAttendantThroughItsResourceAccountAndRefusesAStalePlan()
    {
        using var host = new McpTestHost().Connected();
        host.PowerShell.Respond(
            host.Get<IPowerShellCommandService>().GetRetrieveTenantTopologyCommand(),
            "TOPRA: raaa-contoso-main|raaa-contoso-main@contoso.com|" + Reception + "||AutoAttendant|True\n" +
            "TOPAA: Main AA|aaaaaaaa-0000-0000-0000-000000000001|de-DE|W. Europe Standard Time|" + Reception + "||\n" +
            "SUCCESS: Tenant topology retrieved\n");
        var check = PhoneNumbers.GetCheckCommand(ServiceNumber, Reception);
        host.PowerShell.Respond(check, State(ServiceNumber, "OperatorConnect", null, "VoiceApplicationAssignment", Target(Reception, "raaa-contoso-main@contoso.com", "ResourceAccount", null)));
        var tools = host.Tool<PhoneNumberTools>();

        // The auto attendant is named; the server takes its resource account.
        var planned = McpTestHost.Parse(await tools.SetPhoneNumberAssignment(ServiceNumber, "Main AA", cancellationToken: Cancellation));
        var preview = planned.GetProperty("preview");
        Assert.Equal("Assign", preview.GetProperty("change").GetString());
        Assert.Equal("resourceAccount", preview.GetProperty("getsTheNumber").GetProperty("kind").GetString());
        var script = planned.GetProperty("plan").GetProperty("script").GetString()!;
        Assert.Contains("Set-CsPhoneNumberAssignment -Identity '" + Reception + "' -PhoneNumber '" + ServiceNumber + "' -PhoneNumberType OperatorConnect", script);

        // Another account takes the number before the plan runs: the plan is refused and nothing is sent.
        host.PowerShell.Respond(check, State(ServiceNumber, "OperatorConnect", Ben, "VoiceApplicationAssignment", Target(Reception, "raaa-contoso-main@contoso.com", "ResourceAccount", null)));
        var stale = McpTestHost.Parse(await tools.SetPhoneNumberAssignment(
            ServiceNumber, "Main AA", planId: planned.GetProperty("plan").GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Equal("blocked", stale.GetProperty("status").GetString());
        Assert.Equal("assignedElsewhere", stale.GetProperty("blockers")[0].GetProperty("code").GetString());
        Assert.DoesNotContain(script, host.PowerShell.Executed);
    }

    [Fact]
    public async Task Tool_ReportsWhoHasTheNumberAfterAMoveThatFailed()
    {
        using var host = new McpTestHost().Connected();
        var check = PhoneNumbers.GetCheckCommand(AnnaNumber, "ben@contoso.com");
        var before = State(AnnaNumber, "CallingPlan", Anna, "UserAssignment", Target(Ben, "ben@contoso.com", "User", null), Target(Anna, "anna@contoso.com", "User", AnnaNumber));
        host.PowerShell.Respond(check, before);
        var tools = host.Tool<PhoneNumberTools>();

        var refused = McpTestHost.Parse(await tools.SetPhoneNumberAssignment(AnnaNumber, "ben@contoso.com", cancellationToken: Cancellation));
        Assert.Equal("blocked", refused.GetProperty("status").GetString());

        var planned = McpTestHost.Parse(await tools.SetPhoneNumberAssignment(AnnaNumber, "ben@contoso.com", reassign: true, cancellationToken: Cancellation));
        Assert.Equal("Reassign", planned.GetProperty("preview").GetProperty("change").GetString());
        Assert.Equal("anna@contoso.com", planned.GetProperty("preview").GetProperty("losesTheNumber").GetProperty("userPrincipalName").GetString());
        Assert.Equal("ben@contoso.com", planned.GetProperty("preview").GetProperty("getsTheNumber").GetProperty("userPrincipalName").GetString());
        var script = planned.GetProperty("plan").GetProperty("script").GetString()!;

        // The new account did not take the number and the script gave it back: the tenant is as before.
        host.PowerShell.Respond(script, "ERROR: Failed to move " + AnnaNumber + ": The number was given back to the account that had it.", hadErrors: true);
        var failed = McpTestHost.Parse(await tools.SetPhoneNumberAssignment(
            AnnaNumber, "ben@contoso.com", reassign: true, planId: planned.GetProperty("plan").GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        var verification = failed.GetProperty("verification");
        Assert.False(verification.GetProperty("asRequested").GetBoolean());
        Assert.Contains("anna@contoso.com", verification.GetProperty("problem").GetString());
        Assert.Equal(Anna, verification.GetProperty("numberNow").GetProperty("assignedToId").GetString());
    }

    [Fact]
    public async Task Tool_UnassignKeepsTheNumberAndListShowsTheInventory()
    {
        using var host = new McpTestHost().Connected();
        var check = PhoneNumbers.GetCheckCommand(AnnaNumber, null);
        host.PowerShell.Respond(check, State(AnnaNumber, "CallingPlan", Anna, "UserAssignment", null, Target(Anna, "anna@contoso.com", "User", AnnaNumber)));
        var tools = host.Tool<PhoneNumberTools>();

        var planned = McpTestHost.Parse(await tools.RemovePhoneNumberAssignment(AnnaNumber, cancellationToken: Cancellation));
        var script = planned.GetProperty("plan").GetProperty("script").GetString()!;
        Assert.Contains("Remove-CsPhoneNumberAssignment -Identity '" + Anna + "'", script);
        Assert.DoesNotContain("Remove-CsOnlineTelephoneNumber", script);
        host.PowerShell.Respond(script, "SUCCESS: " + AnnaNumber + " was taken off the account and stays in the inventory");
        host.PowerShell.AfterRun = executed =>
        {
            if (executed == script)
            {
                host.PowerShell.Respond(check, State(AnnaNumber, "CallingPlan", null, "UserAssignment", null));
            }
        };
        var applied = McpTestHost.Parse(await tools.RemovePhoneNumberAssignment(
            AnnaNumber, planId: planned.GetProperty("plan").GetProperty("planId").GetString(), cancellationToken: Cancellation));
        Assert.Equal("applied", applied.GetProperty("status").GetString());
        Assert.True(applied.GetProperty("verification").GetProperty("asRequested").GetBoolean());
        Assert.False(applied.GetProperty("verification").GetProperty("numberNow").GetProperty("assigned").GetBoolean());

        host.PowerShell.Respond(PhoneNumbers.GetListCommand(null, "unassigned"), PhoneNumbers.ListMarker + JsonSerializer.Serialize(new[]
        {
            new { number = FreeNumber, type = "CallingPlan", status = "Unassigned", assignedToId = "", city = "", capabilities = new[] { "UserAssignment" }, owner = (object?)null },
            new { number = ServiceNumber, type = "OperatorConnect", status = "Unassigned", assignedToId = "", city = "", capabilities = new[] { "VoiceApplicationAssignment" }, owner = (object?)null }
        }) + "\nSUCCESS: Phone numbers retrieved");
        var list = McpTestHost.Parse(await tools.ListPhoneNumbers(status: "unassigned", type: "OperatorConnect", cancellationToken: Cancellation));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(ServiceNumber, list.GetProperty("numbers")[0].GetProperty("number").GetString());
    }

    private static object Target(string id, string upn, string accountType, string? number)
        => new { id, userPrincipalName = upn, displayName = upn.Split('@')[0], accountType, phoneNumber = number ?? string.Empty, featureTypes = new[] { accountType == "User" ? "PhoneSystem" : "VoiceApp" } };

    private static string State(string number, string type, string? assignedTo, string capability, object? target, object? owner = null)
        => PhoneNumbers.CheckMarker + JsonSerializer.Serialize(new
        {
            number = new
            {
                number,
                type,
                status = assignedTo is null ? "Unassigned" : "UserAssigned",
                assignedToId = assignedTo ?? string.Empty,
                city = string.Empty,
                capabilities = new[] { capability },
                owner
            },
            target,
            targetError = (string?)null
        }) + "\nSUCCESS: Phone number retrieved";
}
