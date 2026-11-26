using PhoneDesk.Models;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services;
using System.Text;

namespace PhoneDesk.Services.ScriptBuilders
{
    public class CallQueueScriptBuilder
    {
        private readonly IPowerShellSanitizationService _sanitizer;

        public CallQueueScriptBuilder(IPowerShellSanitizationService sanitizer)
        {
            _sanitizer = sanitizer;
        }

        public string GetRetrieveCallQueuesCommand()
        {
            return @"
try {
    $callQueues = Get-CsCallQueue | Where-Object {$_.Name -like '*" + ConstantsService.Naming.CallQueuePrefix + @"*'}
    if ($callQueues) {
        Write-Host ""SUCCESS: Found $($callQueues.Count) call queues containing '" + ConstantsService.Naming.CallQueuePrefix + @"'""
        foreach ($queue in $callQueues) {
            Write-Host ""CALLQUEUE: $($queue.Name)|$($queue.Identity)|$($queue.RoutingMethod)|$($queue.AgentAlertTime)""
        }
    } else {
        Write-Host ""INFO: No call queues found containing '" + ConstantsService.Naming.CallQueuePrefix + @"'""
    }
}
catch {
    Write-Host ""ERROR: Failed to retrieve call queues: $_""
}";
        }

        /// <summary>
        /// Guided Setup step: creates the call queue for the configured M365 group and associates the
        /// call queue resource account with it. The resource account and its license are separate steps.
        /// Safe to run again: an existing call queue is updated to the current Configuration and an existing
        /// association is kept, instead of creating duplicates.
        /// </summary>
        public string GetCreateCallQueueCommand(IPhoneManagerVariables variables)
        {
            var callQueueParams = BuildCallQueueParameters(variables);
            // SECURITY: Sanitize all user inputs and wrap in quotes
            var sanitizedRacqUPN = _sanitizer.SanitizeString(variables.RacqUPN);
            var sanitizedCqDisplayName = _sanitizer.SanitizeString(variables.CqDisplayName);
            var sanitizedGroupName = _sanitizer.SanitizeString(variables.M365Group);
            var sanitizedLanguageId = _sanitizer.SanitizeString(variables.LanguageId);

            return $@"
try {{
    $cqGroupId = $global:m365groupId
    if (-not $cqGroupId) {{
        $cqGroup = Get-MgGroup -Filter ""displayName eq '{sanitizedGroupName}'"" -ErrorAction Stop | Select-Object -First 1
        if ($cqGroup) {{
            $cqGroupId = $cqGroup.Id
            $global:m365groupId = $cqGroupId
        }}
    }}
    if (-not $cqGroupId) {{
        throw ""Microsoft 365 group '{sanitizedGroupName}' was not found. Run the M365 group step first.""
    }}

    $cqApplicationInstance = Get-CsOnlineUser ""{sanitizedRacqUPN}"" -ErrorAction Stop
    if (-not $cqApplicationInstance) {{
        throw ""Resource account '{sanitizedRacqUPN}' was not found. Run the call queue resource account step first.""
    }}

    $cqExisting = Get-CsCallQueue -NameFilter ""{sanitizedCqDisplayName}"" -ErrorAction Stop | Where-Object {{ $_.Name -eq ""{sanitizedCqDisplayName}"" }} | Select-Object -First 1
    if ($cqExisting) {{
        # Running the step again applies the current Configuration to the existing call queue.
        $cqId = $cqExisting.Identity
        Set-CsCallQueue `
        -Identity $cqId `
        -RoutingMethod Attendant `
        -AllowOptOut $true `
        -ConferenceMode $true `
        -AgentAlertTime {ConstantsService.CallQueue.AgentAlertTime} `
        -LanguageId ""{sanitizedLanguageId}"" `
        -DistributionLists @($cqGroupId) `
        {callQueueParams}
        -PresenceBasedRouting $false `
        -ErrorAction Stop | Out-Null
        Write-Host ""INFO: Call queue {sanitizedCqDisplayName} already existed and was updated to the current Configuration.""
    }}
    else {{
        $cqNew = New-CsCallQueue `
        -Name ""{sanitizedCqDisplayName}"" `
        -RoutingMethod Attendant `
        -AllowOptOut $true `
        -ConferenceMode $true `
        -AgentAlertTime {ConstantsService.CallQueue.AgentAlertTime} `
        -LanguageId ""{sanitizedLanguageId}"" `
        -DistributionLists @($cqGroupId) `
        {callQueueParams}
        -PresenceBasedRouting $false `
        -ErrorAction Stop
        $cqId = $cqNew.Identity

        Write-Host ""{ConstantsService.Messages.WaitingMessage}""
        Start-Sleep -Seconds {ConstantsService.PowerShell.DefaultWaitTimeSeconds}
    }}

    $cqAssociation = $null
    try {{
        $cqAssociation = Get-CsOnlineApplicationInstanceAssociation -Identity $cqApplicationInstance.Identity -ErrorAction Stop
    }}
    catch {{
        $cqAssociation = $null
    }}
    if ($cqAssociation -and $cqAssociation.ConfigurationId -eq $cqId) {{
        Write-Host ""INFO: Resource account {sanitizedRacqUPN} is already associated with call queue {sanitizedCqDisplayName}.""
    }}
    else {{
        New-CsOnlineApplicationInstanceAssociation -Identities @($cqApplicationInstance.Identity) -ConfigurationId $cqId -ConfigurationType CallQueue -ErrorAction Stop | Out-Null
    }}

    Write-Host ""SUCCESS: Call queue {sanitizedCqDisplayName} is ready and associated with {sanitizedRacqUPN}""
}}
catch {{
    Write-Host ""ERROR: Failed to create call queue: $_""
}}";
        }

        public string GetCreateCallQueueCommand(string name, string languageId, string m365GroupId, IPhoneManagerVariables? variables = null)
        {
            var callQueueParams = variables != null ? BuildCallQueueParameters(variables) : BuildDefaultCallQueueParameters();

            // SECURITY: Sanitize all inputs
            var sanitizedName = _sanitizer.SanitizeString(name);
            var sanitizedLanguageId = _sanitizer.SanitizeString(languageId);
            var sanitizedM365GroupId = _sanitizer.SanitizeString(m365GroupId);

            return $@"
try {{
    # Create Call Queue in one step
    New-CsCallQueue `
    -Name ""{sanitizedName}"" `
    -RoutingMethod Attendant `
    -AllowOptOut $true `
    -ConferenceMode $true `
    -AgentAlertTime 30 `
    -LanguageId ""{sanitizedLanguageId}"" `
    -DistributionLists @(""{sanitizedM365GroupId}"") `
    {callQueueParams}
    -PresenceBasedRouting $false

    Write-Host ""SUCCESS: Call queue created successfully""
}}
catch {{
    Write-Host ""ERROR: Failed to create call queue: $_""
}}";
        }

        private string BuildDefaultCallQueueParameters()
        {
            return $@"-OverflowThreshold " + ConstantsService.CallQueue.OverflowThreshold + @" `
-OverflowAction DisconnectWithBusy `
-TimeoutThreshold " + ConstantsService.CallQueue.TimeoutThreshold + @" `
-TimeoutAction Disconnect `
-UseDefaultMusicOnHold $true `";
        }

        public string GetAssociateResourceAccountWithCallQueueCommand(string resourceAccountUpn, string callQueueName)
        {
            var sanitizedUpn = _sanitizer.SanitizeString(resourceAccountUpn);
            var sanitizedQueueName = _sanitizer.SanitizeString(callQueueName);
            
            return $@"
try {{
    $cqapplicationInstanceId = (Get-CsOnlineUser ""{sanitizedUpn}"").Identity
    $cqautoAttendantId = (Get-CsCallQueue -NameFilter ""{sanitizedQueueName}"").Identity
    New-CsOnlineApplicationInstanceAssociation -Identities @($cqapplicationInstanceId) -ConfigurationId $cqautoAttendantId -ConfigurationType CallQueue
    Write-Host ""SUCCESS: Associated resource account with call queue""
}}
catch {{
    Write-Host ""ERROR: Failed to associate resource account with call queue: $_""
}}";
        }

        public string GetValidateCallQueueResourceAccountCommand(string racqUpn)
        {
            var sanitizedUpn = _sanitizer.SanitizeString(racqUpn);
            
            return $@"
try {{
    $racqUser = Get-CsOnlineUser ""{sanitizedUpn}""
    if ($racqUser) {{
        Write-Host ""SUCCESS: Call Queue resource account found and validated""
        Write-Host ""Account Details: $($racqUser.DisplayName) - $($racqUser.UserPrincipalName)""
    }} else {{
        Write-Host ""ERROR: Call Queue resource account not found. Please create it first.""
    }}
}}
catch {{
    Write-Host ""ERROR: Failed to validate Call Queue resource account: $_""
}}";
        }

        public string GetCreateCallTargetCommand(string racqUpn)
        {
            var sanitizedUpn = _sanitizer.SanitizeString(racqUpn);
            
            return $@"
try {{
    $racqUser = Get-CsOnlineUser ""{sanitizedUpn}""
    if (-not $racqUser) {{
        throw ""Resource account not found. Please ensure it was created successfully.""
    }}
    $racqid = $racqUser.Identity
    $aacalltarget = New-CsAutoAttendantCallableEntity -Identity $racqid -Type ApplicationEndpoint
    Write-Host ""SUCCESS: Created call target""
}}
catch {{
    Write-Host ""ERROR: Failed to create call target: $_""
}}";
        }

        /// <summary>
        /// Removes a call queue by name.
        /// </summary>
        public string GetRemoveCallQueueCommand(string callQueueName)
        {
            var sanitizedName = _sanitizer.SanitizeString(callQueueName);
            
            return $@"
try {{
    $cq = Get-CsCallQueue -NameFilter ""{sanitizedName}"" -ErrorAction Stop | Where-Object {{$_.Name -eq ""{sanitizedName}""}} | Select-Object -First 1
    if (-not $cq) {{
        Write-Host ""ERROR: Call queue '{sanitizedName}' not found""
    }} else {{
        # Release linked resource accounts first, so they can be reused or deleted afterwards.
        if ($cq.ApplicationInstances) {{
            Remove-CsOnlineApplicationInstanceAssociation -Identities @($cq.ApplicationInstances) -ErrorAction Stop | Out-Null
        }}
        Remove-CsCallQueue -Identity $cq.Identity -ErrorAction Stop
        Write-Host ""SUCCESS: Call queue '{sanitizedName}' removed successfully""
    }}
}}
catch {{
    Write-Host ""ERROR: Failed to remove call queue '{sanitizedName}': $_""
}}";
        }

        /// <summary>
        /// Removes a resource account (application instance) by UPN: releases its call queue or auto attendant
        /// association, then deletes the user object. The Teams module has no Remove-CsOnlineUser cmdlet;
        /// Microsoft Graph moves the account to Deleted users, where it can be restored for 30 days.
        /// </summary>
        public string GetRemoveResourceAccountCommand(string upn)
        {
            var sanitizedUpn = _sanitizer.SanitizeString(upn);

            return $@"
try {{
    $resourceAccount = Get-CsOnlineApplicationInstance -Identity ""{sanitizedUpn}"" -ErrorAction Stop
    $association = $null
    try {{
        $association = Get-CsOnlineApplicationInstanceAssociation -Identity $resourceAccount.ObjectId -ErrorAction Stop
    }}
    catch {{
        $association = $null
    }}
    if ($association -and $association.ConfigurationId) {{
        Remove-CsOnlineApplicationInstanceAssociation -Identities @($resourceAccount.ObjectId) -ErrorAction Stop | Out-Null
    }}
    Remove-MgUser -UserId $resourceAccount.ObjectId -ErrorAction Stop
    Write-Host ""SUCCESS: Resource account '{sanitizedUpn}' removed successfully""
}}
catch {{
    Write-Host ""ERROR: Failed to remove resource account '{sanitizedUpn}': $_""
}}";
        }

        private string BuildCallQueueParameters(IPhoneManagerVariables variables)
        {
            var parameters = new StringBuilder();

            AppendGreetingParameters(parameters, variables);
            AppendMusicOnHoldParameters(parameters, variables);
            AppendOverflowParameters(parameters, variables);
            AppendTimeoutParameters(parameters, variables);
            AppendNoAgentParameters(parameters, variables);

            return parameters.ToString().TrimEnd();
        }

        private void AppendGreetingParameters(StringBuilder parameters, IPhoneManagerVariables variables)
        {
            if (variables.CqGreetingType == "AudioFile" && !string.IsNullOrWhiteSpace(variables.CqGreetingAudioFileId))
            {
                parameters.AppendLine($"-WelcomeMusicAudioFileId \"{_sanitizer.SanitizeString(variables.CqGreetingAudioFileId)}\" `");
            }
            else if (variables.CqGreetingType == "TextToSpeech" && !string.IsNullOrWhiteSpace(variables.CqGreetingTextToSpeechPrompt))
            {
                parameters.AppendLine($"-WelcomeTextToSpeechPrompt \"{_sanitizer.SanitizeSpokenText(variables.CqGreetingTextToSpeechPrompt)}\" `");
            }
        }

        private void AppendMusicOnHoldParameters(StringBuilder parameters, IPhoneManagerVariables variables)
        {
            if (variables.CqMusicOnHoldType == "AudioFile" && !string.IsNullOrWhiteSpace(variables.CqMusicOnHoldAudioFileId))
            {
                parameters.AppendLine($"-MusicOnHoldAudioFileId \"{_sanitizer.SanitizeString(variables.CqMusicOnHoldAudioFileId)}\" `");
                parameters.AppendLine($"-UseDefaultMusicOnHold $false `");
            }
            else
            {
                parameters.AppendLine($"-UseDefaultMusicOnHold $true `");
            }
        }

        private void AppendOverflowParameters(StringBuilder parameters, IPhoneManagerVariables variables)
        {
            var threshold = variables.CqOverflowThreshold ?? ConstantsService.CallQueue.OverflowThreshold;
            parameters.AppendLine($"-OverflowThreshold {threshold} `");

            AppendActionParameters(parameters, 
                actionType: variables.CqOverflowAction,
                target: variables.CqOverflowActionTarget,
                prefix: "Overflow",
                voicemailGreetingType: variables.CqOverflowVoicemailGreetingType,
                voicemailTtsPrompt: variables.CqOverflowActionTextToSpeechPrompt,
                voicemailAudioFileId: variables.CqOverflowActionAudioFileId,
                defaultAction: "DisconnectWithBusy");
        }

        private void AppendTimeoutParameters(StringBuilder parameters, IPhoneManagerVariables variables)
        {
            var threshold = variables.CqTimeoutThreshold ?? ConstantsService.CallQueue.TimeoutThreshold;
            if (threshold < ConstantsService.CallQueue.MinTimeoutThreshold)
            {
                threshold = ConstantsService.CallQueue.MinTimeoutThreshold;
            }
            parameters.AppendLine($"-TimeoutThreshold {threshold} `");

            AppendActionParameters(parameters, 
                actionType: variables.CqTimeoutAction,
                target: variables.CqTimeoutActionTarget,
                prefix: "Timeout",
                voicemailGreetingType: variables.CqTimeoutVoicemailGreetingType,
                voicemailTtsPrompt: variables.CqTimeoutActionTextToSpeechPrompt,
                voicemailAudioFileId: variables.CqTimeoutActionAudioFileId,
                defaultAction: "Disconnect");
        }

        private void AppendNoAgentParameters(StringBuilder parameters, IPhoneManagerVariables variables)
        {
            if (string.IsNullOrWhiteSpace(variables.CqNoAgentAction) || variables.CqNoAgentAction == "QueueCall")
            {
                return; // Default behavior - no parameters needed
            }

            AppendActionParameters(parameters, 
                actionType: variables.CqNoAgentAction,
                target: variables.CqNoAgentActionTarget,
                prefix: "NoAgent",
                voicemailGreetingType: variables.CqNoAgentVoicemailGreetingType,
                voicemailTtsPrompt: variables.CqNoAgentActionTextToSpeechPrompt,
                voicemailAudioFileId: variables.CqNoAgentActionAudioFileId,
                defaultAction: null);

            // ApplyTo setting for NoAgent
            parameters.AppendLine(variables.CqNoAgentApplyToNewCallsOnly 
                ? $"-NoAgentApplyTo NewCalls `" 
                : $"-NoAgentApplyTo AllCalls `");
        }

        /// <summary>
        /// Appends action parameters for overflow, timeout, or no-agent scenarios.
        /// Consolidates common logic for Disconnect, TransferToTarget, and TransferToVoicemail actions.
        /// </summary>
        private void AppendActionParameters(
            StringBuilder parameters,
            string? actionType,
            string? target,
            string prefix,
            string? voicemailGreetingType,
            string? voicemailTtsPrompt,
            string? voicemailAudioFileId,
            string? defaultAction)
        {
            if (string.IsNullOrWhiteSpace(actionType))
            {
                if (defaultAction != null)
                {
                    parameters.AppendLine($"-{prefix}Action {defaultAction} `");
                }
                return;
            }

            switch (actionType)
            {
                case "Disconnect":
                    parameters.AppendLine($"-{prefix}Action {(prefix == "Overflow" ? "DisconnectWithBusy" : "Disconnect")} `");
                    break;

                case "TransferToTarget" when !string.IsNullOrWhiteSpace(target):
                    parameters.AppendLine($"-{prefix}Action Forward `");
                    parameters.AppendLine($"-{prefix}ActionTarget \"{_sanitizer.SanitizeString(target)}\" `");
                    break;

                case "TransferToVoicemail" when !string.IsNullOrWhiteSpace(target):
                    var sanitizedTarget = _sanitizer.SanitizeString(target);
                    var isGuid = Guid.TryParse(target, out _);
                    
                    if (isGuid)
                    {
                        parameters.AppendLine($"-{prefix}Action SharedVoicemail `");
                        parameters.AppendLine($"-{prefix}ActionTarget \"{sanitizedTarget}\" `");
                        AppendVoicemailGreetingParameters(parameters, prefix, voicemailGreetingType, voicemailTtsPrompt, voicemailAudioFileId);
                    }
                    else
                    {
                        parameters.AppendLine($"-{prefix}Action Voicemail `");
                        parameters.AppendLine($"-{prefix}ActionTarget \"{sanitizedTarget}\" `");
                    }
                    break;

                default:
                    if (defaultAction != null)
                    {
                        parameters.AppendLine($"-{prefix}Action {defaultAction} `");
                    }
                    break;
            }
        }

        /// <summary>
        /// Appends voicemail greeting parameters for shared voicemail scenarios.
        /// </summary>
        private void AppendVoicemailGreetingParameters(
            StringBuilder parameters,
            string prefix,
            string? greetingType,
            string? ttsPrompt,
            string? audioFileId)
        {
            if (greetingType == "TextToSpeech" && !string.IsNullOrWhiteSpace(ttsPrompt))
            {
                parameters.AppendLine($"-{prefix}SharedVoicemailTextToSpeechPrompt \"{_sanitizer.SanitizeSpokenText(ttsPrompt)}\" `");
            }
            else if (greetingType == "AudioFile" && !string.IsNullOrWhiteSpace(audioFileId))
            {
                parameters.AppendLine($"-{prefix}SharedVoicemailAudioFilePrompt \"{_sanitizer.SanitizeString(audioFileId)}\" `");
            }
        }
    }
}
