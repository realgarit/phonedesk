using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Mcp.Tools;

/// <summary>
/// Single provisioning operations, the same ones the manual pages of the desktop app offer.
/// Arguments left out are taken from the configuration.
/// </summary>
[McpServerToolType]
internal sealed class ProvisioningTools
{
    private const string PlanIdDescription = "The planId from the first call. Omit to get the plan.";
    private const string ChangeNote = " CHANGES THE TENANT. Call it first without planId: it returns the exact script as a plan and changes nothing. Show the plan to the user, then call it again with the same arguments and the planId to run it.";

    private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".wma" };

    private readonly ConfigurationStore _store;
    private readonly IPowerShellCommandService _commands;
    private readonly ChangeGate _changes;
    private readonly TenantGateway _tenant;
    private readonly CreatedObjectGuard _guard;
    private readonly CreatedObjectLedger _ledger;
    private readonly HolidayCallHandlingReader _holidays;
    private readonly TimeProvider _time;
    private readonly HolidayBackupWriter _backups;
    private readonly AudioUploadLog _uploads;

    public ProvisioningTools(
        ConfigurationStore store,
        IPowerShellCommandService commands,
        ChangeGate changes,
        TenantGateway tenant,
        CreatedObjectGuard guard,
        CreatedObjectLedger ledger,
        HolidayCallHandlingReader holidays,
        TimeProvider time,
        HolidayBackupWriter backups,
        AudioUploadLog uploads)
    {
        _backups = backups;
        _uploads = uploads;
        _holidays = holidays;
        _time = time;
        _store = store;
        _commands = commands;
        _changes = changes;
        _tenant = tenant;
        _guard = guard;
        _ledger = ledger;
    }

    [McpServerTool(Name = "create_m365_group", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Creates the Microsoft 365 group for call queue agents. An existing group with the same name is reused." + ChangeNote)]
    public Task<string> CreateM365Group(
        [Description("Group name. Omit to use the name computed from the configuration.")] string? name = null,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var groupName = Pick(name, _store.Variables.M365Group, "group name");
            return await _changes.PlanOrApplyAsync(
                Creating(TenantObjectType.M365Group, groupName, "CreateM365Group",
                    _commands.GetCreateM365GroupCommand(groupName), $"Create Microsoft 365 group '{groupName}'."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "lookup_m365_group_id", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Looks up the object ID of a Microsoft 365 group by name. Use the ID as the target of a TransferToVoicemail action (shared voicemail), for example callQueueTemplate.timeoutActionTarget. With saveToConfiguration=true the ID is also written to general.m365GroupId of the local configuration, as a note; the call queue finds its agent group by name, not from this value. Reads from the tenant only.")]
    public Task<string> LookupM365GroupId(
        [Description("Group name. Omit to use the name computed from the configuration.")] string? name = null,
        [Description("True to store the ID in the local configuration.")] bool saveToConfiguration = false,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var groupName = Pick(name, _store.Variables.M365Group, "group name");
            var result = await _tenant.ReadAsync(_commands.GetM365GroupIdCommand(groupName), "GetM365GroupId", cancellationToken);
            var groupId = TenantOutputParser.ParseM365GroupId(result.Value);
            if (!result.IsSuccess || string.IsNullOrWhiteSpace(groupId))
            {
                throw new InvalidOperationException(
                    $"The ID of group '{groupName}' could not be read: {result.ErrorMessage ?? McpJson.Truncate(result.RawOutput, 500)}");
            }

            if (saveToConfiguration)
            {
                _store.Patch(new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["general.m365GroupId"] = System.Text.Json.JsonSerializer.SerializeToElement(groupId)
                });
            }

            return new { success = true, groupName, groupId, savedToConfiguration = saveToConfiguration };
        });

    [McpServerTool(Name = "create_resource_account", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Creates a Teams resource account for a call queue or an auto attendant. An existing account with the same UPN is reused. A UPN without a domain gets the fallback domain of the configuration." + ChangeNote)]
    public Task<string> CreateResourceAccount(
        [Description("callQueue or autoAttendant.")] string kind,
        [Description("User principal name. Omit to use the one computed from the configuration.")] string? upn = null,
        [Description("Display name. Omit to use the one computed from the configuration.")] string? displayName = null,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            var isQueue = IsCallQueue(kind);
            var accountUpn = CompleteUpn(Pick(upn, (isQueue ? variables.RacqUPN : variables.RaaaUPN), "UPN"), variables.MsFallbackDomain);
            var accountName = Pick(displayName, (isQueue ? variables.RacqDisplayName : variables.RaaaDisplayName), "display name");
            var applicationId = Require(isQueue ? variables.CsAppCqId : variables.CsAppAaId, "application ID");
            var script = isQueue
                ? _commands.GetCreateResourceAccountCommand(accountUpn, accountName, applicationId)
                : _commands.GetCreateAutoAttendantResourceAccountCommand(accountUpn, accountName, applicationId);
            return await _changes.PlanOrApplyAsync(
                Creating(TenantObjectType.ResourceAccount, accountUpn, "CreateResourceAccount", script,
                    $"Create the {(isQueue ? "call queue" : "auto attendant")} resource account '{accountUpn}'."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "set_resource_account_usage_location", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Sets the usage location (two-letter country code) of a resource account. A license can be assigned only after this. The UPN must be a resource account: the one of the configuration, one this server created, or one that list_resource_accounts shows." + ChangeNote)]
    public Task<string> SetUsageLocation(
        [Description("callQueue or autoAttendant.")] string kind,
        [Description("User principal name. Omit to use the one computed from the configuration.")] string? upn = null,
        [Description("Two-letter country code, for example CH. Omit to use the configuration.")] string? usageLocation = null,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            var isQueue = IsCallQueue(kind);
            var accountUpn = Pick(upn, (isQueue ? variables.RacqUPN : variables.RaaaUPN), "UPN");
            await RequireResourceAccountAsync(accountUpn, variables, cancellationToken);
            var location = Require(usageLocation ?? variables.UsageLocation, "usage location").ToUpperInvariant();
            if (location.Length != 2 || !location.All(char.IsAsciiLetterUpper))
            {
                throw new ArgumentException($"'{location}' is not a usage location. Give the two-letter country code, for example CH.");
            }

            var script = isQueue
                ? _commands.GetUpdateResourceAccountUsageLocationCommand(accountUpn, location)
                : _commands.GetUpdateAutoAttendantResourceAccountUsageLocationCommand(accountUpn, location);
            return await _changes.PlanOrApplyAsync(
                new ChangeRequest("UpdateUsageLocation", accountUpn, script, $"Set the usage location of '{accountUpn}' to {location}."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "assign_resource_account_license", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Assigns the Teams Phone resource account license to a resource account. The tenant must have a free license and the account needs a usage location. The UPN must be a resource account: the one of the configuration, one this server created, or one that list_resource_accounts shows." + ChangeNote)]
    public Task<string> AssignLicense(
        [Description("callQueue or autoAttendant.")] string kind,
        [Description("User principal name. Omit to use the one computed from the configuration.")] string? upn = null,
        [Description("License SKU ID. Omit to use the configuration (Teams Phone resource account license).")] string? skuId = null,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            var isQueue = IsCallQueue(kind);
            var accountUpn = Pick(upn, (isQueue ? variables.RacqUPN : variables.RaaaUPN), "UPN");
            await RequireResourceAccountAsync(accountUpn, variables, cancellationToken);
            var sku = Require(skuId ?? variables.SkuId, "SKU ID");
            var script = isQueue
                ? _commands.GetAssignLicenseCommand(accountUpn, sku)
                : _commands.GetAssignAutoAttendantLicenseCommand(accountUpn, sku);
            return await _changes.PlanOrApplyAsync(
                new ChangeRequest("AssignLicense", accountUpn, script, $"Assign license {sku} to '{accountUpn}'."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "assign_phone_number", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Assigns the phone number of the configuration to the auto attendant resource account of the configuration; it is the number step of a setup. The account needs its license first. The UPN must be a resource account: the one of the configuration, one this server created, or one that list_resource_accounts shows. For any other number work use set_phone_number_assignment (an employee, a move from one account to another, with a check of both accounts and a read-back), remove_phone_number_assignment and list_phone_numbers." + ChangeNote)]
    public Task<string> AssignPhoneNumber(
        [Description("User principal name. Omit to use the auto attendant resource account from the configuration.")] string? upn = null,
        [Description("Phone number in E.164 format, for example +41441234567. Omit to use the configuration.")] string? phoneNumber = null,
        [Description("Phone number type: DirectRouting, CallingPlan or OperatorConnect. Omit to use the configuration.")] string? phoneNumberType = null,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            var accountUpn = Pick(upn, variables.RaaaUPN, "UPN");
            await RequireResourceAccountAsync(accountUpn, variables, cancellationToken);
            var number = Require(phoneNumber ?? variables.RaaAnr, "phone number");
            var type = Require(phoneNumberType ?? variables.PhoneNumberType, "phone number type");
            if (!PhoneNumberScripts.IsE164(number))
            {
                throw new ArgumentException($"'{number}' is not a phone number in E.164 format, for example +41441234567.");
            }

            return await _changes.PlanOrApplyAsync(
                new ChangeRequest("AssignPhoneNumber", accountUpn,
                    _commands.GetAssignPhoneNumberToAutoAttendantCommand(accountUpn, number, type),
                    $"Assign {number} ({type}) to '{accountUpn}'.")
                {
                    // A number the tenant did not have before is recorded, so release_phone_number can remove it later.
                    OnSuccess = result =>
                    {
                        if (string.Equals(type.Trim(), "DirectRouting", StringComparison.OrdinalIgnoreCase))
                        {
                            _guard.Claim(TenantObjectType.PhoneNumber, number.Trim(), "AssignPhoneNumber", result.RawOutput);
                        }
                    }
                },
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "create_call_queue", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Creates the call queue from the configuration (name, language, agent group, greeting, music on hold, overflow, timeout and no-agent handling) and associates it with its resource account. The name, the agent group and the resource account are the ones computed from the configuration (see configuration_get); for another call queue change general.customerGroupName, which gives it its own group and resource account. The configuration must be valid. An existing call queue with the same name is updated to the configuration." + ChangeNote)]
    public Task<string> CreateCallQueue(
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            // The same check as Guided Setup: a configuration with a problem never reaches the script.
            _store.RequireUsable();
            var variables = _store.Variables;
            var name = Pick(null, variables.CqDisplayName, "call queue name");
            return await _changes.PlanOrApplyAsync(
                Creating(TenantObjectType.CallQueue, name, "CreateCallQueue",
                    AgentGroupCheck.InFrontOf(_commands.GetCreateCallQueueCommand(variables), variables.M365Group),
                    $"Create call queue '{name}' with agent group '{variables.M365Group}' and resource account '{variables.RacqUPN}'. " +
                    "Members of the group can wait up to eight hours for their first call (a delay in Teams)."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "create_auto_attendant", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Creates the auto attendant from the configuration: it builds the default and after-hours call flows and the opening-hours schedule in one script. The configuration must be valid. An existing auto attendant with the same name is updated to the configuration." + ChangeNote)]
    public Task<string> CreateAutoAttendant(
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            // The same check as Guided Setup step 7: a configuration with a problem never reaches the script.
            _store.RequireUsable();
            var variables = _store.Variables;
            var name = Pick(null, variables.AaDisplayName, "auto attendant name");
            var script = _commands.GetCreateAutoAttendantCommand(variables);
            return await _changes.PlanOrApplyAsync(
                Creating(TenantObjectType.AutoAttendant, name, "CreateAutoAttendant", script,
                    $"Create auto attendant '{name}'.") with
                {
                    ChangeWasMadeDespiteError = await _guard.CreatedDespiteLateModuleErrorAsync(
                        TenantObjectType.AutoAttendant, name, applying: !string.IsNullOrWhiteSpace(planId), cancellationToken)
                },
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "associate_resource_account", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Associates a resource account with its call queue or auto attendant, so calls to the account reach it." + ChangeNote)]
    public Task<string> AssociateResourceAccount(
        [Description("callQueue or autoAttendant.")] string kind,
        [Description("User principal name of the resource account. Omit to use the configuration.")] string? upn = null,
        [Description("Name of the call queue or auto attendant. Omit to use the configuration.")] string? name = null,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            var isQueue = IsCallQueue(kind);
            var accountUpn = Pick(upn, (isQueue ? variables.RacqUPN : variables.RaaaUPN), "UPN");
            var targetName = Pick(name, (isQueue ? variables.CqDisplayName : variables.AaDisplayName), "name");
            var script = isQueue
                ? _commands.GetAssociateResourceAccountWithCallQueueCommand(accountUpn, targetName)
                : _commands.GetAssociateResourceAccountWithAutoAttendantCommand(accountUpn, targetName);
            return await _changes.PlanOrApplyAsync(
                new ChangeRequest("AssociateResourceAccount", targetName, script,
                    $"Associate '{accountUpn}' with {(isQueue ? "call queue" : "auto attendant")} '{targetName}'."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "create_holiday_schedule", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Creates the holiday schedule from the holiday entries of the configuration (holidayTemplate.series). Fill the series first: swiss_holidays_apply for the public holidays of a Swiss canton, configuration_update for other dates." + ChangeNote)]
    public Task<string> CreateHolidaySchedule(
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            if (variables.HolidaySeries.Count == 0)
            {
                throw new InvalidOperationException(
                    "The configuration has no holiday entries. Fill them with swiss_holidays_apply, or set holidayTemplate.series with configuration_update first.");
            }

            var name = Pick(null, variables.HolidayName, "holiday schedule name");
            return await _changes.PlanOrApplyAsync(
                Creating(TenantObjectType.HolidaySchedule, name, "CreateHolidaySeries",
                    _commands.GetCreateHolidaySeriesFromEntriesCommand(name, variables.HolidaySeries),
                    $"Create holiday schedule '{name}' with {variables.HolidaySeries.Count} date(s)."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "verify_auto_attendant", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Checks that an auto attendant exists in the tenant. Use it before attaching a holiday schedule. Read-only.")]
    public Task<string> VerifyAutoAttendant(
        [Description("Auto attendant name. Omit to use the configuration.")] string? name = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var attendantName = Pick(name, _store.Variables.AaDisplayName, "auto attendant name");
            var result = await _tenant.ReadAsync(_commands.GetVerifyAutoAttendantCommand(attendantName), "VerifyAutoAttendant", cancellationToken);
            return new
            {
                autoAttendant = attendantName,
                exists = result.Succeeded,
                output = McpJson.Truncate(result.RawOutput, 2000)
            };
        });

    [McpServerTool(Name = "attach_holiday_schedule", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Attaches a holiday schedule to an auto attendant with a spoken greeting, after which the call ends: callers hear the greeting on those dates. For another call handling (audio file, transfer, voicemail, no greeting) copy the call handling of an existing schedule with copy_holiday_call_handling. The first call reads the auto attendant and refuses the plan when Teams would not save it: a holiday that starts at the same time, an overlap the user did not accept, the call flow limit, or authorized users that no longer exist. A schedule that is attached already is reported and not attached again. After the save the server reads the auto attendant again and reports whether the schedule is attached and whether Teams holds the greeting exactly as it was sent." + ChangeNote)]
    public Task<string> AttachHolidaySchedule(
        [Description("Holiday schedule name. Omit to use the configuration.")] string? holidayName = null,
        [Description("Auto attendant name. Omit to use the configuration.")] string? autoAttendantName = null,
        [Description("Greeting text read to callers on a holiday. Omit to use the configuration.")] string? greeting = null,
        [Description("True when the user accepts that the schedule overlaps a holiday that is attached already. Two holidays that start at the same time are refused in every case.")] bool acceptOverlaps = false,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            var schedule = Pick(holidayName, variables.HolidayName, "holiday schedule name");
            var attendant = Pick(autoAttendantName, variables.AaDisplayName, "auto attendant name");
            var prompt = Require(greeting ?? variables.HolidayGreetingPromptDE, "holiday greeting");

            // The script finds both objects by name and takes what it finds. The checks need the IDs.
            var (attendantId, _) = await _holidays.ResolveAutoAttendantAsync(attendant, cancellationToken);
            var before = await _holidays.ReadAsync(new[] { attendantId }, true, true, cancellationToken);
            var named = before.Schedules
                .Where(item => string.Equals(item.Name.Trim(), schedule, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (named.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No schedule is named '{schedule}'. Create it first (create_holiday_schedule); list_holiday_schedules shows the schedules of the tenant.");
            }

            if (named.Count > 1)
            {
                throw new InvalidOperationException(
                    $"{named.Count} schedules are named '{schedule}' ({string.Join(", ", named.Select(item => item.Id))}), so the name does not say which one to attach. " +
                    "Rename one of them in Teams first.");
            }

            var scheduleId = named[0].Id;
            var assessment = HolidayCallHandling.AssessAttach(
                before,
                attendantId,
                scheduleId,
                acceptOverlaps,
                HolidayCallHandlingTools.NowFor(_time, await _tenant.GetTopologyAsync(false, cancellationToken), attendantId));
            var overlaps = assessment.Overlaps.Select(HolidayCallHandlingTools.OverlapView).ToList();
            if (assessment.AlreadyAttached)
            {
                return new
                {
                    status = "alreadyAttached",
                    success = true,
                    note = $"Schedule '{schedule}' is attached to auto attendant '{attendant}' already. Nothing was changed.",
                    verification = VerifyAttachedGreeting(before, attendantId, scheduleId, prompt)
                };
            }

            if (assessment.Blockers.Count > 0)
            {
                return new
                {
                    status = "blocked",
                    success = false,
                    error = "The schedule cannot be attached: " + string.Join(" ", assessment.Blockers.Select(blocker => blocker.Message)),
                    blockers = assessment.Blockers,
                    overlaps,
                    next = "Nothing was changed and no plan was made. Remove the cause or change the arguments, then call the tool again."
                };
            }

            var applied = false;
            var outcome = await _changes.PlanOrApplyAsync(
                new ChangeRequest("AttachHolidayToAutoAttendant", attendant,
                    _commands.GetAttachHolidayToAutoAttendantCommand(schedule, attendant, prompt),
                    $"Attach holiday schedule '{schedule}' to auto attendant '{attendant}'.")
                {
                    ScopedBackupAsync = _backups.For(new[] { attendantId }, new[] { scheduleId }),
                    OnSuccess = _ => applied = true,
                    // The schedule was not attached before this call, so finding it attached proves the save.
                    ChangeWasMadeDespiteError = async (_, token) =>
                    {
                        var after = await _holidays.ReadAsync(new[] { attendantId }, true, false, token);
                        return after.FindAutoAttendant(attendantId)?.HolidayLinkFor(scheduleId) is not null;
                    }
                },
                planId,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(planId))
            {
                return overlaps.Count == 0 ? outcome : McpJson.With(outcome, "overlaps", overlaps);
            }

            if (!applied)
            {
                return outcome;
            }

            var saved = await _holidays.ReadAsync(new[] { attendantId }, true, false, cancellationToken);
            return McpJson.With(outcome, "verification", VerifyAttachedGreeting(saved, attendantId, scheduleId, prompt));
        });

    /// <summary>Compares the greeting Teams holds for the schedule with the greeting that was sent, character by character.</summary>
    internal static object VerifyAttachedGreeting(HolidayCallHandlingState state, string attendantId, string scheduleId, string greeting)
    {
        var attendant = state.FindAutoAttendant(attendantId);
        var link = attendant?.HolidayLinkFor(scheduleId);
        var flow = link is null ? null : attendant!.FindCallFlow(link.CallFlowId);
        var stored = flow?.Greetings.Select(item => item.Text).FirstOrDefault(text => !string.IsNullOrEmpty(text));
        return new
        {
            attached = link is not null,
            greetingAsSent = link is not null && string.Equals(stored, greeting, StringComparison.Ordinal),
            greetingInTeams = stored,
            callHandling = HolidayCallHandling.Describe(flow)
        };
    }

    [McpServerTool(Name = "list_audio_files", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lists the audio files Teams holds for one application, with their IDs. A file is uploaded for one application and works only there: 'autoAttendant' (Teams name OrgAutoAttendant) for a greeting of an auto attendant, 'callQueue' (Teams name HuntGroup) for a greeting or the music on hold of a call queue. Use it to take a file that was uploaded before by its ID instead of uploading it again. Two files can have the same name; the ID tells them apart. Read-only.")]
    public Task<string> ListAudioFiles(
        [Description("'autoAttendant' or 'callQueue'.")] string application,
        [Description("Only files whose name or ID contains this text.")] string? search = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var app = AudioFiles.NormalizeApplication(application);
            var result = await _tenant.ReadAsync(AudioFiles.GetListCommand(app), "ListAudioFiles", cancellationToken);
            var wanted = search?.Trim();
            var files = AudioFiles.Parse(string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput)
                .Where(file => string.IsNullOrEmpty(wanted) ||
                               file.FileName.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                               file.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new
            {
                application = app,
                usedFor = AudioFiles.Describe(app),
                total = files.Count,
                sameNameMoreThanOnce = files.GroupBy(file => file.FileName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToList(),
                files
            };
        });

    [McpServerTool(Name = "import_audio_file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Uploads an audio file (WAV, MP3 or WMA) from this computer to Teams for ONE application and returns its audio file ID. Name the application: 'autoAttendant' for a greeting of an auto attendant (configuration paths autoAttendantTemplate.*AudioFileId, replace_holiday_greeting), 'callQueue' for a greeting, the music on hold or a shared voicemail greeting of a call queue (callQueueTemplate.*AudioFileId). A file that was uploaded for the other application is not found when it is used. The plan shows the application, the file with its size and the SHA-256 of its content. When this server uploaded the same content for the same application before, the call returns that audio file ID and uploads nothing, unless forceNewUpload is true; a file with the same name but other content is a new upload. Teams converts an upload, so two uploads cannot be compared by downloading them: the ID is what identifies a file. Teams removes an uploaded file by itself after 48 to 72 hours when nothing uses it; it cannot be removed earlier." + ChangeNote)]
    public Task<string> ImportAudioFile(
        [Description("Full path of the audio file on this computer.")] string path,
        [Description("'autoAttendant' or 'callQueue': the application that will play the file.")] string application,
        [Description("True to upload again although this server uploaded the same content for this application before.")] bool forceNewUpload = false,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var app = AudioFiles.NormalizeApplication(application);
            var fullPath = McpFiles.LocalFullPath(path);
            if (!AudioExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only .wav, .mp3 and .wma files can be uploaded.");
            }

            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"'{fullPath}' does not exist.");
            }

            var fileName = Path.GetFileName(fullPath);
            var size = new FileInfo(fullPath).Length;
            var hash = AudioUploadLog.Hash(fullPath);
            var file = new { name = fileName, path = fullPath, sizeBytes = size, sha256 = hash };
            var earlier = _uploads.Find(hash, app, _tenant.TenantId);
            if (earlier.Count > 0 && !forceNewUpload)
            {
                return new
                {
                    status = "alreadyUploaded",
                    success = true,
                    audioFileId = earlier[0].AudioFileId,
                    application = app,
                    file,
                    uploadedUtc = earlier[0].UploadedUtc,
                    note = "This server uploaded the same content for this application before. Use this audio file ID; nothing was uploaded. " +
                           "list_audio_files shows whether Teams still has the file. Set forceNewUpload=true to upload it again."
                };
            }

            string? audioFileId = null;
            var outcome = await _changes.PlanOrApplyAsync(
                new ChangeRequest("ImportAudioFile", fileName,
                    _commands.GetImportAudioFileCommand(fullPath, app),
                    $"Upload audio file '{fileName}' ({size} bytes, SHA-256 {hash}) to Teams for {AudioFiles.Describe(app)} (application {app}).")
                {
                    // The plan is for this content: a file that was replaced on disk needs a new plan.
                    ApprovedState = hash + "|" + app,
                    OnSuccess = result =>
                    {
                        audioFileId = TenantOutputParser.ParseAudioFileId(result.RawOutput);
                        if (audioFileId is not null)
                        {
                            _uploads.Add(new AudioUpload(hash, fileName, app, audioFileId, _tenant.TenantId, _time.GetUtcNow()));
                        }
                    }
                },
                planId,
                cancellationToken);
            return audioFileId is null
                ? new { application = app, usedFor = AudioFiles.Describe(app), file, result = outcome } as object
                : new { audioFileId, application = app, usedFor = AudioFiles.Describe(app), file, result = outcome };
        });

    private ChangeRequest Creating(TenantObjectType type, string key, string operation, string script, string summary)
        => new(operation, key, script, summary)
        {
            // A failed script can still have created the object; the guard then confirms it in the tenant.
            OnSuccessAsync = (result, token) => _guard.ClaimAsync(type, key, operation, result.RawOutput, token),
            OnFailureAsync = (result, token) => _guard.ClaimAfterFailureAsync(type, key, operation, result, token)
        };

    /// <summary>A UPN without a domain gets the fallback domain, as on the desktop pages; any other malformed UPN is refused.</summary>
    internal static string CompleteUpn(string upn, string? fallbackDomain)
    {
        var value = upn.Trim();
        if (!value.Contains('@'))
        {
            if (!PhoneDesk.Models.ResourceAccountUpn.IsValidFallbackDomain(fallbackDomain))
            {
                throw new ArgumentException(
                    $"'{value}' has no domain. Give the full user principal name, or set general.msFallbackDomain in the configuration.");
            }

            value = PhoneDesk.Models.ResourceAccountUpn.Build(value, fallbackDomain);
        }

        var parts = value.Split('@');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[0].Any(char.IsWhiteSpace) ||
            !PhoneDesk.Models.ResourceAccountUpn.IsValidFallbackDomain(parts[1]))
        {
            throw new ArgumentException($"'{value}' is not a user principal name. Example: racq-contoso-reception@contoso.onmicrosoft.com.");
        }

        return value;
    }

    /// <summary>
    /// Usage location, license and phone number go to a resource account only. Without this check
    /// a mistyped UPN, or the UPN of a person, would get the license or the number.
    /// </summary>
    private async Task RequireResourceAccountAsync(string upn, ConfigurationDocumentVariables variables, CancellationToken cancellationToken)
    {
        bool Same(string? other) => string.Equals(other?.Trim(), upn, StringComparison.OrdinalIgnoreCase);

        // The accounts of the configuration and the ones this server created need no tenant read:
        // a new resource account is not visible in the tenant for some seconds.
        if ((!string.IsNullOrWhiteSpace(variables.Customer) && (Same(variables.RacqUPN) || Same(variables.RaaaUPN))) ||
            _ledger.Contains(_tenant.TenantId, TenantObjectType.ResourceAccount, upn))
        {
            return;
        }

        foreach (var refresh in new[] { false, true })
        {
            var topology = await _tenant.GetTopologyAsync(refresh, cancellationToken);
            if (topology.ResourceAccounts.Any(account => Same(account.UserPrincipalName)))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"'{upn}' is not a resource account of this tenant. Check the spelling with list_resource_accounts. " +
            "Usage location, license and phone number are set on resource accounts only.");
    }

    internal static bool IsCallQueue(string kind)
        => kind?.Trim().ToLowerInvariant() switch
        {
            "callqueue" or "cq" => true,
            "autoattendant" or "aa" => false,
            _ => throw new ArgumentException("kind must be callQueue or autoAttendant.")
        };

    /// <summary>Returns the argument, or the name computed from the configuration when the argument was left out.</summary>
    private string Pick(string? argument, string configured, string what)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            return argument.Trim();
        }

        var variables = _store.Variables;
        if (string.IsNullOrWhiteSpace(variables.Customer) || string.IsNullOrWhiteSpace(variables.CustomerGroupName))
        {
            throw new InvalidOperationException(
                $"The {what} is missing. Pass it as an argument, or set general.customer and general.customerGroupName in the configuration first.");
        }

        return Require(configured, what);
    }

    internal static string Require(string? value, string what)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"The {what} is missing. Pass it as an argument or set it in the configuration first.")
            : value.Trim();
}
