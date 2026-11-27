using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhoneDesk.Operations;

public enum EditObjectKind
{
    CallQueue,
    AutoAttendant
}

public enum EditFieldType
{
    Text,
    Choice,
    Number,
    Bool,
    Hours
}

/// <summary>One setting of a call queue or an auto attendant that can be changed.</summary>
public sealed record EditField(string Key, string Label, EditFieldType Type, string Help)
{
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    public int Minimum { get; init; }

    public int Maximum { get; init; }

    /// <summary>The key of the setting that is always sent together with this one (an action and its target).</summary>
    public string? SentWith { get; init; }
}

/// <summary>What an object holds now, read from the tenant.</summary>
public sealed class EditObjectState
{
    public EditObjectKind Kind { get; init; }

    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>The current value of every field that can be changed for this object.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

    /// <summary>Fields that cannot be changed for this object, with the reason.</summary>
    public IReadOnlyDictionary<string, string> Locked { get; init; } = new Dictionary<string, string>();

    /// <summary>What an operator must know before they change the object.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The objects a change reaches beside the object itself: for an auto attendant the schedule of
    /// its opening hours, who else uses that schedule, and its after-hours call flow. A change is
    /// approved for this reach, and it is saved only to these objects.
    /// </summary>
    public IReadOnlyDictionary<string, string> Scope { get; init; } = new Dictionary<string, string>();
}

public sealed record EditChange(string Key, string Label, string Old, string New);

public sealed class EditAssessment
{
    public EditObjectState State { get; init; } = new();

    /// <summary>The settings that differ from the tenant. Only these are sent.</summary>
    public IReadOnlyList<EditChange> Changes { get; init; } = Array.Empty<EditChange>();

    public IReadOnlyList<HolidayBlocker> Blockers { get; init; } = Array.Empty<HolidayBlocker>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool CanApply => Blockers.Count == 0 && Changes.Count > 0;
}

public sealed record EditOutcome(string Key, string Label, bool Saved, string Message);

/// <summary>
/// Changes to a call queue or an auto attendant that exists. The flow is the same in both heads:
/// read the object, compare the wanted values with it, send only the settings that differ, one
/// setting at a time, and report each one. The scripts take the object by its ID, and every value
/// travels inside one Base64 text, so a name or a greeting can hold any character.
/// </summary>
public static class ObjectEdit
{
    public const string ReadMarker = "PDEDIT_JSON: ";
    public const string PropertyMarker = "PDEDIT_PROP: ";

    /// <summary>A sign-in name the script resolved, with the object ID Teams stores for it.</summary>
    public const string TargetMarker = "PDEDIT_TARGET: ";

    public const string Disconnect = "Disconnect";
    public const string Transfer = "Transfer";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] Days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    private static readonly IReadOnlyList<EditField> CallQueueFields = new[]
    {
        new EditField("routingMethod", "Routing method", EditFieldType.Choice, "How calls are given to the agents.") { Choices = new[] { "Attendant", "Serial", "RoundRobin", "LongestIdle" } },
        new EditField("agentAlertTime", "Agent alert time (seconds)", EditFieldType.Number, "How long an agent is called before the next one.") { Minimum = 15, Maximum = 180 },
        new EditField("language", "Language", EditFieldType.Text, "Language of the system prompts, for example de-DE."),
        new EditField("greetingText", "Greeting (spoken text)", EditFieldType.Text, "The text callers hear first. Empty for no greeting."),
        new EditField("allowOptOut", "Agents can opt out", EditFieldType.Bool, "Whether an agent can stop taking calls of this queue."),
        new EditField("presenceBasedRouting", "Presence-based routing", EditFieldType.Bool, "Calls go only to agents whose presence is Available."),
        new EditField("conferenceMode", "Conference mode", EditFieldType.Bool, "Connects the caller and the agent faster."),
        new EditField("agentGroups", "Agent groups (IDs)", EditFieldType.Text, "Object IDs of the groups whose members are the agents, separated by commas."),
        new EditField("overflowThreshold", "Overflow threshold (calls)", EditFieldType.Number, "How many calls may wait before the overflow action applies.") { Minimum = 0, Maximum = 200 },
        new EditField("overflowAction", "Overflow action", EditFieldType.Choice, "What happens to a call over the threshold.") { Choices = new[] { "DisconnectWithBusy", "Forward", "Voicemail", "SharedVoicemail" }, SentWith = "overflowTarget" },
        new EditField("overflowTarget", "Overflow target", EditFieldType.Text, "Sign-in name or object ID of the account the call goes to. A group ID for SharedVoicemail.") { SentWith = "overflowAction" },
        new EditField("timeoutThreshold", "Timeout (seconds)", EditFieldType.Number, "How long a call may wait before the timeout action applies.") { Minimum = 0, Maximum = 2700 },
        new EditField("timeoutAction", "Timeout action", EditFieldType.Choice, "What happens to a call that waited too long.") { Choices = new[] { "Disconnect", "Forward", "Voicemail", "SharedVoicemail" }, SentWith = "timeoutTarget" },
        new EditField("timeoutTarget", "Timeout target", EditFieldType.Text, "Sign-in name or object ID of the account the call goes to. A group ID for SharedVoicemail.") { SentWith = "timeoutAction" }
    };

    private static readonly IReadOnlyList<EditField> AutoAttendantFields = new[]
    {
        new EditField("language", "Language", EditFieldType.Text, "Language of the spoken texts, for example de-DE."),
        new EditField("timeZone", "Time zone", EditFieldType.Text, "The Windows time zone name, for example W. Europe Standard Time."),
        new EditField("greetingText", "Greeting in opening hours (spoken text)", EditFieldType.Text, "The text callers hear in opening hours. Empty for no greeting."),
        new EditField("defaultAction", "Action in opening hours", EditFieldType.Choice, "What happens to a call in opening hours.") { Choices = new[] { Disconnect, Transfer }, SentWith = "defaultTarget" },
        new EditField("defaultTarget", "Target in opening hours", EditFieldType.Text, "Sign-in name or object ID of the account the call goes to: a resource account or a person.") { SentWith = "defaultAction" },
        new EditField("businessHours", "Opening hours", EditFieldType.Hours, "One entry per day, for example: Mon 08:00-12:00,13:00-17:00; Tue 08:00-17:00; Sat closed. Times in steps of 15 minutes."),
        new EditField("afterHoursGreetingText", "Greeting after hours (spoken text)", EditFieldType.Text, "The text callers hear outside opening hours. Empty for no greeting."),
        new EditField("afterHoursAction", "Action after hours", EditFieldType.Choice, "What happens to a call outside opening hours.") { Choices = new[] { Disconnect, Transfer }, SentWith = "afterHoursTarget" },
        new EditField("afterHoursTarget", "Target after hours", EditFieldType.Text, "Sign-in name or object ID of the account the call goes to.") { SentWith = "afterHoursAction" }
    };

    public static IReadOnlyList<EditField> Fields(EditObjectKind kind)
        => kind == EditObjectKind.CallQueue ? CallQueueFields : AutoAttendantFields;

    /// <summary>The read script. The object is given by its ID, or by its exact name when only one object has it.</summary>
    public static string GetReadCommand(EditObjectKind kind, string idOrName)
    {
        var value = (idOrName ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException("Give the name or the ID of the object.");
        }

        var request = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { value, isId = Guid.TryParse(value, out _) })));
        return (kind == EditObjectKind.CallQueue ? ReadCallQueueTemplate : ReadAutoAttendantTemplate)
            .Replace("__REQUEST__", request, StringComparison.Ordinal);
    }

    public static EditObjectState Parse(EditObjectKind kind, string? output)
    {
        var lines = (output ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var json = lines.LastOrDefault(line => line.StartsWith(ReadMarker, StringComparison.Ordinal));
        if (json is null)
        {
            var error = lines.FirstOrDefault(line => line.StartsWith("ERROR:", StringComparison.Ordinal));
            throw new InvalidDataException(error is null ? "The object could not be read." : error["ERROR:".Length..].Trim());
        }

        var raw = JsonSerializer.Deserialize<RawState>(json[ReadMarker.Length..], Options) ?? new RawState();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields(kind))
        {
            if (raw.Values.TryGetValue(field.Key, out var value) && !raw.Locked.ContainsKey(field.Key))
            {
                values[field.Key] = Normalize(field, value ?? string.Empty);
            }
        }

        return new EditObjectState
        {
            Kind = kind,
            Id = raw.Id,
            Name = raw.Name,
            Values = values,
            Locked = new Dictionary<string, string>(raw.Locked, StringComparer.OrdinalIgnoreCase),
            Notes = raw.Notes,
            Scope = new Dictionary<string, string>(raw.Scope.ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty), StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Compares the wanted values with the object. A value that is not given, or that equals what
    /// the tenant holds, is not a change and is not sent.
    /// </summary>
    public static EditAssessment Assess(EditObjectState state, IReadOnlyDictionary<string, string?> wanted)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(wanted);
        var blockers = new List<HolidayBlocker>();
        var changes = new List<EditChange>();
        var fields = Fields(state.Kind);
        if (!Guid.TryParse(state.Id, out _))
        {
            blockers.Add(new("noId", "The object has no ID the change could be sent to."));
        }

        foreach (var key in wanted.Keys.Where(key => fields.All(field => !string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))))
        {
            blockers.Add(new("unknownField", $"'{key}' is not a setting that can be changed. Settings: {string.Join(", ", fields.Select(field => field.Key))}."));
        }

        foreach (var field in fields)
        {
            var given = wanted.FirstOrDefault(pair => string.Equals(pair.Key, field.Key, StringComparison.OrdinalIgnoreCase));
            if (given.Key is null || given.Value is null)
            {
                continue;
            }

            if (state.Locked.TryGetValue(field.Key, out var reason))
            {
                blockers.Add(new("locked", $"{field.Label} cannot be changed here: {reason}"));
                continue;
            }

            if (!state.Values.TryGetValue(field.Key, out var current))
            {
                blockers.Add(new("notRead", $"{field.Label} was not read from the tenant, so it is not changed."));
                continue;
            }

            string value;
            try
            {
                value = Validate(field, given.Value);
            }
            catch (ArgumentException ex)
            {
                blockers.Add(new("invalid", $"{field.Label}: {ex.Message}"));
                continue;
            }

            if (!string.Equals(value, current, field.Type == EditFieldType.Text ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new EditChange(field.Key, field.Label, current, value));
            }
        }

        // An action that sends the call somewhere needs its target, and the two are sent in one call.
        string After(string key) => changes.FirstOrDefault(change => change.Key == key)?.New ?? (state.Values.TryGetValue(key, out var now) ? now : string.Empty);
        foreach (var field in fields.Where(field => field.Type == EditFieldType.Choice && field.SentWith is not null))
        {
            var touched = changes.Any(change => change.Key == field.Key || change.Key == field.SentWith);
            var action = After(field.Key);
            var target = After(field.SentWith!);
            var needsTarget = action is "Forward" or "Voicemail" or "SharedVoicemail" or Transfer;
            if (touched && needsTarget && target.Length == 0)
            {
                blockers.Add(new("targetMissing", $"{field.Label} '{action}' needs a target."));
            }
        }

        var warnings = new List<string>();
        if (changes.Any(change => change.Key == "businessHours") && state.Notes.Any(note => note.Contains("shared", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("The schedule of the opening hours is used by more than one auto attendant. The new hours apply to all of them.");
        }

        return new EditAssessment { State = state, Changes = changes, Blockers = blockers, Warnings = warnings };
    }

    /// <summary>
    /// The text an approval is bound to: the object, every change with its old and new value, and
    /// the other objects the change reaches. When the auto attendant is given another schedule
    /// after the check, or another auto attendant starts to use its schedule, the approval no
    /// longer holds.
    /// </summary>
    public static string ApprovedState(EditAssessment assessment)
        => assessment.State.Kind + "|" + assessment.State.Id + "\n" +
           string.Join("\n", assessment.Changes.Select(change => $"{change.Key}|{change.Old}|{change.New}")) + "\n" +
           string.Join("\n", assessment.State.Scope.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"scope:{pair.Key}={pair.Value}"));

    /// <summary>
    /// The script that saves the changes. Each setting is saved on its own behind a marker line, so
    /// one that fails does not stop the others and each has its own result. An action and its target
    /// are one step.
    /// </summary>
    public static string GetApplyCommand(EditAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (!assessment.CanApply)
        {
            throw new InvalidOperationException("There is nothing to save, or the change is blocked.");
        }

        var state = assessment.State;
        var id = Guid.Parse(state.Id).ToString("D");
        // The values after the change, for every field: a step that sends two settings needs both.
        var values = new Dictionary<string, string>(state.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var change in assessment.Changes)
        {
            values[change.Key] = change.New;
        }

        // The reach the change was approved for. The script saves to these objects and to no others.
        foreach (var (key, value) in state.Scope)
        {
            values["scope" + char.ToUpperInvariant(key[0]) + key[1..]] = value;
        }

        if (values.TryGetValue("businessHours", out var hours))
        {
            values["businessHoursJson"] = JsonSerializer.Serialize(BusinessHours.Parse(hours).ToDictionary(
                day => day.Key, day => day.Value.Select(range => new[] { Clock(range.Start), Clock(range.End) }).ToList()));
        }

        var script = new StringBuilder();
        script.Append("$pdEdit = ConvertFrom-Json ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('")
            .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).Append("')))\n");
        script.Append("$pdEditId = '").Append(id).Append("'\n").Append(Helpers).Append('\n');
        var templates = state.Kind == EditObjectKind.CallQueue ? CallQueueSteps : AutoAttendantSteps;
        foreach (var step in Steps(assessment))
        {
            script.Append("Write-Host '").Append(PropertyMarker).Append(step).Append("'\n");
            script.Append("try {\n").Append(templates[step]).Append("\n    Write-Host 'SUCCESS: saved'\n}\ncatch {\n    Write-Host \"ERROR: $_\"\n}\n");
        }

        return script.ToString();
    }

    /// <summary>The steps of a change, in the order of the fields. An action and its target are one step, named by the action.</summary>
    public static IReadOnlyList<string> Steps(EditAssessment assessment)
    {
        var steps = new List<string>();
        foreach (var field in Fields(assessment.State.Kind))
        {
            if (assessment.Changes.All(change => change.Key != field.Key))
            {
                continue;
            }

            var step = StepOf(field);
            if (!steps.Contains(step))
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    /// <summary>Reads the result of the script per setting.</summary>
    public static IReadOnlyList<EditOutcome> ParseOutcome(EditAssessment assessment, string? output)
    {
        var results = new Dictionary<string, (bool Saved, string Message)>(StringComparer.Ordinal);
        string? current = null;
        foreach (var line in (output ?? string.Empty).Split('\n').Select(line => line.Trim()))
        {
            if (line.StartsWith(PropertyMarker, StringComparison.Ordinal))
            {
                current = line[PropertyMarker.Length..].Trim();
                continue;
            }

            if (current is null || results.ContainsKey(current))
            {
                continue;
            }

            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                results[current] = (false, line);
            }
            else if (line.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                results[current] = (true, "Saved.");
            }
        }

        var fields = Fields(assessment.State.Kind);
        return assessment.Changes.Select(change =>
        {
            var step = StepOf(fields.First(field => field.Key == change.Key));
            return results.TryGetValue(step, out var result)
                ? new EditOutcome(change.Key, change.Label, result.Saved, result.Message)
                : new EditOutcome(change.Key, change.Label, false, "The script did not report a result for this setting.");
        }).ToList();
    }

    /// <summary>The settings a new read of the object does not show as wanted. Empty when all were saved.</summary>
    public static IReadOnlyList<EditChange> NotAsWanted(EditAssessment assessment, EditObjectState after)
        => NotAsWanted(assessment, after, null);

    /// <summary>
    /// The settings a new read of the object does not show as wanted. A target can be given as a
    /// sign-in name, and Teams stores and returns the object ID of that account. The save script
    /// reports the ID it resolved (<see cref="TargetMarker"/>), so a target counts as shown when the
    /// tenant holds that ID. Without the script output only an equal text counts.
    /// </summary>
    public static IReadOnlyList<EditChange> NotAsWanted(EditAssessment assessment, EditObjectState after, string? scriptOutput)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(after);
        var resolved = ResolvedTargets(scriptOutput);
        return assessment.Changes
            .Where(change =>
            {
                if (!after.Values.TryGetValue(change.Key, out var now))
                {
                    return true;
                }

                if (string.Equals(now, change.New, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return !(change.Key.EndsWith("Target", StringComparison.Ordinal)
                         && resolved.TryGetValue(change.New.Trim(), out var id)
                         && string.Equals(now, id, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
    }

    /// <summary>The sign-in names the save script resolved, each with the object ID it found.</summary>
    public static IReadOnlyDictionary<string, string> ResolvedTargets(string? scriptOutput)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (scriptOutput ?? string.Empty).Split('\n').Select(line => line.Trim()))
        {
            if (!line.StartsWith(TargetMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.LastIndexOf('|');
            if (separator > TargetMarker.Length && separator < line.Length - 1)
            {
                resolved[line[TargetMarker.Length..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return resolved;
    }

    private static string StepOf(EditField field)
        => field.SentWith is null ? field.Key : field.Type == EditFieldType.Choice ? field.Key : field.SentWith;

    private static string Normalize(EditField field, string value)
    {
        var text = value.Trim();
        try
        {
            return field.Type switch
            {
                EditFieldType.Bool => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false",
                EditFieldType.Hours => BusinessHours.Format(BusinessHours.Parse(text)),
                EditFieldType.Choice => field.Choices.FirstOrDefault(choice => string.Equals(choice, text, StringComparison.OrdinalIgnoreCase)) ?? text,
                _ => text
            };
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private static string Validate(EditField field, string value)
    {
        var text = value.Trim();
        switch (field.Type)
        {
            case EditFieldType.Choice:
                return field.Choices.FirstOrDefault(choice => string.Equals(choice, text, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"'{value}' is not one of {string.Join(", ", field.Choices)}.");
            case EditFieldType.Number:
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < field.Minimum || number > field.Maximum)
                {
                    throw new ArgumentException($"'{value}' is not a whole number from {field.Minimum} to {field.Maximum}.");
                }

                return number.ToString(CultureInfo.InvariantCulture);
            case EditFieldType.Bool:
                return text.ToLowerInvariant() switch
                {
                    "true" or "yes" or "on" => "true",
                    "false" or "no" or "off" => "false",
                    _ => throw new ArgumentException($"'{value}' is not true or false.")
                };
            case EditFieldType.Hours:
                return BusinessHours.Format(BusinessHours.Parse(text));
            default:
                if (field.Key == "agentGroups")
                {
                    var ids = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (ids.Any(id => !Guid.TryParse(id, out _)))
                    {
                        throw new ArgumentException("Give the object IDs of the groups, separated by commas.");
                    }

                    return string.Join(",", ids.Select(id => Guid.Parse(id).ToString("D")));
                }

                if (field.Key is "language" or "timeZone" && text.Length == 0)
                {
                    throw new ArgumentException("A value is needed.");
                }

                return text;
        }
    }

    private static string Clock(TimeSpan time)
        => time == TimeSpan.FromHours(24) ? "1.00:00" : time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private sealed class RawState
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public Dictionary<string, string?> Values { get; init; } = new();

        public Dictionary<string, string> Locked { get; init; } = new();

        public List<string> Notes { get; init; } = new();

        public Dictionary<string, string?> Scope { get; init; } = new();
    }

    /// <summary>Opening hours as text: one entry per day, each with its time ranges.</summary>
    public static class BusinessHours
    {
        public static IReadOnlyDictionary<string, IReadOnlyList<(TimeSpan Start, TimeSpan End)>> Parse(string? text)
        {
            var result = Days.ToDictionary(day => day, _ => (IReadOnlyList<(TimeSpan, TimeSpan)>)Array.Empty<(TimeSpan, TimeSpan)>(), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (text ?? string.Empty).Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var space = entry.IndexOf(' ', StringComparison.Ordinal);
                var dayText = space < 0 ? entry : entry[..space];
                var day = Days.FirstOrDefault(name => dayText.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"'{entry}' does not start with a day (Mon, Tue, Wed, Thu, Fri, Sat, Sun).");
                var rest = space < 0 ? string.Empty : entry[(space + 1)..].Trim();
                if (rest.Length == 0 || rest.Equals("closed", StringComparison.OrdinalIgnoreCase))
                {
                    result[day] = Array.Empty<(TimeSpan, TimeSpan)>();
                    continue;
                }

                var ranges = new List<(TimeSpan Start, TimeSpan End)>();
                foreach (var rangeText in rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = rangeText.Split('-', StringSplitOptions.TrimEntries);
                    if (parts.Length != 2 || !TryTime(parts[0], out var start) || !TryTime(parts[1], out var end))
                    {
                        throw new ArgumentException($"'{rangeText}' is not a time range like 08:00-17:00.");
                    }

                    if (end <= start)
                    {
                        throw new ArgumentException($"'{rangeText}' ends before it starts.");
                    }

                    if (start.Minutes % 15 != 0 || end.Minutes % 15 != 0)
                    {
                        throw new ArgumentException($"'{rangeText}': Teams takes times in steps of 15 minutes.");
                    }

                    ranges.Add((start, end));
                }

                ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
                for (var index = 1; index < ranges.Count; index++)
                {
                    if (ranges[index].Start < ranges[index - 1].End)
                    {
                        throw new ArgumentException($"The time ranges of {day} overlap.");
                    }
                }

                result[day] = ranges;
            }

            return result;
        }

        public static string Format(IReadOnlyDictionary<string, IReadOnlyList<(TimeSpan Start, TimeSpan End)>> hours)
            => string.Join("; ", Days.Select(day =>
                day + " " + (hours.TryGetValue(day, out var ranges) && ranges.Count > 0
                    ? string.Join(",", ranges.Select(range => Show(range.Start) + "-" + Show(range.End)))
                    : "closed")));

        private static string Show(TimeSpan time)
            => time >= TimeSpan.FromHours(24) ? "24:00" : time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        private static bool TryTime(string text, out TimeSpan time)
        {
            if (text == "24:00")
            {
                time = TimeSpan.FromHours(24);
                return true;
            }

            return TimeSpan.TryParseExact(text, new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromHours(24);
        }
    }

    private const string Helpers = """
        $pdGuid = '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'
        $pdResolve = {
            param($pdTarget)
            $pdText = ([string]$pdTarget).Trim()
            if (-not $pdText) { return $null }
            if ($pdText -match $pdGuid) { return $pdText }
            $pdUser = Get-CsOnlineUser -Identity $pdText -ErrorAction Stop
            if (-not $pdUser) { throw "The account '$pdText' was not found." }
            Write-Host "PDEDIT_TARGET: $pdText|$([string]$pdUser.Identity)"
            return [string]$pdUser.Identity
        }
        $pdEntity = {
            param($pdTarget)
            $pdUser = Get-CsOnlineUser -Identity ([string]$pdTarget).Trim() -ErrorAction Stop
            if (-not $pdUser) { throw "The account '$pdTarget' was not found." }
            Write-Host "PDEDIT_TARGET: $(([string]$pdTarget).Trim())|$([string]$pdUser.Identity)"
            $pdType = if ("$($pdUser.AccountType)" -eq 'ResourceAccount') { 'ApplicationEndpoint' } else { 'User' }
            return (New-CsAutoAttendantCallableEntity -Identity ([string]$pdUser.Identity) -Type $pdType -ErrorAction Stop)
        }
        $pdMenu = {
            param($pdFlow, $pdAction, $pdTarget)
            if ("$pdAction" -eq 'Transfer') {
                $pdOption = New-CsAutoAttendantMenuOption -Action TransferCallToTarget -DtmfResponse Automatic -CallTarget (& $pdEntity $pdTarget) -ErrorAction Stop
            }
            else {
                $pdOption = New-CsAutoAttendantMenuOption -Action DisconnectCall -DtmfResponse Automatic -ErrorAction Stop
            }
            $pdName = if ($pdFlow.Menu -and $pdFlow.Menu.Name) { [string]$pdFlow.Menu.Name } else { 'Menu' }
            $pdFlow.Menu = New-CsAutoAttendantMenu -Name $pdName -MenuOptions @($pdOption) -ErrorAction Stop
        }
        $pdGreeting = {
            param($pdFlow, $pdText)
            if ([string]$pdText) { $pdFlow.Greetings = @(New-CsAutoAttendantPrompt -TextToSpeechPrompt ([string]$pdText) -ErrorAction Stop) }
            else { $pdFlow.Greetings = @() }
        }
        $pdAfterHoursFlow = {
            param($pdAa)
            $pdLink = @($pdAa.CallHandlingAssociations | Where-Object { "$($_.Type)" -eq 'AfterHours' }) | Select-Object -First 1
            if (-not $pdLink) { throw 'The auto attendant has no after-hours call handling.' }
            if ("$($pdLink.CallFlowId)" -ne [string]$pdEdit.scopeCallFlowId) { throw 'The auto attendant has another after-hours call flow than when the change was checked. Nothing was saved to it.' }
            $pdFound = @($pdAa.CallFlows | Where-Object { "$($_.Id)" -eq "$($pdLink.CallFlowId)" }) | Select-Object -First 1
            if (-not $pdFound) { throw 'The after-hours call flow was not found.' }
            return $pdFound
        }
        """;

    private static readonly IReadOnlyDictionary<string, string> CallQueueSteps = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["routingMethod"] = "    Set-CsCallQueue -Identity $pdEditId -RoutingMethod ([string]$pdEdit.routingMethod) -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null",
        ["agentAlertTime"] = "    Set-CsCallQueue -Identity $pdEditId -AgentAlertTime ([int]$pdEdit.agentAlertTime) -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null",
        ["language"] = "    Set-CsCallQueue -Identity $pdEditId -LanguageId ([string]$pdEdit.language) -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null",
        ["greetingText"] =
            "    if ([string]$pdEdit.greetingText) { Set-CsCallQueue -Identity $pdEditId -WelcomeTextToSpeechPrompt ([string]$pdEdit.greetingText) -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null }\n" +
            "    else { Set-CsCallQueue -Identity $pdEditId -WelcomeTextToSpeechPrompt $null -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null }",
        ["allowOptOut"] = "    Set-CsCallQueue -Identity $pdEditId -AllowOptOut ([string]$pdEdit.allowOptOut -eq 'true') -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null",
        ["presenceBasedRouting"] = "    Set-CsCallQueue -Identity $pdEditId -PresenceBasedRouting ([string]$pdEdit.presenceBasedRouting -eq 'true') -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null",
        ["conferenceMode"] = "    Set-CsCallQueue -Identity $pdEditId -ConferenceMode ([string]$pdEdit.conferenceMode -eq 'true') -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null",
        ["agentGroups"] =
            "    $pdGroups = @(([string]$pdEdit.agentGroups).Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })\n" +
            "    Set-CsCallQueue -Identity $pdEditId -DistributionLists $pdGroups -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null",
        ["overflowThreshold"] = "    Set-CsCallQueue -Identity $pdEditId -OverflowThreshold ([int]$pdEdit.overflowThreshold) -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null",
        ["overflowAction"] =
            "    $pdTargetId = (& $pdResolve $pdEdit.overflowTarget)\n" +
            "    if ($pdTargetId -and ([string]$pdEdit.overflowAction) -ne 'DisconnectWithBusy') { Set-CsCallQueue -Identity $pdEditId -OverflowAction ([string]$pdEdit.overflowAction) -OverflowActionTarget $pdTargetId -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null }\n" +
            "    else { Set-CsCallQueue -Identity $pdEditId -OverflowAction ([string]$pdEdit.overflowAction) -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null }",
        ["timeoutThreshold"] = "    Set-CsCallQueue -Identity $pdEditId -TimeoutThreshold ([int]$pdEdit.timeoutThreshold) -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null",
        ["timeoutAction"] =
            "    $pdTargetId = (& $pdResolve $pdEdit.timeoutTarget)\n" +
            "    if ($pdTargetId -and ([string]$pdEdit.timeoutAction) -ne 'Disconnect') { Set-CsCallQueue -Identity $pdEditId -TimeoutAction ([string]$pdEdit.timeoutAction) -TimeoutActionTarget $pdTargetId -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null }\n" +
            "    else { Set-CsCallQueue -Identity $pdEditId -TimeoutAction ([string]$pdEdit.timeoutAction) -WarningAction SilentlyContinue -ErrorAction Stop | Out-Null }"
    };

    private const string GetAa = "    $pdAa = Get-CsAutoAttendant -Identity $pdEditId -ErrorAction Stop\n";
    private const string SetAa = "\n    Set-CsAutoAttendant -Instance $pdAa -ErrorAction Stop | Out-Null";

    private static readonly IReadOnlyDictionary<string, string> AutoAttendantSteps = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["language"] = GetAa + "    $pdAa.LanguageId = [string]$pdEdit.language" + SetAa,
        ["timeZone"] = GetAa + "    $pdAa.TimeZoneId = [string]$pdEdit.timeZone" + SetAa,
        ["greetingText"] = GetAa + "    & $pdGreeting $pdAa.DefaultCallFlow $pdEdit.greetingText" + SetAa,
        ["defaultAction"] = GetAa + "    & $pdMenu $pdAa.DefaultCallFlow $pdEdit.defaultAction $pdEdit.defaultTarget" + SetAa,
        ["afterHoursGreetingText"] = GetAa + "    & $pdGreeting (& $pdAfterHoursFlow $pdAa) $pdEdit.afterHoursGreetingText" + SetAa,
        ["afterHoursAction"] = GetAa + "    & $pdMenu (& $pdAfterHoursFlow $pdAa) $pdEdit.afterHoursAction $pdEdit.afterHoursTarget" + SetAa,
        ["businessHours"] =
            GetAa +
            "    $pdLink = @($pdAa.CallHandlingAssociations | Where-Object { \"$($_.Type)\" -eq 'AfterHours' }) | Select-Object -First 1\n" +
            "    if (-not $pdLink) { throw 'The auto attendant has no after-hours call handling.' }\n" +
            "    if (\"$($pdLink.ScheduleId)\" -ne [string]$pdEdit.scopeScheduleId) { throw 'The auto attendant uses another schedule than when the change was checked. Nothing was saved to it.' }\n" +
            "    $pdSchedule = Get-CsOnlineSchedule -Id ([string]$pdEdit.scopeScheduleId) -ErrorAction Stop\n" +
            "    $pdUsers = (@($pdSchedule.AssociatedConfigurationIds | Where-Object { $_ } | ForEach-Object { [string]$_ } | Sort-Object) -join ',')\n" +
            "    if ($pdUsers -ne [string]$pdEdit.scopeScheduleUsers) { throw 'Other auto attendants use the schedule than when the change was checked. Nothing was saved to it.' }\n" +
            "    $pdHours = ConvertFrom-Json ([string]$pdEdit.businessHoursJson)\n" +
            "    $pdDays = [ordered]@{ Mon = 'MondayHours'; Tue = 'TuesdayHours'; Wed = 'WednesdayHours'; Thu = 'ThursdayHours'; Fri = 'FridayHours'; Sat = 'SaturdayHours'; Sun = 'SundayHours' }\n" +
            "    foreach ($pdDay in $pdDays.Keys) {\n" +
            "        $pdRanges = @(@($pdHours.$pdDay) | Where-Object { $_ } | ForEach-Object { New-CsOnlineTimeRange -Start ([string]$_[0]) -End ([string]$_[1]) -ErrorAction Stop })\n" +
            "        $pdSchedule.WeeklyRecurrentSchedule.($pdDays[$pdDay]) = $pdRanges\n" +
            "    }\n" +
            "    Set-CsOnlineSchedule -Instance $pdSchedule -ErrorAction Stop | Out-Null"
    };

    private const string ReadCommon = """
        try {
            $pdRequest = ConvertFrom-Json ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('__REQUEST__')))
            $pdValue = [string]$pdRequest.value
        """;

    private const string ReadCallQueueTemplate = ReadCommon + """

            if ($pdRequest.isId) { $pdFound = @(Get-CsCallQueue -Identity $pdValue -WarningAction SilentlyContinue -ErrorAction Stop) }
            else { $pdFound = @(Get-CsCallQueue -NameFilter $pdValue -WarningAction SilentlyContinue -ErrorAction Stop | Where-Object { $_ -and $_.Name -eq $pdValue }) }
            if ($pdFound.Count -eq 0) { throw "No call queue '$pdValue' was found." }
            if ($pdFound.Count -gt 1) { throw "$($pdFound.Count) call queues are named '$pdValue'. Give the ID of the one you mean." }
            $pdQ = $pdFound[0]
            $pdValues = [ordered]@{
                routingMethod = [string]$pdQ.RoutingMethod
                agentAlertTime = [string]$pdQ.AgentAlertTime
                language = [string]$pdQ.LanguageId
                greetingText = [string]$pdQ.WelcomeTextToSpeechPrompt
                allowOptOut = [string]([bool]$pdQ.AllowOptOut)
                presenceBasedRouting = [string]([bool]$pdQ.PresenceBasedRouting)
                conferenceMode = [string]([bool]$pdQ.ConferenceMode)
                agentGroups = (@($pdQ.DistributionLists | Where-Object { $_ } | ForEach-Object { [string]$_ }) -join ',')
                overflowThreshold = [string]$pdQ.OverflowThreshold
                overflowAction = [string]$pdQ.OverflowAction
                overflowTarget = if ($pdQ.OverflowActionTarget) { [string]$pdQ.OverflowActionTarget.Id } else { '' }
                timeoutThreshold = [string]$pdQ.TimeoutThreshold
                timeoutAction = [string]$pdQ.TimeoutAction
                timeoutTarget = if ($pdQ.TimeoutActionTarget) { [string]$pdQ.TimeoutActionTarget.Id } else { '' }
            }
            $pdLocked = [ordered]@{}
            $pdNotes = @()
            if ($pdQ.WelcomeMusicAudioFileId) {
                $pdLocked['greetingText'] = 'The greeting is an audio file. Replace it in the Teams admin center.'
            }
            $pdDocument = [ordered]@{ id = [string]$pdQ.Identity; name = [string]$pdQ.Name; values = $pdValues; locked = $pdLocked; notes = @($pdNotes) }
            Write-Host ("PDEDIT_JSON: " + (ConvertTo-Json -InputObject $pdDocument -Depth 6 -Compress))
            Write-Host "SUCCESS: Call queue read"
        }
        catch {
            Write-Host "ERROR: $_"
        }
        """;

    private const string ReadAutoAttendantTemplate = ReadCommon + """

            if ($pdRequest.isId) { $pdFound = @(Get-CsAutoAttendant -Identity $pdValue -ErrorAction Stop) }
            else { $pdFound = @(Get-CsAutoAttendant -NameFilter $pdValue -ErrorAction Stop | Where-Object { $_ -and $_.Name -eq $pdValue }) }
            if ($pdFound.Count -eq 0) { throw "No auto attendant '$pdValue' was found." }
            if ($pdFound.Count -gt 1) { throw "$($pdFound.Count) auto attendants are named '$pdValue'. Give the ID of the one you mean." }
            $pdA = $pdFound[0]
            $pdLocked = [ordered]@{}
            $pdNotes = @()
            $pdScope = [ordered]@{}
            $pdValues = [ordered]@{ language = [string]$pdA.LanguageId; timeZone = [string]$pdA.TimeZoneId }
            $pdFlowValues = {
                param($pdFlow, $pdGreetingKey, $pdActionKey, $pdTargetKey, $pdWhere)
                $pdPrompts = @($pdFlow.Greetings | Where-Object { $_ })
                if (@($pdPrompts | Where-Object { $_.AudioFilePrompt }).Count -gt 0) {
                    $pdLocked[$pdGreetingKey] = "The greeting $pdWhere is an audio file. Replace it in the Teams admin center."
                }
                $pdValues[$pdGreetingKey] = if ($pdPrompts.Count -gt 0) { [string]$pdPrompts[0].TextToSpeechPrompt } else { '' }
                $pdOptions = @($pdFlow.Menu.MenuOptions | Where-Object { $_ })
                if ($pdOptions.Count -eq 1 -and "$($pdOptions[0].DtmfResponse)" -eq 'Automatic') {
                    if ("$($pdOptions[0].Action)" -eq 'TransferCallToTarget') {
                        $pdValues[$pdActionKey] = 'Transfer'
                        $pdValues[$pdTargetKey] = [string]$pdOptions[0].CallTarget.Id
                    }
                    elseif ("$($pdOptions[0].Action)" -eq 'DisconnectCall') {
                        $pdValues[$pdActionKey] = 'Disconnect'
                        $pdValues[$pdTargetKey] = ''
                    }
                    else {
                        $pdLocked[$pdActionKey] = "The action $pdWhere is '$($pdOptions[0].Action)', which is not changed here."
                        $pdLocked[$pdTargetKey] = $pdLocked[$pdActionKey]
                    }
                }
                else {
                    $pdLocked[$pdActionKey] = "The call flow $pdWhere has a menu with keys. A menu is changed in the Teams admin center."
                    $pdLocked[$pdTargetKey] = $pdLocked[$pdActionKey]
                }
            }
            & $pdFlowValues $pdA.DefaultCallFlow 'greetingText' 'defaultAction' 'defaultTarget' 'in opening hours'
            $pdLink = @($pdA.CallHandlingAssociations | Where-Object { "$($_.Type)" -eq 'AfterHours' }) | Select-Object -First 1
            $pdAfter = if ($pdLink) { @($pdA.CallFlows | Where-Object { "$($_.Id)" -eq "$($pdLink.CallFlowId)" }) | Select-Object -First 1 } else { $null }
            if ($pdAfter) {
                & $pdFlowValues $pdAfter 'afterHoursGreetingText' 'afterHoursAction' 'afterHoursTarget' 'after hours'
                $pdSchedule = Get-CsOnlineSchedule -Id ([string]$pdLink.ScheduleId) -ErrorAction Stop
                $pdScope['scheduleId'] = [string]$pdLink.ScheduleId
                $pdScope['callFlowId'] = [string]$pdLink.CallFlowId
                $pdScope['scheduleUsers'] = (@($pdSchedule.AssociatedConfigurationIds | Where-Object { $_ } | ForEach-Object { [string]$_ } | Sort-Object) -join ',')
                $pdDays = [ordered]@{ Mon = 'MondayHours'; Tue = 'TuesdayHours'; Wed = 'WednesdayHours'; Thu = 'ThursdayHours'; Fri = 'FridayHours'; Sat = 'SaturdayHours'; Sun = 'SundayHours' }
                $pdParts = @()
                foreach ($pdDay in $pdDays.Keys) {
                    $pdRanges = @($pdSchedule.WeeklyRecurrentSchedule.($pdDays[$pdDay]) | Where-Object { $_ } | ForEach-Object {
                        $pdStart = [TimeSpan]$_.Start
                        $pdEnd = [TimeSpan]$_.End
                        $pdEndText = if ($pdEnd.TotalHours -ge 24) { '24:00' } else { '{0:00}:{1:00}' -f $pdEnd.Hours, $pdEnd.Minutes }
                        ('{0:00}:{1:00}' -f $pdStart.Hours, $pdStart.Minutes) + '-' + $pdEndText
                    })
                    $pdParts += ($pdDay + ' ' + $(if ($pdRanges.Count -gt 0) { $pdRanges -join ',' } else { 'closed' }))
                }
                $pdValues['businessHours'] = ($pdParts -join '; ')
                if (@($pdSchedule.AssociatedConfigurationIds | Where-Object { $_ }).Count -gt 1) {
                    $pdNotes += "The schedule of the opening hours ('$($pdSchedule.Name)') is shared with other auto attendants."
                }
            }
            else {
                foreach ($pdKey in @('businessHours', 'afterHoursGreetingText', 'afterHoursAction', 'afterHoursTarget')) {
                    $pdLocked[$pdKey] = 'The auto attendant has no after-hours call handling.'
                }
            }
            $pdDocument = [ordered]@{ id = [string]$pdA.Identity; name = [string]$pdA.Name; values = $pdValues; locked = $pdLocked; notes = @($pdNotes); scope = $pdScope }
            Write-Host ("PDEDIT_JSON: " + (ConvertTo-Json -InputObject $pdDocument -Depth 6 -Compress))
            Write-Host "SUCCESS: Auto attendant read"
        }
        catch {
            Write-Host "ERROR: $_"
        }
        """;
}
