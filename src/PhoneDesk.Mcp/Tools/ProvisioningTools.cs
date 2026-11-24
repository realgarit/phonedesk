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

    public ProvisioningTools(
        ConfigurationStore store,
        IPowerShellCommandService commands,
        ChangeGate changes,
        TenantGateway tenant,
        CreatedObjectGuard guard)
    {
        _store = store;
        _commands = commands;
        _changes = changes;
        _tenant = tenant;
        _guard = guard;
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
    [Description("Looks up the object ID of a Microsoft 365 group by name. With saveToConfiguration=true the ID is written to general.m365GroupId of the local configuration. Reads from the tenant only.")]
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
    [Description("Creates a Teams resource account for a call queue or an auto attendant. An existing account with the same UPN is reused." + ChangeNote)]
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
            var accountUpn = Pick(upn, (isQueue ? variables.RacqUPN : variables.RaaaUPN), "UPN");
            var accountName = Pick(displayName, (isQueue ? variables.RacqDisplayName : variables.RaaaDisplayName), "display name");
            var script = isQueue
                ? _commands.GetCreateResourceAccountCommand(accountUpn, accountName, variables.CsAppCqId)
                : _commands.GetCreateAutoAttendantResourceAccountCommand(accountUpn, accountName, variables.CsAppAaId);
            return await _changes.PlanOrApplyAsync(
                Creating(TenantObjectType.ResourceAccount, accountUpn, "CreateResourceAccount", script,
                    $"Create the {(isQueue ? "call queue" : "auto attendant")} resource account '{accountUpn}'."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "set_resource_account_usage_location", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Sets the usage location (two-letter country code) of a resource account. A license can be assigned only after this." + ChangeNote)]
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
            var location = Require(usageLocation ?? variables.UsageLocation, "usage location");
            var script = isQueue
                ? _commands.GetUpdateResourceAccountUsageLocationCommand(accountUpn, location)
                : _commands.GetUpdateAutoAttendantResourceAccountUsageLocationCommand(accountUpn, location);
            return await _changes.PlanOrApplyAsync(
                new ChangeRequest("UpdateUsageLocation", accountUpn, script, $"Set the usage location of '{accountUpn}' to {location}."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "assign_resource_account_license", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Assigns the Teams Phone resource account license to a resource account. The tenant must have a free license and the account needs a usage location." + ChangeNote)]
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
    [Description("Assigns a phone number to an auto attendant resource account. The account needs its license first." + ChangeNote)]
    public Task<string> AssignPhoneNumber(
        [Description("User principal name. Omit to use the auto attendant resource account from the configuration.")] string? upn = null,
        [Description("Phone number in E.164 format, for example +41441234567. Omit to use the configuration.")] string? phoneNumber = null,
        [Description("Phone number type, for example DirectRouting, CallingPlan or OperatorConnect. Omit to use the configuration.")] string? phoneNumberType = null,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            var accountUpn = Pick(upn, variables.RaaaUPN, "UPN");
            var number = Require(phoneNumber ?? variables.RaaAnr, "phone number");
            var type = Require(phoneNumberType ?? variables.PhoneNumberType, "phone number type");
            return await _changes.PlanOrApplyAsync(
                new ChangeRequest("AssignPhoneNumber", accountUpn,
                    _commands.GetAssignPhoneNumberToAutoAttendantCommand(accountUpn, number, type),
                    $"Assign {number} ({type}) to '{accountUpn}'."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "create_call_queue", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Creates the call queue from the configuration (name, language, agent group, greeting, music on hold, overflow, timeout and no-agent handling) and associates it with its resource account. An existing call queue with the same name is updated to the configuration." + ChangeNote)]
    public Task<string> CreateCallQueue(
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            var name = Pick(null, variables.CqDisplayName, "call queue name");
            return await _changes.PlanOrApplyAsync(
                Creating(TenantObjectType.CallQueue, name, "CreateCallQueue",
                    _commands.GetCreateCallQueueCommand(variables), $"Create call queue '{name}'."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "create_auto_attendant", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Creates the auto attendant from the configuration. By default it builds the default and after-hours call flows and the opening-hours schedule in one script; an existing auto attendant with the same name is updated to the configuration. With simple=true it creates a minimal auto attendant with only name, language and time zone." + ChangeNote)]
    public Task<string> CreateAutoAttendant(
        [Description("True for a minimal auto attendant without call flows.")] bool simple = false,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            var name = Pick(null, variables.AaDisplayName, "auto attendant name");
            var script = simple
                ? _commands.GetCreateSimpleAutoAttendantCommand(variables)
                : _commands.GetCreateAutoAttendantCommand(variables);
            return await _changes.PlanOrApplyAsync(
                Creating(TenantObjectType.AutoAttendant, name, "CreateAutoAttendant", script,
                    $"Create {(simple ? "a minimal " : string.Empty)}auto attendant '{name}'."),
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
    [Description("Creates the holiday schedule from the holiday entries of the configuration (holidayTemplate.series). Fill the series first, for example from swiss_holidays." + ChangeNote)]
    public Task<string> CreateHolidaySchedule(
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            if (variables.HolidaySeries.Count == 0)
            {
                throw new InvalidOperationException(
                    "The configuration has no holiday entries. Set holidayTemplate.series with configuration_update first.");
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
    [Description("Attaches a holiday schedule to an auto attendant with a spoken greeting, so callers hear the greeting on those dates." + ChangeNote)]
    public Task<string> AttachHolidaySchedule(
        [Description("Holiday schedule name. Omit to use the configuration.")] string? holidayName = null,
        [Description("Auto attendant name. Omit to use the configuration.")] string? autoAttendantName = null,
        [Description("Greeting text read to callers on a holiday. Omit to use the configuration.")] string? greeting = null,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var variables = _store.Variables;
            var schedule = Pick(holidayName, variables.HolidayName, "holiday schedule name");
            var attendant = Pick(autoAttendantName, variables.AaDisplayName, "auto attendant name");
            var prompt = Require(greeting ?? variables.HolidayGreetingPromptDE, "holiday greeting");
            return await _changes.PlanOrApplyAsync(
                new ChangeRequest("AttachHolidayToAutoAttendant", attendant,
                    _commands.GetAttachHolidayToAutoAttendantCommand(schedule, attendant, prompt),
                    $"Attach holiday schedule '{schedule}' to auto attendant '{attendant}'."),
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "import_audio_file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Uploads an audio file (WAV, MP3 or WMA) from this computer to Teams and returns its audio file ID, which the configuration uses for greetings and music on hold. Teams removes an uploaded file by itself after 48 to 72 hours when no call queue or auto attendant uses it; it cannot be removed earlier." + ChangeNote)]
    public Task<string> ImportAudioFile(
        [Description("Full path of the audio file on this computer.")] string path,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var fullPath = Path.GetFullPath(path);
            if (!AudioExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only .wav, .mp3 and .wma files can be uploaded.");
            }

            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"'{fullPath}' does not exist.");
            }

            string? audioFileId = null;
            var outcome = await _changes.PlanOrApplyAsync(
                new ChangeRequest("ImportAudioFile", Path.GetFileName(fullPath),
                    _commands.GetImportAudioFileCommand(fullPath), $"Upload audio file '{fullPath}' to Teams.")
                {
                    OnSuccess = result => audioFileId = TenantOutputParser.ParseAudioFileId(result.RawOutput)
                },
                planId,
                cancellationToken);
            return audioFileId is null ? outcome : new { audioFileId, result = outcome };
        });

    private ChangeRequest Creating(TenantObjectType type, string key, string operation, string script, string summary)
        => new(operation, key, script, summary)
        {
            // A failed script can still have created the object, so both outcomes report to the guard.
            OnSuccess = result => _guard.Claim(type, key, operation, result.RawOutput),
            OnFailure = result => _guard.Claim(type, key, operation, result.RawOutput),
            ChangeWasMadeDespiteError = type == TenantObjectType.AutoAttendant
                ? (result, token) => _guard.ExistsAfterLateModuleErrorAsync(type, key, result, token)
                : null
        };

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
