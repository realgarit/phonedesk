using PhoneDesk.Models;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services;
using System.Text;

namespace PhoneDesk.Services.ScriptBuilders
{
    public class AutoAttendantScriptBuilder
    {
        private readonly IPowerShellSanitizationService _sanitizer;

        public AutoAttendantScriptBuilder(IPowerShellSanitizationService sanitizer)
        {
            _sanitizer = sanitizer;
        }

        public string GetRetrieveAutoAttendantsCommand()
        {
            return @"
try {
    $autoAttendants = Get-CsAutoAttendant | Where-Object {$_.Name -like '*" + ConstantsService.Naming.AutoAttendantPrefix + @"*'}
    if ($autoAttendants) {
        Write-Host ""SUCCESS: Found $($autoAttendants.Count) auto attendants containing '" + ConstantsService.Naming.AutoAttendantPrefix + @"'""
        foreach ($aa in $autoAttendants) {
            Write-Host ""AUTOATTENDANT: $($aa.Name)|$($aa.Identity)|$($aa.LanguageId)|$($aa.TimeZoneId)""
        }
    } else {
        Write-Host ""INFO: No auto attendants found containing '" + ConstantsService.Naming.AutoAttendantPrefix + @"'""
    }
}
catch {
    Write-Host ""ERROR: Failed to retrieve auto attendants: $_""
}";
        }

        /// <summary>
        /// Guided Setup step: creates the call flows, the after-hours schedule and the auto attendant.
        /// The resource account, its license, its phone number and the association are separate steps.
        /// Safe to run again: an existing auto attendant with the same name is updated to the current
        /// call flows, schedule, language and time zone instead of being duplicated, and the schedule it
        /// used before is removed by ID when nothing else uses it.
        /// </summary>
        public string GetCreateAutoAttendantCommand(IPhoneManagerVariables variables)
        {
            var defaultCallFlow = BuildCallFlow(variables, "Default", variables.AaDefaultGreetingType, variables.AaDefaultGreetingTextToSpeechPrompt, variables.AaDefaultGreetingAudioFileId, variables.AaDefaultAction, variables.AaDefaultActionTarget);
            var afterHoursCallFlow = BuildCallFlow(variables, "After Hours", variables.AaAfterHoursGreetingType, variables.AaAfterHoursGreetingTextToSpeechPrompt, variables.AaAfterHoursGreetingAudioFileId, variables.AaAfterHoursAction, variables.AaAfterHoursActionTarget);

            var sanitizedAaDisplayName = _sanitizer.SanitizeString(variables.AaDisplayName);
            var sanitizedLanguageId = _sanitizer.SanitizeString(variables.LanguageId);
            var sanitizedTimeZoneId = _sanitizer.SanitizeString(variables.TimeZoneId);
            var afterHoursSchedule = BuildAfterHoursScheduleStatements(variables);

            return $@"
try {{
    $aaExisting = Get-CsAutoAttendant -NameFilter ""{sanitizedAaDisplayName}"" -ErrorAction Stop | Where-Object {{ $_.Name -eq ""{sanitizedAaDisplayName}"" }} | Select-Object -First 1

{defaultCallFlow}

{afterHoursCallFlow}

{afterHoursSchedule}
$aaafterHoursCallHandlingAssociation = New-CsAutoAttendantCallHandlingAssociation -Type AfterHours -ScheduleId $aaafterHoursSchedule.Id -CallFlowId $aaafterHoursCallFlow.Id

    if ($aaExisting) {{
        # Running the step again applies the current Configuration to the existing auto attendant.
        $aaPreviousScheduleIds = @($aaExisting.CallHandlingAssociations | ForEach-Object {{ $_.ScheduleId }} | Where-Object {{ $_ -and $_ -ne $aaafterHoursSchedule.Id }})
        $aaExisting.DefaultCallFlow = $aadefaultCallFlow
        $aaExisting.CallFlows = @($aaafterHoursCallFlow)
        $aaExisting.CallHandlingAssociations = @($aaafterHoursCallHandlingAssociation)
        $aaExisting.LanguageId = ""{sanitizedLanguageId}""
        $aaExisting.TimeZoneId = ""{sanitizedTimeZoneId}""
        try {{
            Set-CsAutoAttendant -Instance $aaExisting -ErrorAction Stop | Out-Null
        }}
        catch {{
            # Do not leave the schedule created for this run behind when the update fails.
            try {{ Remove-CsOnlineSchedule -Id $aaafterHoursSchedule.Id -ErrorAction Stop }} catch {{ }}
            throw
        }}
        Write-Host ""INFO: Auto attendant {sanitizedAaDisplayName} already existed and was updated to the current Configuration.""

        foreach ($scheduleId in $aaPreviousScheduleIds) {{
            try {{
                $previousSchedule = Get-CsOnlineSchedule -Id $scheduleId -ErrorAction Stop
                if ($previousSchedule -and -not $previousSchedule.AssociatedConfigurationIds) {{
                    Remove-CsOnlineSchedule -Id $scheduleId -ErrorAction Stop
                }}
            }}
            catch {{
            }}
        }}
    }}
    else {{
        New-CsAutoAttendant `
        -Name ""{sanitizedAaDisplayName}"" `
        -DefaultCallFlow $aadefaultCallFlow `
        -CallFlows @($aaafterHoursCallFlow) `
        -CallHandlingAssociations @($aaafterHoursCallHandlingAssociation) `
        -LanguageId ""{sanitizedLanguageId}"" `
        -TimeZoneId ""{sanitizedTimeZoneId}"" `
        -ErrorAction Stop | Out-Null
    }}

    Write-Host ""SUCCESS: Auto attendant {sanitizedAaDisplayName} is ready""
}}
catch {{
    Write-Host ""ERROR: Failed to create auto attendant: $_""
}}";
        }

        /// <summary>
        /// Builds the time ranges and the after-hours schedule ($aaafterHoursSchedule) for the uniform
        /// Monday–Friday hours or the per-day plan. A second time range is created only when it is set:
        /// New-CsOnlineTimeRange rejects an empty 00:00–00:00 range.
        /// </summary>
        private static string BuildAfterHoursScheduleStatements(IPhoneManagerVariables variables)
        {
            var sb = new StringBuilder();
            if (variables.UsePerDaySchedule)
            {
                var dayNames = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
                var parameters = new StringBuilder();
                foreach (var dayName in dayNames)
                {
                    var day = variables.WeeklySchedule.FirstOrDefault(d => d.DayName == dayName);
                    if (day == null || !day.IsEnabled) continue;

                    var varPrefix = dayName.ToLower();
                    sb.AppendLine($"${varPrefix}Tr1 = New-CsOnlineTimeRange -Start \"{day.Hours1Start:hh\\:mm}\" -End \"{day.Hours1End:hh\\:mm}\" -ErrorAction Stop");
                    if (day.HasSecondRange && day.Hours2Start != day.Hours2End)
                    {
                        sb.AppendLine($"${varPrefix}Tr2 = New-CsOnlineTimeRange -Start \"{day.Hours2Start:hh\\:mm}\" -End \"{day.Hours2End:hh\\:mm}\" -ErrorAction Stop");
                        sb.AppendLine($"${varPrefix}Hours = @(${varPrefix}Tr1, ${varPrefix}Tr2)");
                    }
                    else
                    {
                        sb.AppendLine($"${varPrefix}Hours = @(${varPrefix}Tr1)");
                    }
                    parameters.Append($" -{dayName}Hours ${varPrefix}Hours");
                }

                sb.Append($"$aaafterHoursSchedule = New-CsOnlineSchedule -Name \"After Hours Schedule\" -WeeklyRecurrentSchedule{parameters} -Complement -ErrorAction Stop");
                return sb.ToString();
            }

            sb.AppendLine($"$aatr1 = New-CsOnlineTimeRange -Start \"{variables.OpeningHours1Start:hh\\:mm}\" -End \"{variables.OpeningHours1End:hh\\:mm}\" -ErrorAction Stop");
            if (variables.OpeningHours2Start != variables.OpeningHours2End)
            {
                sb.AppendLine($"$aatr2 = New-CsOnlineTimeRange -Start \"{variables.OpeningHours2Start:hh\\:mm}\" -End \"{variables.OpeningHours2End:hh\\:mm}\" -ErrorAction Stop");
                sb.AppendLine("$aaTimeRanges = @($aatr1, $aatr2)");
            }
            else
            {
                sb.AppendLine("$aaTimeRanges = @($aatr1)");
            }

            sb.Append("$aaafterHoursSchedule = New-CsOnlineSchedule -Name \"After Hours Schedule\" -WeeklyRecurrentSchedule -MondayHours $aaTimeRanges -TuesdayHours $aaTimeRanges -WednesdayHours $aaTimeRanges -ThursdayHours $aaTimeRanges -FridayHours $aaTimeRanges -Complement -ErrorAction Stop");
            return sb.ToString();
        }

        private string BuildCallFlow(IPhoneManagerVariables variables, string flowName, string? greetingType, string? ttsPrompt, string? audioFileId, string? action, string? actionTarget)
        {
            var sb = new StringBuilder();
            var prefix = flowName.Replace(" ", "").ToLower();
            var sanitizedFlowName = _sanitizer.SanitizeString(flowName);

            // Greeting
            string greetingVar = "$null";
            if (greetingType == "AudioFile" && !string.IsNullOrWhiteSpace(audioFileId))
            {
                var sanitizedAudioFileId = _sanitizer.SanitizeString(audioFileId);
                // AudioFilePrompt requires an AudioFile object, not just an ID string.
                // Retrieve the audio file object first using Get-CsOnlineAudioFile.
                sb.AppendLine($"${prefix}AudioFile = Get-CsOnlineAudioFile -Identity \"{sanitizedAudioFileId}\" -ApplicationId OrgAutoAttendant");
                sb.AppendLine($"${prefix}Greeting = New-CsAutoAttendantPrompt -AudioFilePrompt ${prefix}AudioFile");
                greetingVar = $"@(${prefix}Greeting)";
            }
            else if (greetingType == "TextToSpeech" && !string.IsNullOrWhiteSpace(ttsPrompt))
            {
                var sanitizedTtsPrompt = _sanitizer.SanitizeSpokenText(ttsPrompt);
                sb.AppendLine($"${prefix}Greeting = New-CsAutoAttendantPrompt -TextToSpeechPrompt \"{sanitizedTtsPrompt}\"");
                greetingVar = $"@(${prefix}Greeting)";
            }
            // If None, greetingVar remains $null (or empty array for cmdlet)

            // Action
            sb.AppendLine($"${prefix}MenuOption = $null");
            // The default call flow sends calls to the call queue of the same configuration when
            // this is selected, and also when no action was selected: an auto attendant that is
            // built together with a call queue must not hang up during opening hours.
            var toCallQueue = action == "TransferToCallQueue" || (flowName == "Default" && string.IsNullOrWhiteSpace(action));
            if (toCallQueue && !string.IsNullOrWhiteSpace(variables.RacqUPN))
            {
                var sanitizedCallQueueAccount = _sanitizer.SanitizeString(variables.RacqUPN);
                sb.AppendLine($"${prefix}CallQueueAccount = Get-CsOnlineUser \"{sanitizedCallQueueAccount}\" -ErrorAction SilentlyContinue");
                sb.AppendLine($"if (-not ${prefix}CallQueueAccount) {{ throw \"The call queue resource account '{sanitizedCallQueueAccount}' was not found. Create the call queue first, or set the action of this call flow to Disconnect.\" }}");
                sb.AppendLine($"${prefix}CallTarget = New-CsAutoAttendantCallableEntity -Identity ${prefix}CallQueueAccount.Identity -Type ApplicationEndpoint");
                sb.AppendLine($"${prefix}MenuOption = New-CsAutoAttendantMenuOption -Action TransferCallToTarget -CallTarget ${prefix}CallTarget -DtmfResponse Automatic");
            }
            else if (action == "TransferToTarget" && !string.IsNullOrWhiteSpace(actionTarget))
            {
                var sanitizedActionTarget = _sanitizer.SanitizeString(actionTarget);
                // Check if target is a resource account to get Identity
                sb.AppendLine($"$targetUser = Get-CsOnlineUser \"{sanitizedActionTarget}\" -ErrorAction SilentlyContinue");
                sb.AppendLine($"if ($targetUser) {{ $targetId = $targetUser.Identity }} else {{ $targetId = \"{sanitizedActionTarget}\" }}"); // Fallback if not found or is object ID
                sb.AppendLine($"${prefix}CallTarget = New-CsAutoAttendantCallableEntity -Identity $targetId -Type ApplicationEndpoint");
                sb.AppendLine($"${prefix}MenuOption = New-CsAutoAttendantMenuOption -Action TransferCallToTarget -CallTarget ${prefix}CallTarget -DtmfResponse Automatic");
            }
            else if (action == "TransferToVoicemail" && !string.IsNullOrWhiteSpace(actionTarget))
            {
                var sanitizedActionTarget = _sanitizer.SanitizeString(actionTarget);
                // Shared Voicemail (M365 Group)
                sb.AppendLine($"${prefix}CallTarget = New-CsAutoAttendantCallableEntity -Identity \"{sanitizedActionTarget}\" -Type SharedVoicemail");
                sb.AppendLine($"${prefix}MenuOption = New-CsAutoAttendantMenuOption -Action TransferCallToTarget -CallTarget ${prefix}CallTarget -DtmfResponse Automatic");
            }
            else
            {
                // Default to Disconnect
                sb.AppendLine($"${prefix}MenuOption = New-CsAutoAttendantMenuOption -Action DisconnectCall -DtmfResponse Automatic");
            }

            sb.AppendLine($"${prefix}Menu = New-CsAutoAttendantMenu -Name \"{sanitizedFlowName} menu\" -MenuOptions @(${prefix}MenuOption)");

            if (greetingVar != "$null")
            {
                sb.AppendLine($"$aa{prefix}CallFlow = New-CsAutoAttendantCallFlow -Name \"{sanitizedFlowName} call flow\" -Greetings {greetingVar} -Menu ${prefix}Menu");
            }
            else
            {
                sb.AppendLine($"$aa{prefix}CallFlow = New-CsAutoAttendantCallFlow -Name \"{sanitizedFlowName} call flow\" -Menu ${prefix}Menu");
            }

            return sb.ToString();
        }

        public string GetVerifyAutoAttendantCommand(string aaDisplayName)
        {
            var sanitizedAaDisplayName = _sanitizer.SanitizeString(aaDisplayName);
            
            return $@"
try {{
    $AutoAttendant = Get-CsAutoAttendant -NameFilter ""{sanitizedAaDisplayName}""
    if ($AutoAttendant) {{
        Write-Host ""SUCCESS: Auto attendant found and is accessible""
        Write-Host ""Auto Attendant ID: $($AutoAttendant.Identity)""
        Write-Host ""Auto Attendant Name: $($AutoAttendant.Name)""
    }} else {{
        Write-Host ""ERROR: Auto attendant not found""
    }}
}}
catch {{
    Write-Host ""ERROR: Failed to verify auto attendant: $_""
}}";
        }

        public string GetAttachHolidayToAutoAttendantCommand(string holidayName, string aaDisplayName, string holidayGreetingPrompt)
        {
            var sanitizedHolidayName = _sanitizer.SanitizeString(holidayName);
            var sanitizedAaDisplayName = _sanitizer.SanitizeString(aaDisplayName);
            var sanitizedGreetingPrompt = _sanitizer.SanitizeSpokenText(holidayGreetingPrompt);

            return $@"
try {{
    $HolidaySchedule = Get-CsOnlineSchedule | Where-Object {{$_.Name -eq ""{sanitizedHolidayName}""}}
    if (-not $HolidaySchedule) {{
        throw ""Holiday schedule '{sanitizedHolidayName}' not found. Please create the holiday first.""
    }}

    $HolidayScheduleName = $HolidaySchedule.Name
    $HolidayMenuOption = New-CsAutoAttendantMenuOption -Action DisconnectCall -DtmfResponse Automatic
    $HolidayMenu = New-CsAutoAttendantMenu -Name $HolidayScheduleName -MenuOptions @($HolidayMenuOption)
    $HolidayGreetingPromptDE = New-CsAutoAttendantPrompt -TextToSpeechPrompt ""{sanitizedGreetingPrompt}""
    $HolidayCallFlow = New-CsAutoAttendantCallFlow -Name $HolidayScheduleName -Menu $HolidayMenu -Greetings @($HolidayGreetingPromptDE)
    $HolidayCallHandlingAssociation = New-CsAutoAttendantCallHandlingAssociation -Type Holiday -ScheduleId $HolidaySchedule.Id -CallFlowId $HolidayCallFlow.Id

    $HolidayAutoAttendant = Get-CsAutoAttendant -NameFilter ""{sanitizedAaDisplayName}"" | Where-Object {{$_.Name -eq ""{sanitizedAaDisplayName}""}} | Select-Object -First 1
    if (-not $HolidayAutoAttendant) {{
        throw ""Auto Attendant '{sanitizedAaDisplayName}' not found. Please ensure it exists.""
    }}

    $HolidayAutoAttendant.CallFlows += @($HolidayCallFlow)
    $HolidayAutoAttendant.CallHandlingAssociations += @($HolidayCallHandlingAssociation)
    Set-CsAutoAttendant -Instance $HolidayAutoAttendant
    Write-Host ""SUCCESS: Holiday {sanitizedHolidayName} attached to auto attendant {sanitizedAaDisplayName} successfully""
}}
catch {{
    Write-Host ""ERROR: Failed to attach holiday {sanitizedHolidayName} to auto attendant {sanitizedAaDisplayName}: $_""
}}";
        }

        public string GetAssignPhoneNumberToAutoAttendantCommand(string upn, string phoneNumber, string phoneNumberType)
        {
            // SECURITY: Sanitize inputs
            var sanitizedUpn = _sanitizer.SanitizeString(upn);
            var sanitizedPhoneNumber = _sanitizer.SanitizeString(phoneNumber);
            var sanitizedPhoneNumberType = _sanitizer.SanitizeString(phoneNumberType);
            
            return $@"
try {{
    Set-CsPhoneNumberAssignment -Identity ""{sanitizedUpn}"" -PhoneNumber ""{sanitizedPhoneNumber}"" -PhoneNumberType ""{sanitizedPhoneNumberType}"" -ErrorAction Stop
    Write-Host ""SUCCESS: Phone number assigned successfully""
}}
catch {{
    Write-Host ""ERROR: Failed to assign phone number: $_""
}}";
        }

        public string GetCreateDefaultCallFlowCommand(IPhoneManagerVariables variables)
        {
            var callFlowScript = BuildCallFlow(variables, "Default", variables.AaDefaultGreetingType, variables.AaDefaultGreetingTextToSpeechPrompt, variables.AaDefaultGreetingAudioFileId, variables.AaDefaultAction, variables.AaDefaultActionTarget);
            return $@"
try {{
{callFlowScript}
    Write-Host ""SUCCESS: Created default call flow""
}}
catch {{
    Write-Host ""ERROR: Failed to create default call flow: $_""
}}";
        }

        public string GetCreateAfterHoursCallFlowCommand(IPhoneManagerVariables variables)
        {
            var callFlowScript = BuildCallFlow(variables, "After Hours", variables.AaAfterHoursGreetingType, variables.AaAfterHoursGreetingTextToSpeechPrompt, variables.AaAfterHoursGreetingAudioFileId, variables.AaAfterHoursAction, variables.AaAfterHoursActionTarget);
            return $@"
try {{
{callFlowScript}
    Write-Host ""SUCCESS: Created after hours call flow""
}}
catch {{
    Write-Host ""ERROR: Failed to create after hours call flow: $_""
}}";
        }

        public string GetCreateAfterHoursScheduleCommand(IPhoneManagerVariables variables)
        {
            if (variables.UsePerDaySchedule)
            {
                return BuildPerDayScheduleScript(variables);
            }

            return $@"
try {{
{BuildAfterHoursScheduleStatements(variables)}

    if ($aaafterHoursSchedule) {{
        Write-Host ""SUCCESS: Created after hours schedule with ID: $($aaafterHoursSchedule.Id)""
    }} else {{
        Write-Host ""ERROR: Failed to retrieve ID after creating schedule""
    }}
}}
catch {{
    Write-Host ""ERROR: Failed to create after hours schedule: $_""
}}";
        }

        public string GetCreateCallHandlingAssociationCommand()
        {
            return $@"
try {{
    if ($aaafterHoursSchedule.Id) {{
        $aaafterHoursCallHandlingAssociation = New-CsAutoAttendantCallHandlingAssociation -Type AfterHours -ScheduleId $aaafterHoursSchedule.Id -CallFlowId $aaafterHoursCallFlow.Id
        Write-Host ""SUCCESS: Created call handling association""
    }} else {{
        Write-Host ""ERROR: Failed to create call handling association: ScheduleId is empty""
    }}
}}
catch {{
    Write-Host ""ERROR: Failed to create call handling association: $_""
}}";
        }

        public string GetCreateSimpleAutoAttendantCommand(IPhoneManagerVariables variables)
            => GetCreateSimpleAutoAttendantCommand(variables.AaDisplayName, variables.LanguageId, variables.TimeZoneId);

        public string GetCreateSimpleAutoAttendantCommand(string aaName, string languageId, string timeZoneId)
        {
            // SECURITY: Sanitize inputs
            var sanitizedAaDisplayName = _sanitizer.SanitizeString(aaName);
            var sanitizedLanguageId = _sanitizer.SanitizeString(languageId);
            var sanitizedTimeZoneId = _sanitizer.SanitizeString(timeZoneId);
            
            return $@"
try {{
    New-CsAutoAttendant `
    -Name ""{sanitizedAaDisplayName}"" `
    -DefaultCallFlow $aadefaultCallFlow `
    -CallFlows @($aaafterHoursCallFlow) `
    -CallHandlingAssociations @($aaafterHoursCallHandlingAssociation) `
    -LanguageId ""{sanitizedLanguageId}"" `
    -TimeZoneId ""{sanitizedTimeZoneId}""
    Write-Host ""SUCCESS: Auto attendant created successfully""
}}
catch {{
    Write-Host ""ERROR: Failed to create auto attendant: $_""
}}";
        }

        public string GetAssociateResourceAccountWithAutoAttendantCommand(string resourceAccountUpn, string autoAttendantName)
        {
            // SECURITY: Sanitize inputs
            var sanitizedUpn = _sanitizer.SanitizeString(resourceAccountUpn);
            var sanitizedAaName = _sanitizer.SanitizeString(autoAttendantName);
            
            return $@"
try {{
    $aaapplicationInstance = Get-CsOnlineUser ""{sanitizedUpn}"" -ErrorAction Stop
    if (-not $aaapplicationInstance) {{
        throw ""Resource account '{sanitizedUpn}' was not found.""
    }}
    $aaautoAttendant = Get-CsAutoAttendant -NameFilter ""{sanitizedAaName}"" -ErrorAction Stop | Where-Object {{ $_.Name -eq ""{sanitizedAaName}"" }} | Select-Object -First 1
    if (-not $aaautoAttendant) {{
        throw ""Auto attendant '{sanitizedAaName}' was not found.""
    }}
    $aaAssociation = $null
    try {{
        $aaAssociation = Get-CsOnlineApplicationInstanceAssociation -Identity $aaapplicationInstance.Identity -ErrorAction Stop
    }}
    catch {{
        $aaAssociation = $null
    }}
    if ($aaAssociation -and $aaAssociation.ConfigurationId -eq $aaautoAttendant.Identity) {{
        Write-Host ""INFO: Resource account {sanitizedUpn} is already associated with auto attendant {sanitizedAaName}.""
    }}
    else {{
        New-CsOnlineApplicationInstanceAssociation -Identities @($aaapplicationInstance.Identity) -ConfigurationId $aaautoAttendant.Identity -ConfigurationType AutoAttendant -ErrorAction Stop | Out-Null
    }}
    Write-Host ""SUCCESS: Associated resource account with auto attendant""
}}
catch {{
    Write-Host ""ERROR: Failed to associate resource account with auto attendant: $_""
}}";
        }

        /// <summary>
        /// Removes an auto attendant by name. Linked resource accounts are released first, and the
        /// schedules it used are removed afterwards when no other configuration uses them (by ID, never
        /// by name: schedule names such as "After Hours Schedule" are shared across auto attendants).
        /// </summary>
        public string GetRemoveAutoAttendantCommand(string autoAttendantName)
        {
            var sanitizedName = _sanitizer.SanitizeString(autoAttendantName);

            return $@"
try {{
    $aa = Get-CsAutoAttendant -NameFilter ""{sanitizedName}"" -ErrorAction Stop | Where-Object {{$_.Name -eq ""{sanitizedName}""}} | Select-Object -First 1
    if (-not $aa) {{
        Write-Host ""ERROR: Auto attendant '{sanitizedName}' not found""
    }} else {{
        $aaScheduleIds = @($aa.CallHandlingAssociations | ForEach-Object {{ $_.ScheduleId }} | Where-Object {{ $_ }})
        if ($aa.ApplicationInstances) {{
            Remove-CsOnlineApplicationInstanceAssociation -Identities @($aa.ApplicationInstances) -ErrorAction Stop | Out-Null
        }}
        Remove-CsAutoAttendant -Identity $aa.Identity -ErrorAction Stop
        Write-Host ""SUCCESS: Auto attendant '{sanitizedName}' removed successfully""

        # Schedule cleanup is best effort: the auto attendant is already removed. A Teams cmdlet can throw
        # after it did its work, so the result is checked by reading the schedule again.
        foreach ($scheduleId in $aaScheduleIds) {{
            try {{
                $schedule = Get-CsOnlineSchedule -Id $scheduleId -ErrorAction Stop
                if ($schedule -and -not $schedule.AssociatedConfigurationIds) {{
                    Remove-CsOnlineSchedule -Id $scheduleId -ErrorAction Stop
                }}
            }}
            catch {{
            }}
            $remaining = $null
            try {{
                $remaining = Get-CsOnlineSchedule -Id $scheduleId -ErrorAction Stop
            }}
            catch {{
                $remaining = $null
            }}
            if ($remaining -and -not $remaining.AssociatedConfigurationIds) {{
                Write-Host ""WARNING: Schedule '$($remaining.Name)' ($scheduleId) is no longer used but could not be removed.""
            }}
        }}
    }}
}}
catch {{
    Write-Host ""ERROR: Failed to remove auto attendant '{sanitizedName}': $_""
}}";
        }

        /// <summary>
        /// Removes an online schedule by name.
        /// </summary>
        public string GetRemoveScheduleCommand(string scheduleName)
        {
            var sanitizedName = _sanitizer.SanitizeString(scheduleName);
            
            return $@"
try {{
    $schedule = Get-CsOnlineSchedule | Where-Object {{$_.Name -eq ""{sanitizedName}""}} | Select-Object -First 1
    if (-not $schedule) {{
        Write-Host ""ERROR: Schedule '{sanitizedName}' not found""
    }} else {{
        Remove-CsOnlineSchedule -Id $schedule.Id
        Write-Host ""SUCCESS: Schedule '{sanitizedName}' removed successfully""
    }}
}}
catch {{
    Write-Host ""ERROR: Failed to remove schedule '{sanitizedName}': $_""
}}";
        }

        private string BuildPerDayScheduleScript(IPhoneManagerVariables variables)
        {
            var sb = new StringBuilder();
            sb.AppendLine(@"
try {");
            // Build time ranges for each day
            var dayNames = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
            var psParamNames = new[] { "MondayHours", "TuesdayHours", "WednesdayHours", "ThursdayHours", "FridayHours", "SaturdayHours", "SundayHours" };

            for (int i = 0; i < dayNames.Length; i++)
            {
                var day = variables.WeeklySchedule.FirstOrDefault(d => d.DayName == dayNames[i]);
                if (day == null || !day.IsEnabled) continue;

                var varPrefix = dayNames[i].ToLower();
                sb.AppendLine($"    ${varPrefix}Tr1 = New-CsOnlineTimeRange -Start \"{day.Hours1Start:hh\\:mm}\" -End \"{day.Hours1End:hh\\:mm}\"");
                if (day.HasSecondRange && day.Hours2Start != day.Hours2End)
                {
                    sb.AppendLine($"    ${varPrefix}Tr2 = New-CsOnlineTimeRange -Start \"{day.Hours2Start:hh\\:mm}\" -End \"{day.Hours2End:hh\\:mm}\"");
                    sb.AppendLine($"    ${varPrefix}Hours = @(${varPrefix}Tr1, ${varPrefix}Tr2)");
                }
                else
                {
                    sb.AppendLine($"    ${varPrefix}Hours = @(${varPrefix}Tr1)");
                }
            }

            // Build the schedule command with per-day parameters
            sb.AppendLine("    $aaafterHoursSchedule = New-CsOnlineSchedule -Name \"After Hours Schedule\" -WeeklyRecurrentSchedule `");
            
            for (int i = 0; i < dayNames.Length; i++)
            {
                var day = variables.WeeklySchedule.FirstOrDefault(d => d.DayName == dayNames[i]);
                if (day != null && day.IsEnabled)
                {
                    var varPrefix = dayNames[i].ToLower();
                    sb.AppendLine($"        -{psParamNames[i]} ${varPrefix}Hours `");
                }
            }
            sb.AppendLine("        -Complement");

            sb.AppendLine(@"
    if ($aaafterHoursSchedule) {
        Write-Host ""SUCCESS: Created per-day after hours schedule with ID: $($aaafterHoursSchedule.Id)""
    } else {
        Write-Host ""ERROR: Failed to retrieve ID after creating schedule""
    }
}
catch {
    Write-Host ""ERROR: Failed to create after hours schedule: $_""
}");
            return sb.ToString();
        }
    }
}
