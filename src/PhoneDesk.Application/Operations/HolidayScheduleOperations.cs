using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhoneDesk.Operations;

/// <summary>A reason why a holiday change cannot be saved. <see cref="Code"/> is stable; the message is for the operator.</summary>
public sealed record HolidayBlocker(string Code, string Message);

/// <summary>What a change of the dates of one holiday schedule would do. Nothing is changed by the check.</summary>
public sealed class ScheduleEditAssessment
{
    public ScheduleInfo? Schedule { get; init; }

    public IReadOnlyList<ScheduleWindow> NewWindows { get; init; } = Array.Empty<ScheduleWindow>();

    public IReadOnlyList<ScheduleWindow> Added { get; init; } = Array.Empty<ScheduleWindow>();

    public IReadOnlyList<ScheduleWindow> Removed { get; init; } = Array.Empty<ScheduleWindow>();

    public IReadOnlyList<ScheduleWindow> Kept { get; init; } = Array.Empty<ScheduleWindow>();

    /// <summary>The auto attendants that use the schedule as a holiday: the change applies to all of them.</summary>
    public IReadOnlyList<AutoAttendantCallHandling> AffectedAutoAttendants { get; init; } = Array.Empty<AutoAttendantCallHandling>();

    /// <summary>New dates that overlap another holiday of an affected auto attendant.</summary>
    public IReadOnlyList<HolidayOverlap> Overlaps { get; init; } = Array.Empty<HolidayOverlap>();

    public IReadOnlyList<HolidayBlocker> Blockers { get; init; } = Array.Empty<HolidayBlocker>();

    public bool NoChange => Added.Count == 0 && Removed.Count == 0;

    public bool CanSave => Blockers.Count == 0 && !NoChange && Schedule is not null;
}

/// <summary>A new greeting for the holiday call flow of one schedule on one auto attendant: a text or an audio file.</summary>
public sealed record GreetingChange(string AutoAttendantId, string ScheduleId, string? Text, string? AudioFileId);

public sealed class GreetingAssessment
{
    public AutoAttendantCallHandling? AutoAttendant { get; init; }

    public ScheduleInfo? Schedule { get; init; }

    public CallFlowInfo? CallFlow { get; init; }

    /// <summary>Other schedules of the auto attendant that use the same call flow: they get the new greeting too.</summary>
    public IReadOnlyList<ScheduleInfo> OtherSchedulesOnTheCallFlow { get; init; } = Array.Empty<ScheduleInfo>();

    public IReadOnlyList<HolidayBlocker> Blockers { get; init; } = Array.Empty<HolidayBlocker>();

    public bool NoChange { get; init; }

    public bool CanSave => Blockers.Count == 0 && !NoChange;
}

/// <summary>One use of a schedule by an auto attendant.</summary>
public sealed record ScheduleReference(string AutoAttendantId, string AutoAttendantName, string Type, string CallFlowId, bool CallFlowIsRemoved);

public sealed class RetireScheduleItem
{
    public string ScheduleId { get; init; } = string.Empty;

    public ScheduleInfo? Schedule { get; init; }

    public bool Expired { get; init; }

    public IReadOnlyList<ScheduleReference> References { get; init; } = Array.Empty<ScheduleReference>();

    /// <summary>The tenant has no schedule with this ID: it was retired before, for example by a run that stopped later.</summary>
    public bool AlreadyGone => Schedule is null;
}

/// <summary>What retiring holiday schedules would do: which links come off which auto attendant, and which schedules are deleted.</summary>
public sealed class RetireAssessment
{
    public IReadOnlyList<RetireScheduleItem> Schedules { get; init; } = Array.Empty<RetireScheduleItem>();

    public IReadOnlyList<HolidayBlocker> Blockers { get; init; } = Array.Empty<HolidayBlocker>();

    /// <summary>The schedules that are still there. Only these are sent.</summary>
    public IReadOnlyList<RetireScheduleItem> Remaining => Schedules.Where(item => !item.AlreadyGone).ToList();

    /// <summary>Every named schedule is gone already: there is nothing to do.</summary>
    public bool NothingLeft => Blockers.Count == 0 && Schedules.Count > 0 && Remaining.Count == 0;

    public bool CanSave => Blockers.Count == 0 && Remaining.Count > 0;
}

/// <summary>One auto attendant of a rollout: the schedule whose call handling it copies, and an expired schedule to take off.</summary>
public sealed record RolloutItem(string AutoAttendantId, string SourceScheduleId, string? ReplaceScheduleId = null);

public enum RolloutOutcome
{
    /// <summary>The target schedule is attached and its call handling is the same as the source.</summary>
    Verified,

    /// <summary>The target schedule is attached, but the call handling differs from the source.</summary>
    Different,

    /// <summary>The change can be saved and was not run yet.</summary>
    Planned,

    /// <summary>A check refuses the change; nothing is sent for this auto attendant.</summary>
    Blocked,

    /// <summary>The change was sent, and the tenant does not show the schedule on the auto attendant.</summary>
    Failed
}

public sealed class RolloutItemResult
{
    public RolloutItem Item { get; init; } = new(string.Empty, string.Empty);

    public CopyAssessment Assessment { get; init; } = new();

    public RolloutOutcome Outcome { get; init; }

    public IReadOnlyList<string> Differences { get; init; } = Array.Empty<string>();

    public CallFlowInfo? CallFlow { get; init; }
}

/// <summary>
/// Changes to holiday schedules that exist in the tenant: other dates, another greeting, retiring
/// expired schedules, and attaching one schedule to several auto attendants. Each operation has a
/// check (nothing is sent), a script that takes object IDs only, and a state text that binds an
/// approval to what the check showed. Both heads use the same three parts.
/// </summary>
public static class HolidayScheduleOperations
{
    /// <summary>Teams reads a text of up to 1024 characters.</summary>
    public const int MaximumGreetingLength = 1024;

    // ---- dates -------------------------------------------------------------------------------

    public static ScheduleEditAssessment AssessDates(
        HolidayCallHandlingState state,
        string scheduleId,
        IReadOnlyList<ScheduleWindow> newWindows,
        bool acceptOverlaps)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(newWindows);
        var blockers = new List<HolidayBlocker>();
        var schedule = state.FindSchedule(scheduleId);
        var wanted = newWindows.Distinct().OrderBy(window => window.Start).ThenBy(window => window.End).ToList();
        if (schedule is null)
        {
            blockers.Add(new("scheduleNotFound", $"No schedule has the ID {scheduleId}."));
            return new ScheduleEditAssessment { NewWindows = wanted, Blockers = blockers };
        }

        if (!schedule.IsFixed)
        {
            blockers.Add(new("notAHolidaySchedule", $"Schedule '{schedule.Name}' has no fixed dates, so it is not a holiday schedule."));
        }

        if (wanted.Count == 0)
        {
            blockers.Add(new("noDates", $"Schedule '{schedule.Name}' must keep one date at least. To remove the schedule, retire it."));
        }

        foreach (var window in wanted.Where(window => window.End <= window.Start))
        {
            blockers.Add(new("invalidRange", $"The range {Format(window)} ends before it starts."));
        }

        for (var first = 0; first < wanted.Count; first++)
        {
            for (var second = first + 1; second < wanted.Count; second++)
            {
                if (wanted[first].Overlaps(wanted[second]))
                {
                    blockers.Add(new("overlapInSchedule", $"The ranges {Format(wanted[first])} and {Format(wanted[second])} of the schedule overlap."));
                }
            }
        }

        var added = wanted.Where(window => !schedule.Windows.Contains(window)).ToList();
        var removed = schedule.Windows.Where(window => !wanted.Contains(window)).OrderBy(window => window.Start).ToList();
        var kept = wanted.Where(window => schedule.Windows.Contains(window)).ToList();

        var affected = state.AutoAttendants.Where(attendant => attendant.HolidayLinkFor(schedule.Id) is not null).ToList();
        var overlaps = new List<HolidayOverlap>();
        var changed = new ScheduleInfo { Id = schedule.Id, Name = schedule.Name, Type = schedule.Type, Windows = added, UsedBy = schedule.UsedBy };
        foreach (var attendant in affected)
        {
            foreach (var link in attendant.HolidayLinks.Where(link => !HolidayCallHandling.SameId(link.ScheduleId, schedule.Id)))
            {
                var other = state.FindSchedule(link.ScheduleId);
                if (other is null)
                {
                    continue;
                }

                var found = (from window in added
                             from otherWindow in other.Windows
                             where window.Overlaps(otherWindow)
                             select new HolidayOverlap(changed, window, other, otherWindow, attendant.FindCallFlow(link.CallFlowId))).ToList();
                overlaps.AddRange(found);
                if (found.Any(overlap => overlap.SameStart))
                {
                    blockers.Add(new("sameStartTime",
                        $"On auto attendant '{attendant.Name}' the holiday '{other.Name}' starts at the same time as a new date. Teams refuses that."));
                }
                else if (found.Count > 0 && !acceptOverlaps)
                {
                    blockers.Add(new("overlap",
                        $"On auto attendant '{attendant.Name}' a new date overlaps the holiday '{other.Name}', so two call handlings apply at the same time. " +
                        "Change the dates, or accept the overlap."));
                }
            }
        }

        return new ScheduleEditAssessment
        {
            Schedule = schedule,
            NewWindows = wanted,
            Added = added,
            Removed = removed,
            Kept = kept,
            AffectedAutoAttendants = affected,
            Overlaps = overlaps,
            Blockers = blockers
        };
    }

    /// <summary>
    /// Replaces the date ranges of a schedule. The script refuses to run when the schedule no longer
    /// holds the dates the check saw, and reports success only when Teams holds the new dates.
    /// </summary>
    public static string GetSetDatesCommand(string scheduleId, IReadOnlyList<ScheduleWindow> currentWindows, IReadOnlyList<ScheduleWindow> newWindows)
    {
        ArgumentNullException.ThrowIfNull(currentWindows);
        ArgumentNullException.ThrowIfNull(newWindows);
        if (newWindows.Count == 0)
        {
            throw new ArgumentException("A schedule must keep one date at least.");
        }

        var ranges = new StringBuilder();
        foreach (var window in newWindows.OrderBy(window => window.Start))
        {
            ranges.Append("    $pdRanges += @(New-CsOnlineDateTimeRange -Start '")
                .Append(TeamsDate(window.Start)).Append("' -End '").Append(TeamsDate(window.End)).AppendLine("' -ErrorAction Stop)");
        }

        return SetDatesTemplate
            .Replace("__RANGES__", ranges.ToString().TrimEnd('\r', '\n'), StringComparison.Ordinal)
            .Replace("__EXPECTED__", WindowKey(currentWindows), StringComparison.Ordinal)
            .Replace("__NEW__", WindowKey(newWindows), StringComparison.Ordinal)
            .Replace("__ID__", HolidayCallHandling.RequireId(scheduleId, "a schedule"), StringComparison.Ordinal);
    }

    // ---- greeting ----------------------------------------------------------------------------

    public static GreetingAssessment AssessGreeting(HolidayCallHandlingState state, GreetingChange change)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(change);
        var blockers = new List<HolidayBlocker>();
        var attendant = state.FindAutoAttendant(change.AutoAttendantId);
        var schedule = state.FindSchedule(change.ScheduleId);
        var link = attendant?.HolidayLinkFor(change.ScheduleId);
        var flow = link is null ? null : attendant!.FindCallFlow(link.CallFlowId);
        if (attendant is null)
        {
            blockers.Add(new("autoAttendantNotFound", $"No auto attendant has the ID {change.AutoAttendantId}."));
        }
        else if (link is null)
        {
            blockers.Add(new("notAttached", $"Schedule {change.ScheduleId} is not attached to auto attendant '{attendant.Name}' as a holiday."));
        }
        else if (flow is null)
        {
            blockers.Add(new("callFlowMissing", $"The call flow {link.CallFlowId} was not found on auto attendant '{attendant.Name}'."));
        }

        var hasText = !string.IsNullOrWhiteSpace(change.Text);
        var hasAudio = !string.IsNullOrWhiteSpace(change.AudioFileId);
        if (hasText == hasAudio)
        {
            blockers.Add(new("oneGreeting", "Give the greeting as a text or as the ID of an audio file, not both and not neither."));
        }
        else if (hasText && change.Text!.Trim().Length > MaximumGreetingLength)
        {
            blockers.Add(new("textTooLong", $"The text has {change.Text.Trim().Length} characters; Teams reads {MaximumGreetingLength} at most."));
        }
        else if (hasAudio && !Guid.TryParse(change.AudioFileId!.Trim(), out _))
        {
            blockers.Add(new("audioFileId", $"'{change.AudioFileId}' is not the ID of an audio file. Give the ID (a GUID), not the file name."));
        }

        var others = attendant is null || link is null
            ? new List<ScheduleInfo>()
            : attendant.Associations
                .Where(other => HolidayCallHandling.SameId(other.CallFlowId, link.CallFlowId) && !HolidayCallHandling.SameId(other.ScheduleId, link.ScheduleId))
                .Select(other => state.FindSchedule(other.ScheduleId))
                .Where(other => other is not null)
                .Select(other => other!)
                .ToList();

        return new GreetingAssessment
        {
            AutoAttendant = attendant,
            Schedule = schedule,
            CallFlow = flow,
            OtherSchedulesOnTheCallFlow = others,
            Blockers = blockers,
            NoChange = blockers.Count == 0 && flow is not null && GreetingIs(flow, change)
        };
    }

    /// <summary>True when the call flow has exactly the greeting the change asks for.</summary>
    public static bool GreetingIs(CallFlowInfo flow, GreetingChange change)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(change);
        var spoken = flow.Greetings.Where(prompt => !string.IsNullOrEmpty(prompt.Text) || prompt.AudioFile is not null).ToList();
        if (spoken.Count != 1)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(change.AudioFileId)
            ? spoken[0].AudioFile is null && string.Equals(spoken[0].Text, change.Text?.Trim(), StringComparison.Ordinal)
            : HolidayCallHandling.SameId(spoken[0].AudioFile?.Id, change.AudioFileId);
    }

    /// <summary>
    /// Gives the holiday call flow of one schedule on one auto attendant another greeting. The menu
    /// and the target of the call flow stay as they are. The text travels as Base64, so no character
    /// of it is PowerShell code and Teams gets it as it was written.
    /// </summary>
    public static string GetSetGreetingCommand(GreetingChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        string prompt;
        if (!string.IsNullOrWhiteSpace(change.AudioFileId))
        {
            prompt =
                "    $pdAudio = Get-CsOnlineAudioFile -ApplicationId OrgAutoAttendant -Identity '" +
                HolidayCallHandling.RequireId(change.AudioFileId, "an audio file") + "' -ErrorAction Stop\n" +
                "    if (-not $pdAudio) { throw \"The audio file was not found for auto attendants.\" }\n" +
                "    $pdPrompt = New-CsAutoAttendantPrompt -AudioFilePrompt $pdAudio -ErrorAction Stop";
        }
        else
        {
            var text = (change.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                throw new ArgumentException("The greeting is missing.");
            }

            prompt =
                "    $pdText = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "'))\n" +
                "    $pdPrompt = New-CsAutoAttendantPrompt -TextToSpeechPrompt $pdText -ErrorAction Stop";
        }

        return SetGreetingTemplate
            .Replace("__PROMPT__", prompt, StringComparison.Ordinal)
            .Replace("__AA__", HolidayCallHandling.RequireId(change.AutoAttendantId, "an auto attendant"), StringComparison.Ordinal)
            .Replace("__SCHEDULE__", HolidayCallHandling.RequireId(change.ScheduleId, "a schedule"), StringComparison.Ordinal);
    }

    // ---- retire ------------------------------------------------------------------------------

    /// <summary>
    /// Checks whether schedules can be retired: taken off every auto attendant that uses them as a
    /// holiday, and deleted. A schedule with a date that has not passed is refused unless
    /// <paramref name="allowNotExpired"/> is set. The state must hold every auto attendant.
    /// </summary>
    public static RetireAssessment AssessRetire(
        HolidayCallHandlingState state,
        IReadOnlyList<string> scheduleIds,
        DateTime now,
        bool allowNotExpired)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scheduleIds);
        var blockers = new List<HolidayBlocker>();
        var ids = scheduleIds.Select(id => HolidayCallHandling.RequireId(id, "a schedule")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
        {
            blockers.Add(new("noSchedules", "No schedule was given."));
        }

        var items = new List<RetireScheduleItem>();
        foreach (var id in ids)
        {
            var schedule = state.FindSchedule(id);
            if (schedule is null)
            {
                // Not a blocker: a second run after a partial failure names schedules that are gone.
                items.Add(new RetireScheduleItem { ScheduleId = id });
                continue;
            }

            if (!schedule.IsFixed)
            {
                blockers.Add(new("notAHolidaySchedule", $"Schedule '{schedule.Name}' is not a holiday schedule (it holds opening hours). It cannot be retired here."));
            }

            // The time of the newest end is compared with the caller's clock: a schedule is used by
            // auto attendants in different time zones, so a day of margin is the caller's business.
            var expired = schedule.IsExpired(now);
            if (!expired && !allowNotExpired)
            {
                blockers.Add(new("notExpired",
                    $"Schedule '{schedule.Name}' still has a date that has not passed (last end {schedule.LastEnd:yyyy-MM-dd HH:mm}). Callers would lose that holiday."));
            }

            var references = new List<ScheduleReference>();
            foreach (var attendant in state.AutoAttendants)
            {
                foreach (var link in attendant.Associations.Where(link => HolidayCallHandling.SameId(link.ScheduleId, id)))
                {
                    var isHoliday = string.Equals(link.Type, "Holiday", StringComparison.OrdinalIgnoreCase);
                    if (!isHoliday)
                    {
                        blockers.Add(new("usedAsOpeningHours",
                            $"Auto attendant '{attendant.Name}' uses schedule '{schedule.Name}' as {link.Type}, not as a holiday. It is not taken off."));
                    }

                    // The call flow goes only when no link that stays uses it.
                    var flowStays = attendant.Associations.Any(other =>
                        HolidayCallHandling.SameId(other.CallFlowId, link.CallFlowId) &&
                        !(string.Equals(other.Type, "Holiday", StringComparison.OrdinalIgnoreCase) && ids.Contains(other.ScheduleId, StringComparer.OrdinalIgnoreCase)));
                    references.Add(new ScheduleReference(attendant.Id, attendant.Name, link.Type, link.CallFlowId, !flowStays));
                }
            }

            var unknownUsers = schedule.UsedBy
                .Where(user => !state.AutoAttendants.Any(attendant => HolidayCallHandling.SameId(attendant.Id, user)))
                .ToList();
            if (unknownUsers.Count > 0)
            {
                blockers.Add(new("usedElsewhere",
                    $"Schedule '{schedule.Name}' is used by {string.Join(", ", unknownUsers)}, which was not read as an auto attendant. It is not deleted."));
            }

            items.Add(new RetireScheduleItem { ScheduleId = id, Schedule = schedule, Expired = expired, References = references });
        }

        return new RetireAssessment { Schedules = items, Blockers = blockers };
    }

    /// <summary>
    /// Takes the given schedules off the given auto attendants (holiday links only, with a call flow
    /// that nothing else uses) and deletes the schedules. Every schedule has its own result line,
    /// and a schedule that something still uses is not deleted.
    /// </summary>
    public static string GetRetireCommand(RetireAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var ids = assessment.Remaining.Select(item => HolidayCallHandling.RequireId(item.ScheduleId, "a schedule")).ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("No schedule is left to retire.");
        }

        var attendants = assessment.Remaining
            .SelectMany(item => item.References)
            .Select(reference => HolidayCallHandling.RequireId(reference.AutoAttendantId, "an auto attendant"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return RetireTemplate
            .Replace("__SCHEDULES__", string.Join(", ", ids.Select(id => "'" + id + "'")), StringComparison.Ordinal)
            .Replace("__ATTENDANTS__", string.Join(", ", attendants.Select(id => "'" + id + "'")), StringComparison.Ordinal);
    }

    /// <summary>The schedules of a retire request that the tenant still holds, and the links that are still there.</summary>
    public static (IReadOnlyList<string> RemainingSchedules, IReadOnlyList<ScheduleReference> RemainingLinks) RetireLeftovers(
        HolidayCallHandlingState after, RetireAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(assessment);
        var ids = assessment.Schedules.Select(item => item.ScheduleId).ToList();
        var schedules = ids.Where(id => after.FindSchedule(id) is not null).ToList();
        var links = (from attendant in after.AutoAttendants
                     from link in attendant.Associations
                     where ids.Contains(link.ScheduleId, StringComparer.OrdinalIgnoreCase)
                     select new ScheduleReference(attendant.Id, attendant.Name, link.Type, link.CallFlowId, false)).ToList();
        return (schedules, links);
    }

    // ---- rollout -----------------------------------------------------------------------------

    /// <summary>Checks the copy for every auto attendant of a rollout. An attached target counts as done.</summary>
    public static IReadOnlyList<RolloutItemResult> AssessRollout(
        HolidayCallHandlingState state,
        string targetScheduleId,
        IReadOnlyList<RolloutItem> items,
        bool acceptOverlaps,
        bool removeMissingAuthorizedUsers,
        Func<string, DateTime> nowFor)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(nowFor);
        var results = new List<RolloutItemResult>();
        foreach (var item in items)
        {
            var request = new CopyRequest(item.AutoAttendantId, item.SourceScheduleId, targetScheduleId, item.ReplaceScheduleId, acceptOverlaps, removeMissingAuthorizedUsers);
            var assessment = HolidayCallHandling.Assess(state, request, nowFor(item.AutoAttendantId));
            results.Add(Judge(state, targetScheduleId, item, assessment, sent: false));
        }

        return results;
    }

    /// <summary>
    /// Reads the outcome of a rollout from the tenant. What the script printed plays no part: an
    /// item counts as done only when the tenant shows the schedule on the auto attendant.
    /// </summary>
    public static IReadOnlyList<RolloutItemResult> VerifyRollout(
        HolidayCallHandlingState after,
        string targetScheduleId,
        IReadOnlyList<RolloutItemResult> planned)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(planned);
        return planned
            .Select(result => result.Outcome == RolloutOutcome.Blocked
                ? result
                : Judge(after, targetScheduleId, result.Item, result.Assessment, sent: result.Outcome == RolloutOutcome.Planned))
            .ToList();
    }

    /// <summary>One script for every item of the rollout that can be saved: one save per auto attendant.</summary>
    public static string GetRolloutCommand(string targetScheduleId, IReadOnlyList<RolloutItemResult> planned, bool acceptOverlaps)
    {
        ArgumentNullException.ThrowIfNull(planned);
        var script = new StringBuilder();
        foreach (var result in planned.Where(result => result.Outcome == RolloutOutcome.Planned))
        {
            var request = new CopyRequest(
                result.Item.AutoAttendantId, result.Item.SourceScheduleId, targetScheduleId, result.Item.ReplaceScheduleId, acceptOverlaps, false);
            script.AppendLine(HolidayCallHandling.GetCopyCommand(request, result.Assessment.AuthorizedUsersToRemove));
            script.AppendLine();
        }

        return script.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// The objects a rollout changes: the auto attendants of the items that will be saved, with
    /// their source and replaced schedules, and the target. Items that are left out are not part
    /// of it, so an item that cannot be saved does not stop the backup of the others.
    /// </summary>
    public static (IReadOnlyList<string> AutoAttendants, IReadOnlyList<string> Schedules) RolloutBackupScope(
        string targetScheduleId, IReadOnlyList<RolloutItemResult> planned)
    {
        ArgumentNullException.ThrowIfNull(planned);
        var saved = planned.Where(result => result.Outcome == RolloutOutcome.Planned).ToList();
        var schedules = saved
            .SelectMany(result => new[] { result.Item.SourceScheduleId, result.Item.ReplaceScheduleId })
            .Append(targetScheduleId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (saved.Select(result => result.Item.AutoAttendantId).ToList(), schedules);
    }

    private static RolloutItemResult Judge(
        HolidayCallHandlingState state, string targetScheduleId, RolloutItem item, CopyAssessment assessment, bool sent)
    {
        var attendant = state.FindAutoAttendant(item.AutoAttendantId);
        var link = attendant?.HolidayLinkFor(targetScheduleId);
        var flow = link is null ? null : attendant!.FindCallFlow(link.CallFlowId);
        if (link is not null)
        {
            // Without both call flows nothing can be compared, and that is not "the same".
            IReadOnlyList<string> differences = assessment.SourceCallFlow is null
                ? new[] { "The source schedule has no call handling on this auto attendant, so the attached call handling could not be compared." }
                : flow is null
                    ? new[] { $"The call flow {link.CallFlowId} of the attached schedule was not found on the auto attendant." }
                    : HolidayCallHandling.Differences(assessment.SourceCallFlow, flow);
            return new RolloutItemResult
            {
                Item = item,
                Assessment = assessment,
                Outcome = differences.Count == 0 ? RolloutOutcome.Verified : RolloutOutcome.Different,
                Differences = differences,
                CallFlow = flow
            };
        }

        return new RolloutItemResult
        {
            Item = item,
            Assessment = assessment,
            Outcome = sent ? RolloutOutcome.Failed : assessment.Blockers.Count > 0 ? RolloutOutcome.Blocked : RolloutOutcome.Planned
        };
    }

    // ---- approved state ----------------------------------------------------------------------

    /// <summary>What an approval of a date change is bound to: the schedule as the check saw it, and the new dates.</summary>
    public static string ApprovedState(ScheduleEditAssessment assessment)
        => JsonSerializer.Serialize(new
        {
            schedule = assessment.Schedule is null ? null : new { assessment.Schedule.Id, assessment.Schedule.Name, assessment.Schedule.Windows },
            assessment.NewWindows,
            attendants = assessment.AffectedAutoAttendants.Select(attendant => attendant.Id)
        });

    public static string ApprovedState(GreetingAssessment assessment)
        => JsonSerializer.Serialize(new
        {
            attendant = assessment.AutoAttendant?.Id,
            schedule = assessment.Schedule?.Id,
            callFlow = assessment.CallFlow,
            others = assessment.OtherSchedulesOnTheCallFlow.Select(schedule => schedule.Id)
        });

    public static string ApprovedState(RetireAssessment assessment)
        => JsonSerializer.Serialize(assessment.Remaining.Select(item => new
        {
            item.ScheduleId,
            name = item.Schedule?.Name,
            windows = item.Schedule?.Windows,
            item.References
        }));

    public static string ApprovedState(string targetScheduleId, IReadOnlyList<RolloutItemResult> planned)
        => JsonSerializer.Serialize(new
        {
            target = targetScheduleId,
            items = planned.Select(result => new
            {
                result.Item,
                outcome = result.Outcome.ToString(),
                source = result.Assessment.SourceCallFlow,
                targetSchedule = result.Assessment.TargetSchedule is null ? null : new { result.Assessment.TargetSchedule.Name, result.Assessment.TargetSchedule.Windows },
                replaced = result.Assessment.ReplacedSchedule?.Id,
                users = result.Assessment.AuthorizedUsersToRemove
            })
        });

    // ---- text --------------------------------------------------------------------------------

    public static string Format(ScheduleWindow window)
        => window.Start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " to " +
           window.End.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads date ranges from text: one range per line or separated by ';'. A range is a day
    /// (2027-12-25, the whole day) or a start and an end (2027-12-24 12:00 to 2027-12-25 00:00,
    /// the end is not part of the range). A day and an end day (2027-12-24 to 2027-12-26) covers
    /// both days.
    /// </summary>
    public static IReadOnlyList<ScheduleWindow> ParseWindows(string? text)
    {
        var windows = new List<ScheduleWindow>();
        foreach (var raw in (text ?? string.Empty).Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(new[] { " to ", " - ", " – ", "/" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2)
            {
                throw new FormatException($"'{line}' is not a date range. Write 2027-12-25, or 2027-12-24 12:00 to 2027-12-25 00:00.");
            }

            var start = ParseMoment(parts[0], line, out var startIsDay);
            if (parts.Length == 1)
            {
                windows.Add(new ScheduleWindow(start, startIsDay ? start.AddDays(1) : throw new FormatException($"'{line}' has a start time but no end.")));
                continue;
            }

            var end = ParseMoment(parts[1], line, out var endIsDay);
            windows.Add(new ScheduleWindow(start, endIsDay ? end.AddDays(1) : end));
        }

        return windows;
    }

    private static DateTime ParseMoment(string text, string line, out bool isDay)
    {
        var formats = new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd H:mm", "dd.MM.yyyy HH:mm", "dd.MM.yyyy H:mm", "d.M.yyyy HH:mm", "d.M.yyyy H:mm" };
        var days = new[] { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy" };
        if (DateTime.TryParseExact(text, days, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            isDay = true;
            return day;
        }

        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
        {
            isDay = false;
            return moment;
        }

        throw new FormatException($"'{text}' in '{line}' is not a date. Write 2027-12-25 or 2027-12-24 12:00.");
    }

    private static string TeamsDate(DateTime value) => value.ToString("d/M/yyyy H:mm", CultureInfo.InvariantCulture);

    private static string WindowKey(IEnumerable<ScheduleWindow> windows)
        => string.Join(",", windows
            .Select(window => window.Start.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "/" + window.End.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))
            .OrderBy(key => key, StringComparer.Ordinal));

    private const string SetDatesTemplate = """
        try {
            $pdSchedule = Get-CsOnlineSchedule -Id '__ID__' -ErrorAction Stop
            if (-not $pdSchedule -or -not $pdSchedule.FixedSchedule) {
                throw "Schedule __ID__ was not found or has no fixed dates."
            }
            $pdKeyOf = {
                param($pdItem)
                $pdKeys = @(@($pdItem.FixedSchedule.DateTimeRanges) | Where-Object { $_ } | ForEach-Object {
                    ([datetime]$_.Start).ToString('yyyy-MM-ddTHH:mm:ss') + '/' + ([datetime]$_.End).ToString('yyyy-MM-ddTHH:mm:ss')
                })
                [array]::Sort($pdKeys, [System.StringComparer]::Ordinal)
                $pdKeys -join ','
            }
            if ((& $pdKeyOf $pdSchedule) -ne '__EXPECTED__') {
                throw "The dates of schedule '$($pdSchedule.Name)' are not the ones the preview showed. Nothing was changed."
            }
            $pdRanges = @()
        __RANGES__
            $pdSchedule.FixedSchedule.DateTimeRanges = @($pdRanges)
            Set-CsOnlineSchedule -Instance $pdSchedule -ErrorAction Stop | Out-Null

            # The save is checked by reading the schedule again.
            $pdSaved = Get-CsOnlineSchedule -Id '__ID__' -ErrorAction Stop
            if ((& $pdKeyOf $pdSaved) -ne '__NEW__') {
                throw "Teams holds other dates than the ones that were sent: $(& $pdKeyOf $pdSaved)"
            }
            Write-Host "SUCCESS: Schedule '$($pdSaved.Name)' has the new dates"
        }
        catch {
            Write-Host "ERROR: Failed to change the dates of schedule __ID__: $_"
        }
        """;

    private const string SetGreetingTemplate = """
        try {
            $pdAa = Get-CsAutoAttendant -Identity '__AA__' -ErrorAction Stop
            if (-not $pdAa) {
                throw "Auto attendant __AA__ was not found."
            }
            $pdLink = @($pdAa.CallHandlingAssociations | Where-Object { "$($_.Type)" -eq 'Holiday' -and $_.ScheduleId -eq '__SCHEDULE__' }) | Select-Object -First 1
            if (-not $pdLink) {
                throw "Schedule __SCHEDULE__ is not attached to auto attendant '$($pdAa.Name)' as a holiday."
            }
            $pdFlow = @($pdAa.CallFlows | Where-Object { $_.Id -eq $pdLink.CallFlowId }) | Select-Object -First 1
            if (-not $pdFlow) {
                throw "Call flow $($pdLink.CallFlowId) was not found."
            }
        __PROMPT__
            # Only the greeting changes: the menu and its target stay as they are.
            $pdFlow.Greetings = @($pdPrompt)
            Set-CsAutoAttendant -Instance $pdAa -ErrorAction Stop | Out-Null
            Write-Host "SUCCESS: The holiday greeting of schedule __SCHEDULE__ on auto attendant '$($pdAa.Name)' was replaced"
        }
        catch {
            Write-Host "ERROR: Failed to replace the holiday greeting: $_"
        }
        """;

    private const string RetireTemplate = """
        try {
            $pdRetire = @(__SCHEDULES__)
            $pdFailed = 0
            foreach ($pdAaId in @(__ATTENDANTS__)) {
                $pdAa = Get-CsAutoAttendant -Identity $pdAaId -ErrorAction Stop
                $pdGo = @($pdAa.CallHandlingAssociations | Where-Object { "$($_.Type)" -eq 'Holiday' -and $pdRetire -contains "$($_.ScheduleId)" })
                if ($pdGo.Count -eq 0) {
                    Write-Host "INFO: Auto attendant '$($pdAa.Name)' does not use these schedules as a holiday. Nothing was changed there."
                    continue
                }
                $pdStay = @($pdAa.CallHandlingAssociations | Where-Object { -not ("$($_.Type)" -eq 'Holiday' -and $pdRetire -contains "$($_.ScheduleId)") })
                $pdStayFlows = @($pdStay | ForEach-Object { "$($_.CallFlowId)" })
                # A call flow goes only when no link that stays uses it.
                $pdGoFlows = @($pdGo | ForEach-Object { "$($_.CallFlowId)" } | Where-Object { $pdStayFlows -notcontains $_ })
                $pdAa.CallHandlingAssociations = @($pdStay)
                $pdAa.CallFlows = @($pdAa.CallFlows | Where-Object { $pdGoFlows -notcontains "$($_.Id)" })
                Set-CsAutoAttendant -Instance $pdAa -ErrorAction Stop | Out-Null
                Write-Host "INFO: $($pdGo.Count) holiday link(s) were taken off auto attendant '$($pdAa.Name)'."
            }

            foreach ($pdId in $pdRetire) {
                try {
                    $pdSchedule = Get-CsOnlineSchedule -Id $pdId -ErrorAction Stop
                    if (@($pdSchedule.AssociatedConfigurationIds | Where-Object { $_ }).Count -gt 0) {
                        throw "it is still used by $(@($pdSchedule.AssociatedConfigurationIds) -join ', ')"
                    }
                    try {
                        Remove-CsOnlineSchedule -Id $pdId -ErrorAction Stop
                    }
                    catch {
                        # The cmdlet can report an error after it did its work: the schedule is read again.
                        $pdLeft = $null
                        try { $pdLeft = Get-CsOnlineSchedule -Id $pdId -ErrorAction Stop } catch { $pdLeft = $null }
                        if ($pdLeft) { throw }
                    }
                    Write-Host "RETIRED: $pdId"
                }
                catch {
                    $pdFailed++
                    Write-Host "NOTRETIRED: $pdId $_"
                }
            }
            if ($pdFailed -gt 0) {
                throw "$pdFailed of $($pdRetire.Count) schedule(s) were not deleted. Run the same change again after the cause is removed; schedules that are gone are skipped."
            }
            Write-Host "SUCCESS: $($pdRetire.Count) holiday schedule(s) were retired"
        }
        catch {
            Write-Host "ERROR: Failed to retire the holiday schedules: $_"
        }
        """;
}
