using System.Management.Automation.Language;
using PhoneDesk.Mcp;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Models;
using PhoneDesk.Operations;
using PhoneDesk.Planning;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// The dry-run preview against the tenant: what exists already, free licenses, and the phone
/// number. The read script runs in a real PowerShell engine against stand-ins for the cmdlets.
/// </summary>
public sealed class TenantPreflightTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void Script_ReadsTheTenantAndTheChecksSayWhatWouldHappen()
    {
        using var host = new McpTestHost().Configured();
        var variables = host.Get<ConfigurationStore>().Variables;
        var sources = new IPhoneManagerVariables[] { variables };
        var script = host.Get<IPowerShellCommandService>().GetTenantPreflightCommand(sources);

        // The values travel encoded: no name is part of the script text.
        Parser.ParseInput(script, out _, out var errors);
        Assert.Empty(errors);
        Assert.DoesNotContain(variables.M365Group, script);
        Assert.DoesNotContain(variables.RaaaUPN, script);

        var state = TenantPreflight.Parse(McpHolidayCallHandlingTests.RunPowerShell(FakeTenant(variables, freeLicenses: 1), script));
        var plan = TenantPreflight.Apply(host.Get<IDryRunPlanBuilder>().BuildWizardPlan(variables), sources, state);
        var checks = plan.Entries[0].PreflightChecks.ToDictionary(check => check.Name, check => check);

        Assert.DoesNotContain(TenantPreflight.NotCheckedName, checks.Keys);
        // The group and the auto attendant account exist: the setup uses them.
        Assert.Equal(PreflightStatus.Warning, checks["Agent group in the tenant"].Status);
        Assert.Equal(PreflightStatus.Warning, checks["Auto attendant resource account in the tenant"].Status);
        Assert.Equal(PreflightStatus.Pass, checks["Call queue resource account in the tenant"].Status);
        Assert.Equal(PreflightStatus.Pass, checks["Call queue in the tenant"].Status);
        Assert.Equal(PreflightStatus.Pass, checks["Auto attendant in the tenant"].Status);
        // One account has the license already, one license is free for the other.
        Assert.Equal(PreflightStatus.Pass, checks["License available"].Status);
        Assert.Contains("needs 1 license", checks["License available"].Detail);
        // An employee has the number.
        Assert.Equal(PreflightStatus.Fail, checks["Phone number available"].Status);
        Assert.Contains("anna@contoso.com", checks["Phone number available"].Detail);
        Assert.False(plan.Entries[0].IsValid);
    }

    [Fact]
    public void Script_LooksForTheNamesAsTheProvisioningScriptsWriteThem()
    {
        using var host = new McpTestHost().Configured();
        var variables = host.Get<ConfigurationStore>().Variables;
        var requested = new List<string>();
        string Recorded(string value)
        {
            requested.Add(value);
            return value.Replace("'", "''", StringComparison.Ordinal);
        }

        var plain = TenantPreflight.GetReadCommand(new IPhoneManagerVariables[] { variables });
        var written = TenantPreflight.GetReadCommand(new IPhoneManagerVariables[] { variables }, Recorded);

        // The three names and the two sign-in names go through the rule of the scripts.
        Assert.Equal(
            new[] { variables.M365Group, variables.CqDisplayName, variables.AaDisplayName, variables.RacqUPN, variables.RaaaUPN }.Order(),
            requested.Order());
        // Without a character the rule changes, the script is the same.
        Assert.Equal(plain, written);
        Assert.Equal(plain, host.Get<IPowerShellCommandService>().GetTenantPreflightCommand(new IPhoneManagerVariables[] { variables }));
    }

    [Fact]
    public void Licenses_AreCountedOverAllEntriesAndAFailedReadIsNotAPass()
    {
        using var host = new McpTestHost().Configured();
        var variables = host.Get<ConfigurationStore>().Variables;
        var sources = new IPhoneManagerVariables[] { variables, variables };
        var builder = host.Get<IDryRunPlanBuilder>();
        PreflightTenantRow Row(int row) => new()
        {
            Row = row,
            Groups = 0,
            CallQueues = 0,
            AutoAttendants = null,
            CallQueueAccount = new PreflightAccount(),
            AutoAttendantAccount = new PreflightAccount(),
            Number = new PreflightNumber { InInventory = true, Type = "DirectRouting" },
            Errors = new[] { "Auto attendant: the service did not answer" }
        };
        var state = new PreflightTenantState
        {
            Rows = new[] { Row(1), Row(2) },
            LicensesRead = true,
            Licenses = new[] { new PreflightLicense { SkuId = variables.SkuId, Name = "PHONESYSTEM_VIRTUALUSER", Available = 2 } }
        };

        var plan = TenantPreflight.Apply(builder.BuildBulkPlan(sources), sources, state);
        PreflightCheck Check(int entry, string name) => plan.Entries[entry].PreflightChecks.Single(check => check.Name == name);

        // Two licenses are free and each entry needs two: the second entry gets none.
        Assert.Equal(PreflightStatus.Pass, Check(0, "License available").Status);
        Assert.Equal(PreflightStatus.Fail, Check(1, "License available").Status);
        Assert.Contains("after the entries before it", Check(1, "License available").Detail);
        // What could not be read is reported as not checked, with the reason.
        Assert.Equal(PreflightStatus.NotChecked, Check(0, "Auto attendant in the tenant").Status);
        Assert.Contains("did not answer", Check(0, "Tenant read").Detail);
        Assert.Equal(PreflightStatus.Pass, Check(0, "Phone number available").Status);

        // No subscription with the license, and licenses that could not be read.
        var none = TenantPreflight.Apply(builder.BuildWizardPlan(variables), sources, new PreflightTenantState { Rows = new[] { Row(1) }, LicensesRead = true });
        Assert.Equal(PreflightStatus.Fail, none.Entries[0].PreflightChecks.Single(check => check.Name == "License available").Status);
        var unread = TenantPreflight.Apply(builder.BuildWizardPlan(variables), sources, new PreflightTenantState { Rows = new[] { Row(1) } });
        Assert.Equal(PreflightStatus.NotChecked, unread.Entries[0].PreflightChecks.Single(check => check.Name == "License available").Status);
    }

    [Theory]
    // Free, usable for a resource account, not blocked.
    [InlineData("", "VoiceApplicationAssignment", "", PreflightStatus.Pass, "not assigned")]
    // Free, but Teams blocks the assignment.
    [InlineData("", "VoiceApplicationAssignment", "BlockedForever", PreflightStatus.Fail, "blocks the number")]
    // Free, but for users only.
    [InlineData("", "UserAssignment", "", PreflightStatus.Fail, "cannot be used for a resource account")]
    // Both conditions on one number: the block is named first.
    [InlineData("", "UserAssignment", "BlockedForever", PreflightStatus.Fail, "blocks the number")]
    // A number that reports no capability is not refused for that.
    [InlineData("", "", "", PreflightStatus.Pass, "not assigned")]
    // The auto attendant account of this plan has it already: the block and the capability do not matter.
    [InlineData("aa-account", "UserAssignment", "BlockedForever", PreflightStatus.Pass, "already assigned")]
    public void Number_IsAvailableOnlyWhenTeamsLetsAResourceAccountHaveIt(
        string assignedTo, string capability, string blockedState, PreflightStatus expected, string detail)
    {
        using var host = new McpTestHost().Configured();
        var variables = host.Get<ConfigurationStore>().Variables;
        var sources = new IPhoneManagerVariables[] { variables };
        var script = host.Get<IPowerShellCommandService>().GetTenantPreflightCommand(sources);
        var tenant = FakeTenant(variables, freeLicenses: 2)
            .Replace("AssignedPstnTargetId = 'employee-1'", $"AssignedPstnTargetId = '{assignedTo}'; Capability = @({(capability.Length == 0 ? string.Empty : "'" + capability + "'")}); AssignmentBlockedState = '{blockedState}'", StringComparison.Ordinal);
        Assert.Contains("AssignmentBlockedState", tenant);

        // The values come through the real script, not from a hand-made state.
        var state = TenantPreflight.Parse(McpHolidayCallHandlingTests.RunPowerShell(tenant, script));
        var plan = TenantPreflight.Apply(host.Get<IDryRunPlanBuilder>().BuildWizardPlan(variables), sources, state);
        var check = plan.Entries[0].PreflightChecks.Single(item => item.Name == "Phone number available");

        Assert.Equal(expected, check.Status);
        Assert.Contains(detail, check.Detail);
    }

    [Fact]
    public async Task Previews_ReadTheTenantOnlyWhenAsked()
    {
        using var host = new McpTestHost().Connected().Configured();
        var variables = host.Get<ConfigurationStore>().Variables;
        var script = host.Get<IPowerShellCommandService>().GetTenantPreflightCommand(new IPhoneManagerVariables[] { variables });
        host.PowerShell.Respond(script, McpHolidayCallHandlingTests.RunPowerShell(FakeTenant(variables, freeLicenses: 0), script));
        var tools = host.Tool<GuidedSetupTools>();

        var offline = await tools.Preview(cancellationToken: Cancellation);
        Assert.Contains("NotChecked", offline);
        Assert.DoesNotContain(script, host.PowerShell.Executed);

        var live = await tools.Preview(checkTenant: true, cancellationToken: Cancellation);
        Assert.Contains(script, host.PowerShell.Executed);
        Assert.Contains("only 0 are free", live);
        Assert.Contains("is assigned to anna@contoso.com", live);

        // The bulk preview needs no connection without the tenant check.
        using var offlineHost = new McpTestHost();
        var bulk = offlineHost.Tool<BulkTools>();
        var template = McpTestHost.Parse(bulk.Template()).GetProperty("template").GetString()!;
        var preview = await bulk.BulkSetupPreview(template, cancellationToken: Cancellation);
        Assert.Contains("NotChecked", preview);
        var refused = McpTestHost.Parse(await bulk.BulkSetupPreview(template, checkTenant: true, cancellationToken: Cancellation));
        Assert.Contains("Not connected", refused.GetProperty("error").GetString());
    }

    /// <summary>
    /// A tenant that has the agent group and the auto attendant resource account (with its license)
    /// of the configuration, and where an employee has the phone number.
    /// </summary>
    private static string FakeTenant(IPhoneManagerVariables variables, int freeLicenses) => FakeTenantTemplate
        .Replace("__SKU__", variables.SkuId, StringComparison.Ordinal)
        .Replace("__RAAA__", variables.RaaaUPN, StringComparison.Ordinal)
        .Replace("__GROUP__", variables.M365Group, StringComparison.Ordinal)
        .Replace("__USED__", (5 - freeLicenses).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private const string FakeTenantTemplate = """
        function Get-MgSubscribedSku { [CmdletBinding()] param([switch]$All)
            [pscustomobject]@{ SkuId = '__SKU__'; SkuPartNumber = 'PHONESYSTEM_VIRTUALUSER'; PrepaidUnits = [pscustomobject]@{ Enabled = 5 }; ConsumedUnits = __USED__ }
        }
        function Get-MgUser { [CmdletBinding()] param($Filter, $Property)
            if ($Filter -eq "userPrincipalName eq '__RAAA__'") {
                [pscustomobject]@{ Id = 'aa-account'; UserPrincipalName = '__RAAA__'; AssignedLicenses = @([pscustomobject]@{ SkuId = '__SKU__' }) }
            }
        }
        function Get-MgGroup { [CmdletBinding()] param($Filter, [switch]$All)
            if ($Filter -eq "displayName eq '__GROUP__'") { [pscustomobject]@{ Id = 'group-1' } }
        }
        function Get-CsCallQueue { [CmdletBinding()] param($NameFilter) }
        function Get-CsAutoAttendant { [CmdletBinding()] param($NameFilter) }
        function Get-CsPhoneNumberAssignment { [CmdletBinding()] param($TelephoneNumber)
            [pscustomobject]@{ TelephoneNumber = $TelephoneNumber; NumberType = 'DirectRouting'; AssignedPstnTargetId = 'employee-1' }
        }
        function Get-CsOnlineUser { [CmdletBinding()] param($Identity)
            [pscustomobject]@{ UserPrincipalName = 'anna@contoso.com' }
        }
        """;
}
