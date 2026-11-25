using System.Text.Json;

namespace PhoneDesk.Operations;

/// <summary>What the tenant holds about holiday call handling: auto attendants with their call flows, and the schedules.</summary>
public sealed class HolidayCallHandlingState
{
    public IReadOnlyList<AutoAttendantCallHandling> AutoAttendants { get; init; } = Array.Empty<AutoAttendantCallHandling>();

    public IReadOnlyList<ScheduleInfo> Schedules { get; init; } = Array.Empty<ScheduleInfo>();

    /// <summary>Authorized users of an auto attendant that no longer exist in the directory.</summary>
    public IReadOnlyList<AuthorizedUserProblem> MissingAuthorizedUsers { get; init; } = Array.Empty<AuthorizedUserProblem>();

    /// <summary>Authorized users that could not be checked, with the error text.</summary>
    public IReadOnlyList<AuthorizedUserProblem> UncheckedAuthorizedUsers { get; init; } = Array.Empty<AuthorizedUserProblem>();

    public ScheduleInfo? FindSchedule(string? id)
        => Schedules.FirstOrDefault(schedule => HolidayCallHandling.SameId(schedule.Id, id));

    public AutoAttendantCallHandling? FindAutoAttendant(string? id)
        => AutoAttendants.FirstOrDefault(attendant => HolidayCallHandling.SameId(attendant.Id, id));
}

public sealed class AutoAttendantCallHandling
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string> AuthorizedUsers { get; init; } = Array.Empty<string>();

    /// <summary>The call flows beside the default one. Teams limits their number per auto attendant.</summary>
    public IReadOnlyList<CallFlowInfo> CallFlows { get; init; } = Array.Empty<CallFlowInfo>();

    public IReadOnlyList<CallHandlingLink> Associations { get; init; } = Array.Empty<CallHandlingLink>();

    public IEnumerable<CallHandlingLink> HolidayLinks
        => Associations.Where(link => string.Equals(link.Type, "Holiday", StringComparison.OrdinalIgnoreCase));

    public CallHandlingLink? HolidayLinkFor(string? scheduleId)
        => HolidayLinks.FirstOrDefault(link => HolidayCallHandling.SameId(link.ScheduleId, scheduleId));

    public CallFlowInfo? FindCallFlow(string? id)
        => CallFlows.FirstOrDefault(flow => HolidayCallHandling.SameId(flow.Id, id));
}

/// <summary>Connects a schedule with the call flow that handles calls while the schedule applies.</summary>
public sealed class CallHandlingLink
{
    public string Type { get; init; } = string.Empty;

    public string ScheduleId { get; init; } = string.Empty;

    public string CallFlowId { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public int Priority { get; init; }
}

public sealed class CallFlowInfo
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<PromptInfo> Greetings { get; init; } = Array.Empty<PromptInfo>();

    public string? MenuName { get; init; }

    public IReadOnlyList<PromptInfo> MenuPrompts { get; init; } = Array.Empty<PromptInfo>();

    public IReadOnlyList<MenuOptionInfo> Options { get; init; } = Array.Empty<MenuOptionInfo>();

    public bool ForceListenMenu { get; init; }

    public bool DialByName { get; init; }
}

/// <summary>A greeting or menu prompt. <see cref="Type"/> is None, TextToSpeech or AudioFile.</summary>
public sealed class PromptInfo
{
    public string Type { get; init; } = string.Empty;

    public string? Text { get; init; }

    public AudioFileInfo? AudioFile { get; init; }
}

public sealed class AudioFileInfo
{
    public string Id { get; init; } = string.Empty;

    public string? FileName { get; init; }
}

public sealed class MenuOptionInfo
{
    public string Action { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public IReadOnlyList<string> VoiceResponses { get; init; } = Array.Empty<string>();

    public CallTargetInfo? Target { get; init; }

    public PromptInfo? Prompt { get; init; }
}

public sealed class CallTargetInfo
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public bool? EnableTranscription { get; init; }

    public bool? SuppressSystemPrompt { get; init; }

    public int CallPriority { get; init; }
}

public sealed class ScheduleInfo
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Fixed for a holiday schedule, WeeklyRecurrence for opening hours.</summary>
    public string Type { get; init; } = string.Empty;

    public IReadOnlyList<ScheduleWindow> Windows { get; init; } = Array.Empty<ScheduleWindow>();

    /// <summary>The IDs of the auto attendants that use the schedule.</summary>
    public IReadOnlyList<string> UsedBy { get; init; } = Array.Empty<string>();

    public bool IsFixed => string.Equals(Type, "Fixed", StringComparison.OrdinalIgnoreCase);

    public DateTime? LastEnd => Windows.Count == 0 ? null : Windows.Max(window => window.End);

    /// <summary>True when every date range of the schedule has ended.</summary>
    public bool IsExpired(DateTime now) => LastEnd is { } end && end <= now;
}

/// <summary>A date range of a schedule in the time zone of the auto attendant. The end is exclusive.</summary>
public sealed record ScheduleWindow(DateTime Start, DateTime End)
{
    public bool Overlaps(ScheduleWindow other) => Start < other.End && other.Start < End;
}

public sealed record AuthorizedUserProblem(string AutoAttendantId, string UserId, string? Error = null);

/// <summary>Two holiday schedules of one auto attendant that apply at the same time.</summary>
public sealed record HolidayOverlap(
    ScheduleInfo Schedule,
    ScheduleWindow Window,
    ScheduleInfo OtherSchedule,
    ScheduleWindow OtherWindow,
    CallFlowInfo? OtherCallFlow)
{
    /// <summary>Teams refuses two holidays of one auto attendant that start at the same time.</summary>
    public bool SameStart => Window.Start == OtherWindow.Start;
}

/// <summary>A reason why the copy cannot be saved. <see cref="Code"/> is stable; the message is for the operator.</summary>
public sealed record CopyBlocker(string Code, string Message);

public sealed record CopyRequest(
    string AutoAttendantId,
    string SourceScheduleId,
    string TargetScheduleId,
    string? ReplaceScheduleId = null,
    bool AcceptOverlaps = false,
    bool RemoveMissingAuthorizedUsers = false);

/// <summary>The result of checking a copy against the tenant before anything is saved.</summary>
public sealed class CopyAssessment
{
    public AutoAttendantCallHandling? AutoAttendant { get; init; }

    public ScheduleInfo? SourceSchedule { get; init; }

    public CallFlowInfo? SourceCallFlow { get; init; }

    public ScheduleInfo? TargetSchedule { get; init; }

    public ScheduleInfo? ReplacedSchedule { get; init; }

    public CallFlowInfo? ReplacedCallFlow { get; init; }

    /// <summary>The target schedule is attached to the auto attendant already: nothing is left to save.</summary>
    public bool AlreadyAttached { get; init; }

    public IReadOnlyList<HolidayOverlap> Overlaps { get; init; } = Array.Empty<HolidayOverlap>();

    public IReadOnlyList<CopyBlocker> Blockers { get; init; } = Array.Empty<CopyBlocker>();

    /// <summary>Holiday schedules of the auto attendant whose dates have all passed, oldest first.</summary>
    public IReadOnlyList<ScheduleInfo> ExpiredSchedules { get; init; } = Array.Empty<ScheduleInfo>();

    /// <summary>The missing authorized users the save removes from the auto attendant.</summary>
    public IReadOnlyList<string> AuthorizedUsersToRemove { get; init; } = Array.Empty<string>();

    public bool CanSave => Blockers.Count == 0 && !AlreadyAttached;
}

/// <summary>
/// Reads and copies the call handling of holiday schedules. One schedule can have a different call
/// flow on every auto attendant that uses it, so every operation works on one auto attendant.
/// All objects are addressed by ID: names are not unique and never go into a script.
/// </summary>
public static class HolidayCallHandling
{
    /// <summary>
    /// The number of call flows an auto attendant can have beside its default call flow. Teams
    /// refuses to save an auto attendant with more. The value was found in production tenants.
    /// </summary>
    public const int AdditionalCallFlowLimit = 21;

    public const string JsonMarker = "PDHOLIDAY_JSON: ";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static bool SameId(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && string.Equals(left.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>An object ID in a script must be a GUID: nothing else is ever written into script text.</summary>
    public static string RequireId(string? value, string what)
        => Guid.TryParse(value?.Trim(), out var id)
            ? id.ToString("D")
            : throw new ArgumentException($"'{value}' is not the ID of {what}. Give the ID (a GUID), not the name.");

    /// <summary>
    /// Reads call flows, call handling associations and schedules. With no IDs every auto attendant
    /// is read; with <paramref name="includeAutoAttendants"/> false only the schedules are read.
    /// The script only reads.
    /// </summary>
    public static string GetReadCommand(
        IEnumerable<string>? autoAttendantIds = null,
        bool includeAutoAttendants = true,
        bool checkAuthorizedUsers = false)
    {
        var ids = (autoAttendantIds ?? Array.Empty<string>()).Select(id => "'" + RequireId(id, "an auto attendant") + "'").ToList();
        return ReadTemplate
            .Replace("__IDS__", string.Join(", ", ids), StringComparison.Ordinal)
            .Replace("__INCLUDE_ATTENDANTS__", includeAutoAttendants ? "$true" : "$false", StringComparison.Ordinal)
            .Replace("__CHECK_USERS__", checkAuthorizedUsers ? "$true" : "$false", StringComparison.Ordinal);
    }

    /// <summary>Reads the state the read script printed. Throws when the script reported an error.</summary>
    public static HolidayCallHandlingState ParseState(string? output)
    {
        var lines = (output ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var json = lines.LastOrDefault(line => line.StartsWith(JsonMarker, StringComparison.Ordinal));
        if (json is null)
        {
            var error = lines.FirstOrDefault(line => line.StartsWith("ERROR:", StringComparison.Ordinal));
            throw new InvalidDataException(error ?? "The holiday call handling could not be read: the tenant returned no data.");
        }

        return JsonSerializer.Deserialize<HolidayCallHandlingState>(json[JsonMarker.Length..], JsonOptions)
               ?? throw new InvalidDataException("The holiday call handling could not be read: the data is empty.");
    }

    /// <summary>The holiday schedules of one auto attendant that apply at the same time, each pair once.</summary>
    public static IReadOnlyList<HolidayOverlap> FindOverlaps(HolidayCallHandlingState state, AutoAttendantCallHandling attendant)
    {
        var schedules = attendant.HolidayLinks
            .Select(link => (Schedule: state.FindSchedule(link.ScheduleId), Flow: attendant.FindCallFlow(link.CallFlowId)))
            .Where(item => item.Schedule is not null)
            .ToList();
        var overlaps = new List<HolidayOverlap>();
        for (var first = 0; first < schedules.Count; first++)
        {
            for (var second = first + 1; second < schedules.Count; second++)
            {
                overlaps.AddRange(Overlaps(schedules[first].Schedule!, schedules[second].Schedule!, schedules[second].Flow));
            }
        }

        return overlaps;
    }

    /// <summary>Checks a copy against the state of the tenant. Nothing is changed.</summary>
    public static CopyAssessment Assess(HolidayCallHandlingState state, CopyRequest request, DateTime now)
    {
        var blockers = new List<CopyBlocker>();
        var attendant = state.FindAutoAttendant(request.AutoAttendantId);
        var target = state.FindSchedule(request.TargetScheduleId);
        var source = state.FindSchedule(request.SourceScheduleId);
        if (attendant is null)
        {
            blockers.Add(new("autoAttendantNotFound", $"No auto attendant has the ID {request.AutoAttendantId}."));
            return new CopyAssessment { SourceSchedule = source, TargetSchedule = target, Blockers = blockers };
        }

        if (SameId(request.SourceScheduleId, request.TargetScheduleId))
        {
            blockers.Add(new("sameSchedule", "The source schedule and the target schedule are the same."));
        }

        var sourceLink = attendant.HolidayLinkFor(request.SourceScheduleId);
        var sourceFlow = sourceLink is null ? null : attendant.FindCallFlow(sourceLink.CallFlowId);
        if (sourceLink is null)
        {
            blockers.Add(new("sourceNotAttached",
                $"Schedule {request.SourceScheduleId} is not attached to auto attendant '{attendant.Name}' as a holiday, so it has no call handling to copy there."));
        }
        else if (sourceFlow is null)
        {
            blockers.Add(new("sourceCallFlowMissing",
                $"The call flow {sourceLink.CallFlowId} of the source schedule was not found on auto attendant '{attendant.Name}'."));
        }

        if (target is null)
        {
            blockers.Add(new("targetNotFound", $"No schedule has the ID {request.TargetScheduleId}. Create the schedule first."));
        }
        else if (!target.IsFixed || target.Windows.Count == 0)
        {
            blockers.Add(new("targetNotAHolidaySchedule", $"Schedule '{target.Name}' has no fixed date ranges, so it is not a holiday schedule."));
        }

        var alreadyAttached = attendant.HolidayLinkFor(request.TargetScheduleId) is not null;
        var expired = ExpiredSchedules(state, attendant, now, request.SourceScheduleId, request.TargetScheduleId);

        ScheduleInfo? replaced = null;
        CallFlowInfo? replacedFlow = null;
        if (!string.IsNullOrWhiteSpace(request.ReplaceScheduleId))
        {
            var replacedLink = attendant.HolidayLinkFor(request.ReplaceScheduleId);
            replaced = state.FindSchedule(request.ReplaceScheduleId);
            replacedFlow = replacedLink is null ? null : attendant.FindCallFlow(replacedLink.CallFlowId);
            if (SameId(request.ReplaceScheduleId, request.SourceScheduleId) || SameId(request.ReplaceScheduleId, request.TargetScheduleId))
            {
                blockers.Add(new("replaceIsSourceOrTarget", "The schedule to replace must be another schedule than the source and the target."));
            }
            else if (replacedLink is null || replaced is null)
            {
                blockers.Add(new("replaceNotAttached",
                    $"Schedule {request.ReplaceScheduleId} is not attached to auto attendant '{attendant.Name}' as a holiday."));
            }
            else if (!replaced.IsExpired(now))
            {
                blockers.Add(new("replaceNotExpired",
                    $"Schedule '{replaced.Name}' still has a date that has not passed (last end {replaced.LastEnd:yyyy-MM-dd HH:mm}). Only an expired schedule can be replaced."));
            }
        }

        var overlaps = target is null || alreadyAttached
            ? new List<HolidayOverlap>()
            : SaveChecks(state, attendant, target, request.ReplaceScheduleId, replacedFlow is not null, request.AcceptOverlaps, expired, blockers);
        var missing = MissingAuthorizedUsers(state, attendant);
        if (missing.Count > 0 && !request.RemoveMissingAuthorizedUsers && !alreadyAttached)
        {
            blockers.Add(new("missingAuthorizedUsers",
                $"Auto attendant '{attendant.Name}' lists authorized users that no longer exist ({string.Join(", ", missing)}). Teams refuses to save it. " +
                "Set removeMissingAuthorizedUsers=true to take these entries off in the same save."));
        }

        return new CopyAssessment
        {
            AutoAttendant = attendant,
            SourceSchedule = source,
            SourceCallFlow = sourceFlow,
            TargetSchedule = target,
            ReplacedSchedule = replaced,
            ReplacedCallFlow = replacedFlow,
            AlreadyAttached = alreadyAttached,
            Overlaps = overlaps,
            Blockers = blockers,
            ExpiredSchedules = expired,
            AuthorizedUsersToRemove = request.RemoveMissingAuthorizedUsers ? missing : new List<string>()
        };
    }

    /// <summary>
    /// Checks whether a schedule can be attached to an auto attendant as a new holiday: overlaps,
    /// the call flow limit and authorized users that no longer exist. Nothing is changed.
    /// </summary>
    public static CopyAssessment AssessAttach(
        HolidayCallHandlingState state, string autoAttendantId, string scheduleId, bool acceptOverlaps, DateTime now)
    {
        var blockers = new List<CopyBlocker>();
        var attendant = state.FindAutoAttendant(autoAttendantId);
        var target = state.FindSchedule(scheduleId);
        if (attendant is null || target is null)
        {
            return new CopyAssessment { AutoAttendant = attendant, TargetSchedule = target };
        }

        var alreadyAttached = attendant.HolidayLinkFor(scheduleId) is not null;
        var expired = ExpiredSchedules(state, attendant, now, scheduleId);
        var overlaps = alreadyAttached
            ? new List<HolidayOverlap>()
            : SaveChecks(state, attendant, target, null, false, acceptOverlaps, expired, blockers);
        var missing = MissingAuthorizedUsers(state, attendant);
        if (missing.Count > 0 && !alreadyAttached)
        {
            blockers.Add(new("missingAuthorizedUsers",
                $"Auto attendant '{attendant.Name}' lists authorized users that no longer exist ({string.Join(", ", missing)}). Teams refuses to save it. " +
                "Take these entries off the auto attendant first."));
        }

        return new CopyAssessment
        {
            AutoAttendant = attendant,
            TargetSchedule = target,
            AlreadyAttached = alreadyAttached,
            Overlaps = overlaps,
            Blockers = blockers,
            ExpiredSchedules = expired
        };
    }

    private static List<ScheduleInfo> ExpiredSchedules(
        HolidayCallHandlingState state, AutoAttendantCallHandling attendant, DateTime now, params string?[] except)
        => attendant.HolidayLinks
            .Select(link => state.FindSchedule(link.ScheduleId))
            .Where(schedule => schedule is not null && schedule.IsExpired(now) && !except.Any(id => SameId(schedule.Id, id)))
            .Select(schedule => schedule!)
            .OrderBy(schedule => schedule.LastEnd)
            .ToList();

    private static List<string> MissingAuthorizedUsers(HolidayCallHandlingState state, AutoAttendantCallHandling attendant)
        => state.MissingAuthorizedUsers
            .Where(user => SameId(user.AutoAttendantId, attendant.Id))
            .Select(user => user.UserId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The checks on what Teams accepts when one more holiday is saved on an auto attendant.</summary>
    private static List<HolidayOverlap> SaveChecks(
        HolidayCallHandlingState state,
        AutoAttendantCallHandling attendant,
        ScheduleInfo target,
        string? replaceScheduleId,
        bool replacesCallFlow,
        bool acceptOverlaps,
        IReadOnlyList<ScheduleInfo> expired,
        List<CopyBlocker> blockers)
    {
        var overlaps = new List<HolidayOverlap>();
        foreach (var link in attendant.HolidayLinks)
        {
            var other = state.FindSchedule(link.ScheduleId);
            if (other is null || SameId(other.Id, target.Id) || SameId(other.Id, replaceScheduleId))
            {
                continue;
            }

            overlaps.AddRange(Overlaps(target, other, attendant.FindCallFlow(link.CallFlowId)));
        }

        if (overlaps.Any(overlap => overlap.SameStart))
        {
            blockers.Add(new("sameStartTime",
                $"A holiday that is attached to auto attendant '{attendant.Name}' already starts at the same time as a date of schedule '{target.Name}'. " +
                "Teams refuses that. Change the dates of one schedule first."));
        }
        else if (overlaps.Count > 0 && !acceptOverlaps)
        {
            blockers.Add(new("overlap",
                $"Schedule '{target.Name}' overlaps a holiday that is attached to auto attendant '{attendant.Name}' already, so two call handlings apply at the same time. " +
                "Change the dates, or set acceptOverlaps=true when the user accepts the overlap."));
        }

        var flowsAfterSave = attendant.CallFlows.Count + 1 - (replacesCallFlow ? 1 : 0);
        if (flowsAfterSave > AdditionalCallFlowLimit)
        {
            blockers.Add(new("callFlowLimit",
                $"Auto attendant '{attendant.Name}' has {attendant.CallFlows.Count} call flows beside the default one; Teams allows {AdditionalCallFlowLimit}. " +
                (expired.Count > 0
                    ? "copy_holiday_call_handling can take an expired schedule off this auto attendant in the same save (replaceScheduleId); the schedule itself stays."
                    : "No attached holiday schedule has expired, so a call flow must be removed by hand first.")));
        }

        return overlaps;
    }

    /// <summary>
    /// The differences between two call flows as a caller hears them: greetings, menu options,
    /// targets and voicemail options. Empty when both handle a call the same way. Texts are
    /// compared character by character, so a changed apostrophe is a difference.
    /// </summary>
    public static IReadOnlyList<string> Differences(CallFlowInfo expected, CallFlowInfo actual)
    {
        var differences = new List<string>();
        ComparePrompts("greeting", expected.Greetings, actual.Greetings, differences);
        ComparePrompts("menu prompt", expected.MenuPrompts, actual.MenuPrompts, differences);
        if (expected.Options.Count != actual.Options.Count)
        {
            differences.Add($"menu options: expected {expected.Options.Count}, found {actual.Options.Count}");
        }

        for (var index = 0; index < Math.Min(expected.Options.Count, actual.Options.Count); index++)
        {
            var (left, right) = (expected.Options[index], actual.Options[index]);
            var label = $"menu option {index + 1}";
            Compare(label + " action", left.Action, right.Action, differences);
            Compare(label + " key", left.Key, right.Key, differences);
            Compare(label + " target", left.Target?.Id, right.Target?.Id, differences);
            Compare(label + " target type", left.Target?.Type, right.Target?.Type, differences);
            Compare(label + " transcription", left.Target?.EnableTranscription == true, right.Target?.EnableTranscription == true, differences);
            Compare(label + " system prompt suppression", left.Target?.SuppressSystemPrompt == true, right.Target?.SuppressSystemPrompt == true, differences);
            Compare(label + " call priority", left.Target?.CallPriority ?? 0, right.Target?.CallPriority ?? 0, differences);
            ComparePrompts(label + " prompt", Listed(left.Prompt), Listed(right.Prompt), differences);
        }

        Compare("force listen menu", expected.ForceListenMenu, actual.ForceListenMenu, differences);
        Compare("dial by name", expected.DialByName, actual.DialByName, differences);
        return differences;
    }

    /// <summary>One line that says what a caller gets: the greeting, then what happens to the call.</summary>
    public static string Describe(CallFlowInfo? flow)
    {
        if (flow is null)
        {
            return "unknown (the call flow was not found)";
        }

        var greetings = flow.Greetings.Where(IsSpoken).Select(DescribePrompt).ToList();
        var greeting = greetings.Count == 0 ? "no greeting" : "greeting: " + string.Join(" + ", greetings);
        var options = flow.Options.Count == 0
            ? "no menu option"
            : string.Join("; ", flow.Options.Select(option =>
                (string.Equals(option.Key, "Automatic", StringComparison.OrdinalIgnoreCase) ? string.Empty : $"key {option.Key}: ") +
                option.Action +
                (option.Target is null ? string.Empty : $" to {option.Target.Type} {option.Target.Id}")));
        return greeting + ", then " + options;
    }

    /// <summary>
    /// Copies the call flow that the source schedule has on one auto attendant to the target
    /// schedule on the same auto attendant, saves the auto attendant once and reads it again.
    /// The script decides nothing by name and changes nothing when the target is attached already.
    /// </summary>
    public static string GetCopyCommand(CopyRequest request, IReadOnlyList<string> authorizedUsersToRemove)
    {
        var replace = string.IsNullOrWhiteSpace(request.ReplaceScheduleId)
            ? string.Empty
            : ReplaceTemplate.Replace("__REPLACE__", RequireId(request.ReplaceScheduleId, "the schedule to replace"), StringComparison.Ordinal);
        var users = authorizedUsersToRemove.Count == 0
            ? string.Empty
            : UsersTemplate.Replace(
                "__USERS__",
                string.Join(", ", authorizedUsersToRemove.Select(id => "'" + RequireId(id, "an authorized user") + "'")),
                StringComparison.Ordinal);
        return CopyTemplate
            .Replace("__REPLACE_BLOCK__", replace, StringComparison.Ordinal)
            .Replace("__USERS_BLOCK__", users, StringComparison.Ordinal)
            .Replace("__AA__", RequireId(request.AutoAttendantId, "an auto attendant"), StringComparison.Ordinal)
            .Replace("__SOURCE__", RequireId(request.SourceScheduleId, "the source schedule"), StringComparison.Ordinal)
            .Replace("__TARGET__", RequireId(request.TargetScheduleId, "the target schedule"), StringComparison.Ordinal);
    }

    private static IEnumerable<HolidayOverlap> Overlaps(ScheduleInfo schedule, ScheduleInfo other, CallFlowInfo? otherFlow)
        => from window in schedule.Windows
           from otherWindow in other.Windows
           where window.Overlaps(otherWindow)
           select new HolidayOverlap(schedule, window, other, otherWindow, otherFlow);

    private static bool IsSpoken(PromptInfo prompt)
        => !string.IsNullOrEmpty(prompt.Text) || prompt.AudioFile is not null;

    private static string DescribePrompt(PromptInfo prompt)
        => prompt.AudioFile is not null
            ? $"audio file '{prompt.AudioFile.FileName}' ({prompt.AudioFile.Id})"
            : $"text \"{prompt.Text}\"";

    private static IReadOnlyList<PromptInfo> Listed(PromptInfo? prompt) => prompt is null ? Array.Empty<PromptInfo>() : new[] { prompt };

    private static void ComparePrompts(string what, IReadOnlyList<PromptInfo> expected, IReadOnlyList<PromptInfo> actual, List<string> differences)
    {
        var (left, right) = (expected.Where(IsSpoken).ToList(), actual.Where(IsSpoken).ToList());
        if (left.Count != right.Count)
        {
            differences.Add($"{what}: expected {left.Count}, found {right.Count}");
            return;
        }

        for (var index = 0; index < left.Count; index++)
        {
            Compare($"{what} text", left[index].Text ?? string.Empty, right[index].Text ?? string.Empty, differences);
            Compare($"{what} audio file", left[index].AudioFile?.Id, right[index].AudioFile?.Id, differences);
        }
    }

    private static void Compare<T>(string what, T expected, T actual, List<string> differences)
    {
        var same = expected is string || actual is string
            ? string.Equals(expected as string ?? string.Empty, actual as string ?? string.Empty, StringComparison.Ordinal)
            : EqualityComparer<T>.Default.Equals(expected, actual);
        if (!same)
        {
            differences.Add($"{what}: expected '{expected}', found '{actual}'");
        }
    }

    private const string ReadTemplate = """
        try {
            $pdIds = @(__IDS__)
            $pdAttendants = @()
            if (__INCLUDE_ATTENDANTS__) {
                if ($pdIds.Count -gt 0) {
                    foreach ($pdId in $pdIds) {
                        $pdAttendants += @(Get-CsAutoAttendant -Identity $pdId -ErrorAction Stop)
                    }
                }
                else {
                    $pdSkip = 0
                    do {
                        $pdPage = @(Get-CsAutoAttendant -First 100 -Skip $pdSkip -ErrorAction Stop)
                        $pdAttendants += $pdPage
                        $pdSkip += 100
                    } while ($pdPage.Count -eq 100)
                }
            }
            $pdSchedules = @(Get-CsOnlineSchedule -ErrorAction Stop)

            $pdPromptOf = {
                param($pdPrompt)
                if (-not $pdPrompt) { return $null }
                $pdAudio = $null
                if ($pdPrompt.AudioFilePrompt -and $pdPrompt.AudioFilePrompt.Id) {
                    $pdAudio = [ordered]@{ id = [string]$pdPrompt.AudioFilePrompt.Id; fileName = [string]$pdPrompt.AudioFilePrompt.FileName }
                }
                [ordered]@{ type = [string]$pdPrompt.ActiveType; text = $pdPrompt.TextToSpeechPrompt; audioFile = $pdAudio }
            }
            $pdFlowOf = {
                param($pdFlow)
                $pdOptions = @()
                $pdMenuPrompts = @()
                $pdMenuName = $null
                $pdDialByName = $false
                if ($pdFlow.Menu) {
                    $pdMenuName = [string]$pdFlow.Menu.Name
                    $pdDialByName = [bool]$pdFlow.Menu.DialByNameEnabled
                    foreach ($pdMenuPrompt in @($pdFlow.Menu.Prompts)) {
                        if ($pdMenuPrompt) { $pdMenuPrompts += @(& $pdPromptOf $pdMenuPrompt) }
                    }
                    foreach ($pdOption in @($pdFlow.Menu.MenuOptions)) {
                        if (-not $pdOption) { continue }
                        $pdTarget = $null
                        if ($pdOption.CallTarget -and $pdOption.CallTarget.Id) {
                            $pdTarget = [ordered]@{
                                id = [string]$pdOption.CallTarget.Id
                                type = [string]$pdOption.CallTarget.Type
                                enableTranscription = $pdOption.CallTarget.EnableTranscription
                                suppressSystemPrompt = $pdOption.CallTarget.EnableSharedVoicemailSystemPromptSuppression
                                callPriority = [int]$pdOption.CallTarget.CallPriority
                            }
                        }
                        $pdOptions += @([ordered]@{
                            action = [string]$pdOption.Action
                            key = [string]$pdOption.DtmfResponse
                            voiceResponses = @(@($pdOption.VoiceResponses) | Where-Object { $_ } | ForEach-Object { [string]$_ })
                            target = $pdTarget
                            prompt = (& $pdPromptOf $pdOption.Prompt)
                        })
                    }
                }
                $pdGreetings = @()
                foreach ($pdGreeting in @($pdFlow.Greetings)) {
                    if ($pdGreeting) { $pdGreetings += @(& $pdPromptOf $pdGreeting) }
                }
                [ordered]@{
                    id = [string]$pdFlow.Id
                    name = [string]$pdFlow.Name
                    greetings = @($pdGreetings)
                    menuName = $pdMenuName
                    menuPrompts = @($pdMenuPrompts)
                    options = @($pdOptions)
                    forceListenMenu = [bool]$pdFlow.ForceListenMenuEnabled
                    dialByName = $pdDialByName
                }
            }

            $pdMissing = @()
            $pdUnchecked = @()
            $pdAttendantRows = @()
            foreach ($pdAa in $pdAttendants) {
                if (-not $pdAa) { continue }
                $pdFlows = @()
                foreach ($pdFlow in @($pdAa.CallFlows)) {
                    if ($pdFlow) { $pdFlows += @(& $pdFlowOf $pdFlow) }
                }
                $pdLinks = @()
                foreach ($pdLink in @($pdAa.CallHandlingAssociations)) {
                    if (-not $pdLink) { continue }
                    $pdLinks += @([ordered]@{
                        type = [string]$pdLink.Type
                        scheduleId = [string]$pdLink.ScheduleId
                        callFlowId = [string]$pdLink.CallFlowId
                        enabled = [bool]$pdLink.Enabled
                        priority = [int]$pdLink.Priority
                    })
                }
                $pdUsers = @(@($pdAa.AuthorizedUsers) | Where-Object { $_ } | ForEach-Object { [string]$_ })
                if (__CHECK_USERS__) {
                    foreach ($pdUser in $pdUsers) {
                        try {
                            Get-MgUser -UserId $pdUser -Property Id -ErrorAction Stop | Out-Null
                        }
                        catch {
                            if ("$_" -match 'does not exist|ResourceNotFound') {
                                $pdMissing += @([ordered]@{ autoAttendantId = [string]$pdAa.Identity; userId = $pdUser })
                            }
                            else {
                                $pdUnchecked += @([ordered]@{ autoAttendantId = [string]$pdAa.Identity; userId = $pdUser; error = "$_" })
                            }
                        }
                    }
                }
                $pdAttendantRows += @([ordered]@{
                    id = [string]$pdAa.Identity
                    name = [string]$pdAa.Name
                    authorizedUsers = @($pdUsers)
                    callFlows = @($pdFlows)
                    associations = @($pdLinks)
                })
            }

            $pdScheduleRows = @()
            foreach ($pdSchedule in $pdSchedules) {
                if (-not $pdSchedule) { continue }
                $pdWindows = @()
                if ($pdSchedule.FixedSchedule) {
                    foreach ($pdRange in @($pdSchedule.FixedSchedule.DateTimeRanges)) {
                        if ($pdRange) {
                            $pdWindows += @([ordered]@{
                                start = ([datetime]$pdRange.Start).ToString('yyyy-MM-ddTHH:mm:ss')
                                end = ([datetime]$pdRange.End).ToString('yyyy-MM-ddTHH:mm:ss')
                            })
                        }
                    }
                }
                $pdScheduleRows += @([ordered]@{
                    id = [string]$pdSchedule.Id
                    name = [string]$pdSchedule.Name
                    type = [string]$pdSchedule.Type
                    windows = @($pdWindows)
                    usedBy = @(@($pdSchedule.AssociatedConfigurationIds) | Where-Object { $_ } | ForEach-Object { [string]$_ })
                })
            }

            $pdDocument = [ordered]@{
                autoAttendants = @($pdAttendantRows)
                schedules = @($pdScheduleRows)
                missingAuthorizedUsers = @($pdMissing)
                uncheckedAuthorizedUsers = @($pdUnchecked)
            }
            Write-Host ("PDHOLIDAY_JSON: " + (ConvertTo-Json -InputObject $pdDocument -Depth 12 -Compress))
            Write-Host "SUCCESS: Holiday call handling retrieved"
        }
        catch {
            Write-Host "ERROR: Failed to read the holiday call handling: $_"
        }
        """;

    private const string CopyTemplate = """
        try {
            $pdAa = Get-CsAutoAttendant -Identity '__AA__' -ErrorAction Stop
            if (-not $pdAa) {
                throw "Auto attendant __AA__ was not found."
            }
            $pdAttached = @($pdAa.CallHandlingAssociations | Where-Object { "$($_.Type)" -eq 'Holiday' -and $_.ScheduleId -eq '__TARGET__' })
            if ($pdAttached.Count -gt 0) {
                Write-Host "INFO: Schedule __TARGET__ is attached to auto attendant '$($pdAa.Name)' already. Nothing was changed."
            }
            else {
                $pdSourceLink = @($pdAa.CallHandlingAssociations | Where-Object { "$($_.Type)" -eq 'Holiday' -and $_.ScheduleId -eq '__SOURCE__' }) | Select-Object -First 1
                if (-not $pdSourceLink) {
                    throw "Schedule __SOURCE__ is not attached to auto attendant '$($pdAa.Name)' as a holiday."
                }
                $pdSourceFlow = @($pdAa.CallFlows | Where-Object { $_.Id -eq $pdSourceLink.CallFlowId }) | Select-Object -First 1
                if (-not $pdSourceFlow) {
                    throw "Call flow $($pdSourceLink.CallFlowId) of the source schedule was not found."
                }
                $pdTarget = Get-CsOnlineSchedule -Id '__TARGET__' -ErrorAction Stop
                if (-not $pdTarget) {
                    throw "Schedule __TARGET__ was not found."
                }

                # The new call flow uses the menu and the greetings of the source call flow as they are.
                $pdFlowArguments = @{ Name = [string]$pdTarget.Name; Menu = $pdSourceFlow.Menu }
                if ($pdSourceFlow.Greetings) {
                    $pdFlowArguments['Greetings'] = @($pdSourceFlow.Greetings)
                }
                $pdNewFlow = New-CsAutoAttendantCallFlow @pdFlowArguments -ErrorAction Stop
                foreach ($pdProperty in @('ForceListenMenuEnabled', 'RingResourceAccountDelegates', 'TimeoutThreshold', 'TimeoutDisconnectPromptType', 'TimeoutDisconnectPromptCustomText', 'AiDisclaimerType', 'AiDisclaimerCustomText')) {
                    if ($null -ne $pdSourceFlow.$pdProperty -and "$($pdSourceFlow.$pdProperty)" -ne '') {
                        try {
                            $pdNewFlow.$pdProperty = $pdSourceFlow.$pdProperty
                        }
                        catch {
                            Write-Host "WARNING: The call flow setting $pdProperty was not copied: $_"
                        }
                    }
                }
                $pdNewLink = New-CsAutoAttendantCallHandlingAssociation -Type Holiday -ScheduleId $pdTarget.Id -CallFlowId $pdNewFlow.Id -ErrorAction Stop
        __REPLACE_BLOCK____USERS_BLOCK__
                $pdAa.CallFlows += @($pdNewFlow)
                $pdAa.CallHandlingAssociations += @($pdNewLink)
                Set-CsAutoAttendant -Instance $pdAa -ErrorAction Stop | Out-Null
            }

            # The save is checked by reading the auto attendant again.
            $pdSaved = Get-CsAutoAttendant -Identity '__AA__' -ErrorAction Stop
            $pdSavedLink = @($pdSaved.CallHandlingAssociations | Where-Object { "$($_.Type)" -eq 'Holiday' -and $_.ScheduleId -eq '__TARGET__' })
            if ($pdSavedLink.Count -eq 0) {
                throw "Teams reported no error, but schedule __TARGET__ is not attached to auto attendant '$($pdSaved.Name)'."
            }
            Write-Host "SUCCESS: Schedule __TARGET__ is attached to auto attendant '$($pdSaved.Name)' with the call handling of schedule __SOURCE__"
        }
        catch {
            Write-Host "ERROR: Failed to copy the holiday call handling: $_"
        }
        """;

    private const string ReplaceTemplate = """

                # The expired schedule comes off this auto attendant with its call flow. The schedule itself stays.
                $pdOldLink = @($pdAa.CallHandlingAssociations | Where-Object { "$($_.Type)" -eq 'Holiday' -and $_.ScheduleId -eq '__REPLACE__' }) | Select-Object -First 1
                if (-not $pdOldLink) {
                    throw "Schedule __REPLACE__ is not attached to auto attendant '$($pdAa.Name)' as a holiday."
                }
                $pdAa.CallHandlingAssociations = @($pdAa.CallHandlingAssociations | Where-Object { -not ("$($_.Type)" -eq 'Holiday' -and $_.ScheduleId -eq '__REPLACE__') })
                $pdAa.CallFlows = @($pdAa.CallFlows | Where-Object { $_.Id -ne $pdOldLink.CallFlowId })
        """;

    private const string UsersTemplate = """

                # Authorized users that no longer exist stop Teams from saving the auto attendant.
                $pdGone = @(__USERS__)
                $pdAa.AuthorizedUsers = @($pdAa.AuthorizedUsers | Where-Object { $pdGone -notcontains "$_" })
        """;
}
