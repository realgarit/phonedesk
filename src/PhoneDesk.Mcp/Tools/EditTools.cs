using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;

namespace PhoneDesk.Mcp.Tools;

/// <summary>
/// Changes to a call queue or an auto attendant that exists. The desktop app has the same on its
/// Edit Settings page; both use <see cref="ObjectEdit"/>.
/// </summary>
[McpServerToolType]
internal sealed class EditTools
{
    private const string ObjectDescription = "The exact name of the object, or its ID. A name that several objects have is refused; use the ID then (list_call_queues, list_auto_attendants).";
    private const string ChangesDescription = "Map of setting key to new value, with only the settings to change. The keys and the current values are in the result of the matching get_*_settings tool. An action and its target go in the same call.";
    private const string PlanIdDescription = "The planId from the first call. Omit to get the plan.";
    private const string EditNote =
        " CHANGES THE TENANT. Read the current settings first, then give only the settings to change. " +
        "Call it first without planId: it reads the object, returns each change with its old and new value and the exact script, and changes nothing. " +
        "Show the changes to the user, then call it again with the same arguments and the planId. Settings that already have the wanted value are not sent. " +
        "Each setting is saved on its own; the result says for each one whether it was saved and whether a new read of the object shows it. " +
        "An object that changed in the tenant after the plan refuses the plan.";

    private readonly TenantGateway _tenant;
    private readonly ChangeGate _changes;

    public EditTools(TenantGateway tenant, ChangeGate changes)
    {
        _tenant = tenant;
        _changes = changes;
    }

    [McpServerTool(Name = "get_call_queue_settings", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads the settings of one call queue that edit_call_queue can change, with their current values: routing method, agent alert time, language, greeting text, opt-out, presence-based routing, conference mode, agent groups, and the overflow and timeout thresholds, actions and targets. Each setting has its key, type, the allowed values or range, and a note when it cannot be changed here (a greeting that is an audio file). Read-only.")]
    public Task<string> GetCallQueueSettings(
        [Description(ObjectDescription)] string callQueue,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () => View(await ReadAsync(EditObjectKind.CallQueue, callQueue, cancellationToken)));

    [McpServerTool(Name = "get_auto_attendant_settings", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Reads the settings of one auto attendant that edit_auto_attendant can change, with their current values: language, time zone, the greeting text and the action with its target in opening hours, the opening hours (one entry per day, like 'Mon 08:00-12:00,13:00-17:00; Sat closed'), and the greeting text and action after hours. A call flow that has a menu with keys, or a greeting that is an audio file, is marked as not changeable here. 'notes' says when the schedule of the opening hours is shared with other auto attendants. For holidays use the holiday tools. Read-only.")]
    public Task<string> GetAutoAttendantSettings(
        [Description(ObjectDescription)] string autoAttendant,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () => View(await ReadAsync(EditObjectKind.AutoAttendant, autoAttendant, cancellationToken)));

    [McpServerTool(Name = "edit_call_queue", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Changes settings of a call queue that exists." + EditNote + " Settings: routingMethod, agentAlertTime, language, greetingText, allowOptOut, presenceBasedRouting, conferenceMode, agentGroups (group object IDs, comma-separated), overflowThreshold, overflowAction with overflowTarget, timeoutThreshold, timeoutAction with timeoutTarget. A target is a sign-in name or an object ID.")]
    public Task<string> EditCallQueue(
        [Description(ObjectDescription)] string callQueue,
        [Description(ChangesDescription)] Dictionary<string, string> changes,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(() => EditAsync(EditObjectKind.CallQueue, callQueue, changes, planId, cancellationToken));

    [McpServerTool(Name = "edit_auto_attendant", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Changes settings of an auto attendant that exists." + EditNote + " Settings: language, timeZone, greetingText, defaultAction (Disconnect or Transfer) with defaultTarget, businessHours, afterHoursGreetingText, afterHoursAction with afterHoursTarget. The opening hours are stored in a schedule; when get_auto_attendant_settings says it is shared, the new hours apply to every auto attendant that uses it, so tell the user. A menu with keys is not changed by this tool.")]
    public Task<string> EditAutoAttendant(
        [Description(ObjectDescription)] string autoAttendant,
        [Description(ChangesDescription)] Dictionary<string, string> changes,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(() => EditAsync(EditObjectKind.AutoAttendant, autoAttendant, changes, planId, cancellationToken));

    private async Task<object> EditAsync(
        EditObjectKind kind, string target, Dictionary<string, string>? changes, string? planId, CancellationToken cancellationToken)
    {
        if (changes is null || changes.Count == 0)
        {
            throw new ArgumentException("Give at least one setting to change.");
        }

        var state = await ReadAsync(kind, target, cancellationToken);
        var assessment = ObjectEdit.Assess(state, changes.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase));
        if (assessment.Blockers.Count > 0)
        {
            throw new InvalidOperationException("Nothing was changed. " + string.Join(" ", assessment.Blockers.Select(blocker => blocker.Message)));
        }

        var planned = assessment.Changes.Select(change => new { setting = change.Key, label = change.Label, from = change.Old, to = change.New }).ToList();
        if (assessment.Changes.Count == 0)
        {
            return new { success = true, applied = false, nothingToChange = true, message = "The object already has these values.", @object = new { id = state.Id, name = state.Name } };
        }

        IReadOnlyList<EditOutcome>? outcomes = null;
        var result = await _changes.PlanOrApplyAsync(
            new ChangeRequest(
                kind == EditObjectKind.CallQueue ? "EditCallQueue" : "EditAutoAttendant",
                state.Name,
                ObjectEdit.GetApplyCommand(assessment),
                $"Change {assessment.Changes.Count} setting(s) of '{state.Name}': " +
                string.Join("; ", assessment.Changes.Select(change => $"{change.Label}: '{change.Old}' -> '{change.New}'")) + ".")
            {
                ApprovedState = ObjectEdit.ApprovedState(assessment),
                // Each setting reports on its own: the change succeeded when every one was saved.
                IsSuccess = scriptResult =>
                {
                    outcomes = ObjectEdit.ParseOutcome(assessment, string.IsNullOrEmpty(scriptResult.RawOutput) ? scriptResult.Value : scriptResult.RawOutput);
                    return outcomes.All(outcome => outcome.Saved);
                }
            },
            planId,
            cancellationToken);

        result = McpJson.With(McpJson.With(result, "changes", planned), "warnings", assessment.Warnings.Count == 0 ? null : assessment.Warnings);
        if (outcomes is null)
        {
            return result;
        }

        // Read again in every case: after a part of the settings failed, the user must know what the object holds.
        EditObjectState? after = null;
        string? readProblem = null;
        try
        {
            after = await ReadAsync(kind, state.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            // The change was sent. Without a new read nothing of it is confirmed, and that is said.
            readProblem = ex.Message;
        }

        var open = after is null ? null : ObjectEdit.NotAsWanted(assessment, after).Select(change => change.Key).ToHashSet(StringComparer.Ordinal);
        result = McpJson.With(result, "settings", outcomes.Select(outcome => new
        {
            setting = outcome.Key,
            saved = outcome.Saved,
            shownByTenant = open is null ? (bool?)null : !open.Contains(outcome.Key),
            message = outcome.Message,
            now = after is not null && after.Values.TryGetValue(outcome.Key, out var now) ? now : null
        }));
        return readProblem is null
            ? result
            : McpJson.With(result, "notConfirmed", "The change was sent, but the object could not be read again, so no setting is confirmed: " + readProblem + " Read the settings again before you tell the user the result.");
    }

    private async Task<EditObjectState> ReadAsync(EditObjectKind kind, string target, CancellationToken cancellationToken)
    {
        var read = await _tenant.ReadAsync(ObjectEdit.GetReadCommand(kind, target), "ReadObjectSettings", cancellationToken);
        return ObjectEdit.Parse(kind, string.IsNullOrEmpty(read.RawOutput) ? read.Value : read.RawOutput);
    }

    private static object View(EditObjectState state) => new
    {
        id = state.Id,
        name = state.Name,
        kind = state.Kind,
        notes = state.Notes.Count == 0 ? null : state.Notes,
        settings = ObjectEdit.Fields(state.Kind).Select(field => new
        {
            key = field.Key,
            label = field.Label,
            type = field.Type,
            value = state.Values.TryGetValue(field.Key, out var value) ? value : null,
            allowed = field.Choices.Count == 0 ? null : field.Choices,
            range = field.Type == EditFieldType.Number ? $"{field.Minimum} to {field.Maximum}" : null,
            sentWith = field.SentWith,
            cannotBeChanged = state.Locked.TryGetValue(field.Key, out var reason) ? reason : null,
            help = field.Help
        })
    };
}
