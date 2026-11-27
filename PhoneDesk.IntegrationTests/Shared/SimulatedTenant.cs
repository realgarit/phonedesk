using System.Management.Automation;
using PhoneDesk.Operations;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using Xunit;

namespace PhoneDesk.TestShared;

/// <summary>
/// A tenant that exists only in a PowerShell session: stand-ins for the Teams cmdlets that keep one
/// call queue, one auto attendant and its schedule, and change them like Teams does. This file is
/// compiled into both test projects, so the desktop page and the MCP tool run the same scenario
/// against the same stand-ins and must leave the same state.
/// </summary>
public static class SimulatedTenant
{
    public const string QueueName = "Support CQ";
    public const string QueueId = "aaaaaaaa-1111-2222-3333-444444444444";
    public const string AttendantName = "Main AA";
    public const string AttendantId = "bbbbbbbb-1111-2222-3333-444444444444";

    /// <summary>The change to the call queue that both heads make.</summary>
    public static IReadOnlyDictionary<string, string> QueueChanges { get; } = new Dictionary<string, string>
    {
        ["routingMethod"] = "Serial",
        ["agentAlertTime"] = "45",
        ["greetingText"] = "Guten Tag, Contoso's Hotline",
        ["allowOptOut"] = "false",
        ["timeoutAction"] = "Forward",
        ["timeoutTarget"] = "support@contoso.com"
    };

    /// <summary>The change to the auto attendant that both heads make.</summary>
    public static IReadOnlyDictionary<string, string> AttendantChanges { get; } = new Dictionary<string, string>
    {
        ["businessHours"] = "Mon 08:00-17:00",
        ["afterHoursGreetingText"] = "Geschlossen."
    };

    /// <summary>
    /// What the tenant holds after both changes, as the read script of the edit feature shows it.
    /// Each head's test compares its own result with this one text.
    /// </summary>
    public const string ExpectedState =
        "CallQueue.agentAlertTime=45; CallQueue.agentGroups=dddddddd-1111-2222-3333-444444444444; CallQueue.allowOptOut=false; CallQueue.conferenceMode=true; CallQueue.greetingText=Guten Tag, Contoso's Hotline; CallQueue.language=de-DE; CallQueue.overflowAction=DisconnectWithBusy; CallQueue.overflowTarget=; CallQueue.overflowThreshold=50; CallQueue.presenceBasedRouting=false; CallQueue.routingMethod=Serial; CallQueue.timeoutAction=Forward; CallQueue.timeoutTarget=cccccccc-1111-2222-3333-444444444444; CallQueue.timeoutThreshold=1200; " +
        "AutoAttendant.afterHoursAction=Disconnect; AutoAttendant.afterHoursGreetingText=Geschlossen.; AutoAttendant.afterHoursTarget=; AutoAttendant.businessHours=Mon 08:00-17:00; Tue closed; Wed closed; Thu closed; Fri closed; Sat closed; Sun closed; AutoAttendant.greetingText=Willkommen; AutoAttendant.language=de-DE; AutoAttendant.timeZone=W. Europe Standard Time";

    /// <summary>Fails with the whole state text when the tenant does not hold <see cref="ExpectedState"/>.</summary>
    public static async Task AssertExpectedStateAsync(IPowerShellContextService tenant, CancellationToken cancellationToken)
    {
        var actual = await ReadStateAsync(tenant, cancellationToken);
        Assert.True(string.Equals(ExpectedState, actual, StringComparison.Ordinal), "The tenant holds: " + actual);
    }

    /// <summary>Reads both objects from the simulated tenant and returns every setting as sorted text.</summary>
    public static async Task<string> ReadStateAsync(IPowerShellContextService tenant, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        foreach (var (kind, id) in new[] { (EditObjectKind.CallQueue, QueueId), (EditObjectKind.AutoAttendant, AttendantId) })
        {
            var result = await tenant.ExecuteCommandWithDetailsAsync(ObjectEdit.GetReadCommand(kind, id), null, null, cancellationToken);
            var state = ObjectEdit.Parse(kind, result.Output);
            Assert.False(string.IsNullOrEmpty(state.Id), result.Output);
            lines.AddRange(state.Values.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => $"{kind}.{value.Key}={value.Value}"));
        }

        return string.Join("; ", lines);
    }

    /// <summary>Stand-ins for the Teams cmdlets. They keep one call queue and one auto attendant and change them like Teams does.</summary>
    public const string Teams = """
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

/// <summary>
/// Runs every script in one real PowerShell engine that holds the simulated tenant. It returns
/// what the script wrote, like the app's own host does, so a head that works with it runs its real
/// scripts and reads their real output.
/// </summary>
public sealed class SimulatedTenantPowerShell : IPowerShellContextService
{
    private readonly PowerShell _shell = PowerShell.Create();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SimulatedTenantPowerShell()
    {
        _shell.AddScript(SimulatedTenant.Teams).Invoke();
        Assert.True(_shell.Streams.Error.Count == 0, string.Join("; ", _shell.Streams.Error.Select(error => error.ToString())));
    }

    /// <summary>Every script a head sent, in order.</summary>
    public List<string> Executed { get; } = new();

    public async Task<string> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
        => (await ExecuteCommandWithDetailsAsync(command, null, null, cancellationToken)).Output;

    public async Task<string> ExecuteCommandAsync(string command, Dictionary<string, string>? environmentVariables, CancellationToken cancellationToken = default)
        => (await ExecuteCommandWithDetailsAsync(command, environmentVariables, null, cancellationToken)).Output;

    public async Task<PowerShellExecutionResult> ExecuteCommandWithDetailsAsync(
        string command,
        Dictionary<string, string>? environmentVariables,
        IProgress<PowerShellProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Executed.Add(command);
            _shell.Commands.Clear();
            _shell.Streams.ClearStreams();
            var output = new List<string>();
            foreach (var item in _shell.AddScript(command).Invoke())
            {
                if (item is not null)
                {
                    output.Add(item.ToString() ?? string.Empty);
                }
            }

            var written = _shell.Streams.Information.Select(record => record.MessageData?.ToString() ?? string.Empty).Concat(output);
            var errors = _shell.Streams.Error
                .Select(error => new PowerShellErrorInfo { Message = error.ToString() })
                .ToList();
            return new PowerShellExecutionResult
            {
                Output = string.Join("\n", written.Concat(errors.Select(error => "ERROR: " + error.Message))),
                HadErrors = errors.Count > 0,
                Errors = errors
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> IsConnectedAsync(string service, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<string> GetConnectionStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult("Connected");

    public void Dispose()
    {
        _shell.Dispose();
        _gate.Dispose();
    }
}
