using System.Management.Automation;
using PhoneDesk.Mcp;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// The two script parts that came from a field report after a Guided Setup: opening hours go to
/// the call queue, and the call queue gets the auto attendant number as outbound caller ID.
/// They run in a real PowerShell engine against stand-ins for the Teams cmdlets.
/// </summary>
public sealed class GuidedSetupRoutingTests
{
    private const string CallFlowCmdlets = """
        function New-CsAutoAttendantCallableEntity { param($Identity, $Type) Write-Host "TARGET:$Identity|$Type"; [pscustomobject]@{ Id = $Identity } }
        function New-CsAutoAttendantMenuOption { param($Action, $CallTarget, $DtmfResponse) Write-Host "OPTION:$Action|$($CallTarget.Id)"; [pscustomobject]@{ Action = $Action } }
        function New-CsAutoAttendantMenu { param($Name, $MenuOptions) [pscustomobject]@{ Name = $Name } }
        function New-CsAutoAttendantCallFlow { param($Name, $Menu, $Greetings) [pscustomobject]@{ Name = $Name } }
        """;

    [Fact]
    public void DefaultCallFlow_TransfersToTheCallQueueResourceAccount()
    {
        using var host = new McpTestHost().Configured();
        var script = host.Get<IPowerShellCommandService>().GetCreateDefaultCallFlowCommand(host.Get<ConfigurationStore>().Variables);

        var output = Run(
            CallFlowCmdlets +
            "\nfunction Get-CsOnlineUser { param($Identity, $ErrorAction) if ($Identity -eq 'racq-contoso-reception@contoso.onmicrosoft.com') { [pscustomobject]@{ Identity = 'cq-account-id' } } }\n",
            script);

        Assert.Contains("TARGET:cq-account-id|ApplicationEndpoint", output);
        Assert.Contains("OPTION:TransferCallToTarget|cq-account-id", output);
        Assert.Contains("SUCCESS: Created default call flow", output);
    }

    [Fact]
    public void DefaultCallFlow_StopsWithAClearErrorWhenTheCallQueueAccountDoesNotExist()
    {
        using var host = new McpTestHost().Configured();
        var script = host.Get<IPowerShellCommandService>().GetCreateDefaultCallFlowCommand(host.Get<ConfigurationStore>().Variables);

        var output = Run(CallFlowCmdlets + "\nfunction Get-CsOnlineUser { param($Identity, $ErrorAction) }\n", script);

        // No auto attendant that silently hangs up: the script stops and says what to do.
        Assert.Contains("ERROR: Failed to create default call flow: The call queue resource account 'racq-contoso-reception@contoso.onmicrosoft.com' was not found. Create the call queue first", output);
        Assert.DoesNotContain("OPTION:", output);
        Assert.DoesNotContain("SUCCESS", output);
    }

    [Fact]
    public void CallerIdScript_SetsOnlyTheCallerIdOfTheCallQueueWithTheExactName()
    {
        using var host = new McpTestHost().Configured();
        var variables = host.Get<ConfigurationStore>().Variables;
        var script = host.Get<IPowerShellCommandService>().GetSetCallQueueCallerIdCommand(variables.CqDisplayName, variables.RaaaUPN);

        var output = Run(
            """
            function Get-CsOnlineUser { param($Identity, $ErrorAction) [pscustomobject]@{ Identity = "id-of-$Identity" } }
            function Get-CsCallQueue { param($NameFilter, $ErrorAction)
                [pscustomobject]@{ Name = "$NameFilter-old"; Identity = 'wrong-queue' }
                [pscustomobject]@{ Name = $NameFilter; Identity = 'right-queue' } }
            function Set-CsCallQueue { param($Identity, $OboResourceAccountIds, $ErrorAction) Write-Host "SET:$Identity|$($OboResourceAccountIds -join ',')|$($PSBoundParameters.Keys -join ',')" }
            """,
            script);

        Assert.Contains("SET:right-queue|id-of-raaa-contoso-main-reception@contoso.onmicrosoft.com|Identity,OboResourceAccountIds,ErrorAction", output);
        Assert.Contains("SUCCESS: Agents of call queue cq-contoso-reception can call out", output);
    }

    [Fact]
    public void CallerIdScript_ReportsAnErrorWhenTheCallQueueDoesNotExist()
    {
        using var host = new McpTestHost().Configured();
        var variables = host.Get<ConfigurationStore>().Variables;
        var script = host.Get<IPowerShellCommandService>().GetSetCallQueueCallerIdCommand(variables.CqDisplayName, variables.RaaaUPN);

        var output = Run(
            """
            function Get-CsOnlineUser { param($Identity, $ErrorAction) [pscustomobject]@{ Identity = 'a' } }
            function Get-CsCallQueue { param($NameFilter, $ErrorAction) }
            function Set-CsCallQueue { Write-Host 'SET' }
            """,
            script);

        Assert.Contains("ERROR: Failed to set the outbound caller ID of call queue cq-contoso-reception: Call queue 'cq-contoso-reception' was not found.", output);
        Assert.DoesNotContain("SET", output.Replace("SET the", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("to set", string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    private static string Run(string cmdlets, string script)
    {
        using var shell = PowerShell.Create();
        shell.AddScript(cmdlets).Invoke();
        shell.Commands.Clear();
        shell.AddScript(script).Invoke();
        Assert.True(shell.Streams.Error.Count == 0, string.Join("; ", shell.Streams.Error.Select(error => error.ToString())));
        return string.Join("\n", shell.Streams.Information.Select(record => record.MessageData?.ToString()));
    }
}
