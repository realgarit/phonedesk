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

    public const string FreeNumber = "+41441110002";
    public const string AnnaNumber = "+41441110001";
    public const string Ben = "ben@contoso.com";

    /// <summary>
    /// What the number inventory holds after both heads gave the free number to Ben and took
    /// Anna's number off her: number, status and owner per row.
    /// </summary>
    public const string ExpectedPhoneState =
        "+41441110001|Unassigned|; +41441110002|UserAssigned|22222222-bbbb-0000-0000-000000000002; +41441110003|Unassigned|";

    /// <summary>Fails with the whole inventory text when it is not <see cref="ExpectedPhoneState"/>.</summary>
    public static async Task AssertExpectedPhoneStateAsync(IPowerShellContextService tenant, CancellationToken cancellationToken)
    {
        var result = await tenant.ExecuteCommandWithDetailsAsync(PhoneNumbers.GetListCommand(), null, null, cancellationToken);
        var actual = string.Join("; ", PhoneNumbers.ParseList(result.Output)
            .OrderBy(row => row.Number, StringComparer.Ordinal)
            .Select(row => $"{row.Number}|{row.Status}|{row.AssignedToId}"));
        Assert.True(string.Equals(ExpectedPhoneState, actual, StringComparison.Ordinal), "The inventory holds: " + actual);
    }

    public const string RunTenantId = "tenant-1";
    public const string RunGroup = "ttgrp-contoso-reception";
    public const string RunGroupId = "11111111-2222-3333-4444-555555555555";
    public const string RunAccount = "racq-contoso-reception@contoso.onmicrosoft.com";
    public const string RunAccountId = "66666666-2222-3333-4444-555555555555";
    public const string RunQueue = "cq-contoso-reception";
    public const string RunQueueId = "77777777-2222-3333-4444-555555555555";

    /// <summary>
    /// What is left after both heads rolled the run back: the objects that were in the tenant before
    /// the run, and none of the three the run created.
    /// </summary>
    public const string ExpectedRollbackState = "groups=ttgrp-fabrikam-sales; accounts=racq-fabrikam-sales@contoso.onmicrosoft.com; queues=cq-fabrikam-sales";

    /// <summary>A failed Guided Setup run that created an agent group, a resource account and a call queue.</summary>
    public static RunRecord SeedRun(RunJournalService journal)
    {
        var record = journal.Begin(RunKind.GuidedSetup, "test", RunTenantId, "Contoso", "contoso - reception");
        record.Objects.Add(new RunObject { Type = RunObjectType.M365Group, Name = RunGroup, ObjectId = RunGroupId, Step = 1 });
        record.Objects.Add(new RunObject { Type = RunObjectType.ResourceAccount, Name = RunAccount, ObjectId = RunAccountId, Step = 2 });
        record.Objects.Add(new RunObject { Type = RunObjectType.CallQueue, Name = RunQueue, ObjectId = RunQueueId, Step = 4 });
        record.State = RunState.Failed;
        journal.Save(record);
        return record;
    }

    /// <summary>Fails with what the tenant holds when it is not <see cref="ExpectedRollbackState"/>.</summary>
    public static async Task AssertExpectedRollbackStateAsync(IPowerShellContextService tenant, CancellationToken cancellationToken)
    {
        var result = await tenant.ExecuteCommandWithDetailsAsync(
            "Write-Host ('groups=' + (@($global:pdRunGroups | ForEach-Object { $_.DisplayName }) -join ',') + '; accounts=' + (@($global:pdRunAccounts | ForEach-Object { $_.UserPrincipalName }) -join ',') + '; queues=' + (@($global:pdRunQueues | ForEach-Object { $_.Name }) -join ','))",
            null, null, cancellationToken);
        Assert.True(string.Equals(ExpectedRollbackState, result.Output.Trim(), StringComparison.Ordinal), "The tenant holds: " + result.Output);
    }

    /// <summary>
    /// A tenant with the three objects of the run and three that were there before it. The stand-ins
    /// remove what the delete scripts of the pages remove, and nothing else.
    /// </summary>
    public const string RunTenant = """
        $global:pdRunGroups = @(
            [pscustomobject]@{ Id = '11111111-2222-3333-4444-555555555555'; DisplayName = 'ttgrp-contoso-reception' },
            [pscustomobject]@{ Id = '11111111-0000-0000-0000-00000000000f'; DisplayName = 'ttgrp-fabrikam-sales' })
        $global:pdRunAccounts = @(
            [pscustomobject]@{ Id = '66666666-2222-3333-4444-555555555555'; UserPrincipalName = 'racq-contoso-reception@contoso.onmicrosoft.com'; Queue = '77777777-2222-3333-4444-555555555555' },
            [pscustomobject]@{ Id = '66666666-0000-0000-0000-00000000000f'; UserPrincipalName = 'racq-fabrikam-sales@contoso.onmicrosoft.com'; Queue = '77777777-0000-0000-0000-00000000000f' })
        $global:pdRunQueues = @(
            [pscustomobject]@{ Identity = '77777777-2222-3333-4444-555555555555'; Name = 'cq-contoso-reception'; ApplicationInstances = @('66666666-2222-3333-4444-555555555555') },
            [pscustomobject]@{ Identity = '77777777-0000-0000-0000-00000000000f'; Name = 'cq-fabrikam-sales'; ApplicationInstances = @('66666666-0000-0000-0000-00000000000f') })
        function global:Get-MgGroup { [CmdletBinding()] param($Filter, [switch]$All, $GroupId)
            if ($GroupId) {
                $pdFound = $global:pdRunGroups | Where-Object { $_.Id -eq $GroupId }
                if (-not $pdFound) { throw "Group $GroupId does not exist." }
                return $pdFound
            }
            $global:pdRunGroups | Where-Object { $Filter -eq ("displayName eq '" + $_.DisplayName + "'") }
        }
        function global:Remove-MgGroup { [CmdletBinding()] param($GroupId)
            if (-not ($global:pdRunGroups | Where-Object { $_.Id -eq $GroupId })) { throw "Group $GroupId does not exist." }
            $global:pdRunGroups = @($global:pdRunGroups | Where-Object { $_.Id -ne $GroupId })
        }
        function global:Get-MgUser { [CmdletBinding()] param($Filter, $Property)
            $global:pdRunAccounts | Where-Object { $Filter -eq ("userPrincipalName eq '" + $_.UserPrincipalName + "'") }
        }
        function global:Remove-MgUser { [CmdletBinding()] param($UserId)
            if (-not ($global:pdRunAccounts | Where-Object { $_.Id -eq $UserId })) { throw "User $UserId does not exist." }
            $global:pdRunAccounts = @($global:pdRunAccounts | Where-Object { $_.Id -ne $UserId })
        }
        function global:Get-CsOnlineApplicationInstance { [CmdletBinding()] param($Identity)
            $pdFound = $global:pdRunAccounts | Where-Object { $_.UserPrincipalName -eq $Identity }
            if (-not $pdFound) { throw "Application instance $Identity was not found." }
            [pscustomobject]@{ ObjectId = $pdFound.Id; UserPrincipalName = $pdFound.UserPrincipalName }
        }
        function global:Get-CsOnlineApplicationInstanceAssociation { [CmdletBinding()] param($Identity)
            $pdFound = $global:pdRunAccounts | Where-Object { $_.Id -eq $Identity }
            if (-not $pdFound -or -not $pdFound.Queue) { throw "No association." }
            [pscustomobject]@{ Id = $pdFound.Id; ConfigurationId = $pdFound.Queue }
        }
        function global:Remove-CsOnlineApplicationInstanceAssociation { [CmdletBinding()] param($Identities)
            foreach ($pdId in @($Identities)) {
                foreach ($pdAccount in @($global:pdRunAccounts | Where-Object { $_.Id -eq $pdId })) { $pdAccount.Queue = $null }
                foreach ($pdQueue in $global:pdRunQueues) { $pdQueue.ApplicationInstances = @($pdQueue.ApplicationInstances | Where-Object { $_ -ne $pdId }) }
            }
        }
        function global:Get-CsCallQueue { [CmdletBinding()] param($NameFilter, $Identity)
            $global:pdRunQueues | Where-Object { (-not $NameFilter -or $_.Name -like "*$NameFilter*") -and (-not $Identity -or $_.Identity -eq $Identity) }
        }
        function global:Remove-CsCallQueue { [CmdletBinding()] param($Identity)
            if (-not ($global:pdRunQueues | Where-Object { $_.Identity -eq $Identity })) { throw "Call queue $Identity does not exist." }
            $global:pdRunQueues = @($global:pdRunQueues | Where-Object { $_.Identity -ne $Identity })
        }
        function global:Get-CsAutoAttendant { [CmdletBinding()] param($NameFilter, $Identity) }
        """;

    /// <summary>
    /// Stand-ins for the two commands that say who a session is signed in as. A session is signed
    /// in only after <see cref="SignInAsync"/> ran in it, like a browser sign-in the operator finished.
    /// </summary>
    public const string ProfileTenant = """
        $global:pdSignedIn = $null
        function global:Get-CsTenant { [CmdletBinding()] param()
            if (-not $global:pdSignedIn) { throw 'You must call the Connect-MicrosoftTeams cmdlet before calling any other cmdlets.' }
            [pscustomobject]@{ TenantId = $global:pdSignedIn.Id; DisplayName = $global:pdSignedIn.Name; VerifiedDomains = @([pscustomobject]@{ Name = $global:pdSignedIn.Domain }) }
        }
        function global:Get-MgContext { [CmdletBinding()] param()
            if (-not $global:pdSignedIn) { return $null }
            [pscustomobject]@{ TenantId = $global:pdSignedIn.Id; Account = $global:pdSignedIn.Account }
        }
        """;

    /// <summary>Stands for a finished sign-in to Teams and Graph in the session that is active now.</summary>
    public static Task SignInAsync(IPowerShellContextService tenant, string customer, CancellationToken cancellationToken)
        => tenant.ExecuteCommandWithDetailsAsync(
            "$global:pdSignedIn = @{ Id = '" + customer + "-tenant-id'; Name = '" + customer + " AG'; Domain = '" + customer.ToLowerInvariant() + ".com'; Account = 'admin@" + customer.ToLowerInvariant() + ".com' }",
            null, null, cancellationToken);

    /// <summary>
    /// The end of the profile scenario in both heads: after Contoso, then Fabrikam, then Contoso
    /// again, the session of Contoso is in use and still signed in as Contoso, with no new sign-in.
    /// </summary>
    public static void AssertBackOnContoso(IPowerShellContextService tenant, ISessionManager session, TenantIdentityGuard identity, string contosoProfileId)
    {
        Assert.Equal(contosoProfileId, tenant.SessionKey);
        Assert.True(session.TeamsConnected);
        Assert.True(session.GraphConnected);
        Assert.Equal("Contoso AG", session.TenantName);
        Assert.Equal("contoso.com", identity.Expectation.Tenant);
        Assert.Empty(identity.Problems);
    }

    /// <summary>The state right after the first switch to a customer: its session has no sign-in yet.</summary>
    public static void AssertNotSignedIn(ISessionManager session)
    {
        Assert.False(session.TeamsConnected);
        Assert.False(session.GraphConnected);
    }

    /// <summary>A tenant with two employees, one resource account and three numbers.</summary>
    public const string PhoneTenant = """
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
/// Runs every script in a real PowerShell engine that holds the simulated tenant. It returns what
/// the script wrote, like the app's own host does, so a head that works with it runs its real
/// scripts and reads their real output. Like the app's host it keeps one engine per session key:
/// what one session holds (a sign-in, a variable) is not there in another.
/// </summary>
public sealed class SimulatedTenantPowerShell : IPowerShellContextService
{
    private readonly Dictionary<string, PowerShell> _sessions = new(StringComparer.Ordinal);
    private readonly string _tenant;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PowerShell _shell;

    /// <param name="tenant">The stand-ins to load. Without it: the call queue and auto attendant of <see cref="SimulatedTenant.Teams"/>.</param>
    public SimulatedTenantPowerShell(string? tenant = null)
    {
        _tenant = tenant ?? SimulatedTenant.Teams;
        _shell = Open(string.Empty);
    }

    public string SessionKey { get; private set; } = string.Empty;

    public void UseSession(string key)
    {
        SessionKey = key ?? string.Empty;
        _shell = _sessions.TryGetValue(SessionKey, out var existing) ? existing : Open(SessionKey);
    }

    public void CloseSession(string key)
    {
        if (!string.Equals(key, SessionKey, StringComparison.Ordinal) && _sessions.Remove(key ?? string.Empty, out var closed))
        {
            closed.Dispose();
        }
    }

    private PowerShell Open(string key)
    {
        var shell = PowerShell.Create();
        shell.AddScript(_tenant).Invoke();
        Assert.True(shell.Streams.Error.Count == 0, string.Join("; ", shell.Streams.Error.Select(error => error.ToString())));
        _sessions[key] = shell;
        return shell;
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
        foreach (var shell in _sessions.Values)
        {
            shell.Dispose();
        }

        _gate.Dispose();
    }
}
