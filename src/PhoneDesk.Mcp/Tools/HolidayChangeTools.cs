using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;
using PhoneDesk.Topology;

namespace PhoneDesk.Mcp.Tools;

/// <summary>One auto attendant of a rollout.</summary>
public sealed class RolloutItemInput
{
    [Description("ID of the auto attendant.")]
    public string AutoAttendantId { get; set; } = string.Empty;

    [Description("ID of the schedule whose call handling on this auto attendant is copied.")]
    public string SourceScheduleId { get; set; } = string.Empty;

    [Description("ID of an expired holiday schedule of this auto attendant to take off in the same save. Optional.")]
    public string? ReplaceScheduleId { get; set; }
}

/// <summary>
/// Changes to holiday schedules that exist: other dates, another greeting, retiring expired
/// schedules, and one schedule for several auto attendants. The checks, the scripts and the
/// read-back are the ones the desktop app uses (<see cref="HolidayScheduleOperations"/>).
/// </summary>
[McpServerToolType]
internal sealed class HolidayChangeTools
{
    private const string PlanIdDescription = "The planId from the first call. Omit to get the plan.";
    private const string TwoCalls = " CHANGES THE TENANT. Call it first without planId: it reads the tenant, shows what would change and changes nothing. Show the result to the user, then call it again with the same arguments and the planId to run it. The plan is bound to what the first call showed: when the tenant changed in between, the plan is refused.";

    private readonly HolidayCallHandlingReader _reader;
    private readonly TenantGateway _tenant;
    private readonly ChangeGate _changes;
    private readonly TimeProvider _time;
    private readonly HolidayBackupWriter _backups;

    public HolidayChangeTools(HolidayCallHandlingReader reader, TenantGateway tenant, ChangeGate changes, TimeProvider time, HolidayBackupWriter backups)
    {
        _backups = backups;
        _reader = reader;
        _tenant = tenant;
        _changes = changes;
        _time = time;
    }

    [McpServerTool(Name = "edit_holiday_schedule_dates", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Replaces the date ranges of a holiday schedule that exists: add a date, remove a date, or make a day a half day. Give ALL ranges the schedule must have afterwards; ranges you leave out are removed. A range is a whole day ('2027-12-25'), several whole days ('2027-12-24 to 2027-12-26') or a start and an end in the local time of the auto attendant ('2027-12-24 12:00 to 2027-12-25 00:00'; the end is not part of the range, so an afternoon closure ends at 00:00 of the next day). A range can cross a year boundary. The first call shows the added, removed and kept ranges, every auto attendant that uses the schedule (the change applies to all of them), and overlaps of a new date with another holiday of those auto attendants. After the save the server reads the schedule again and reports whether Teams holds the dates that were sent." + TwoCalls)]
    public Task<string> EditHolidayScheduleDates(
        [Description("ID of the schedule (list_holiday_schedules).")] string scheduleId,
        [Description("Every date range the schedule must have afterwards, one text per range.")] string[] dateRanges,
        [Description("True when the user accepts that a new date overlaps another holiday of an auto attendant. Two holidays that start at the same time are refused in every case.")] bool acceptOverlaps = false,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var id = HolidayCallHandling.RequireId(scheduleId, "a schedule");
            var wanted = HolidayScheduleOperations.ParseWindows(string.Join("\n", dateRanges ?? Array.Empty<string>()));
            var topology = await _tenant.GetTopologyAsync(false, cancellationToken);
            var state = await _reader.ReadAsync(null, includeAutoAttendants: true, checkAuthorizedUsers: false, cancellationToken);
            var assessment = HolidayScheduleOperations.AssessDates(state, id, wanted, acceptOverlaps);
            var preview = new
            {
                schedule = assessment.Schedule is null ? null : new { id = assessment.Schedule.Id, name = assessment.Schedule.Name },
                added = assessment.Added.Select(HolidayScheduleOperations.Format).ToList(),
                removed = assessment.Removed.Select(HolidayScheduleOperations.Format).ToList(),
                kept = assessment.Kept.Select(HolidayScheduleOperations.Format).ToList(),
                affectedAutoAttendants = assessment.AffectedAutoAttendants
                    .Select(attendant => new { id = attendant.Id, name = attendant.Name, phoneNumbers = PhoneNumbers(topology, attendant.Id) })
                    .ToList(),
                overlaps = assessment.Overlaps.Select(HolidayCallHandlingTools.OverlapView).ToList()
            };
            if (assessment.Blockers.Count > 0)
            {
                return Blocked(assessment.Blockers, preview);
            }

            if (assessment.NoChange)
            {
                return new { status = "noChange", success = true, note = "The schedule has these dates already. Nothing was changed.", preview };
            }

            var schedule = assessment.Schedule!;
            var applied = false;
            var outcome = await _changes.PlanOrApplyAsync(
                new ChangeRequest(
                    "EditHolidayScheduleDates",
                    schedule.Name,
                    HolidayScheduleOperations.GetSetDatesCommand(id, schedule.Windows, assessment.NewWindows),
                    $"Change the dates of schedule '{schedule.Name}': {assessment.Added.Count} added, {assessment.Removed.Count} removed, {assessment.Kept.Count} kept. " +
                    $"It applies to {assessment.AffectedAutoAttendants.Count} auto attendant(s).")
                {
                    ApprovedState = HolidayScheduleOperations.ApprovedState(assessment),
                    ScopedBackupAsync = _backups.For(assessment.AffectedAutoAttendants.Select(attendant => attendant.Id), new[] { id }),
                    OnSuccess = _ => applied = true,
                    ChangeWasMadeDespiteError = async (_, token) =>
                        HoldsDates(await _reader.ReadAsync(null, false, false, token), id, assessment.NewWindows)
                },
                planId,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(planId))
            {
                return new { preview, plan = outcome };
            }

            if (!applied)
            {
                return outcome;
            }

            var saved = (await _reader.ReadAsync(null, false, false, cancellationToken)).FindSchedule(id);
            return McpJson.With(outcome, "verification", new
            {
                datesAsSent = saved is not null && HoldsDates(saved, assessment.NewWindows),
                datesInTeams = saved?.Windows.OrderBy(window => window.Start).Select(HolidayScheduleOperations.Format).ToList()
            });
        });

    [McpServerTool(Name = "replace_holiday_greeting", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Gives the holiday call flow of one schedule on one auto attendant another greeting: a text Teams reads, or an audio file that was uploaded for auto attendants (import_audio_file with the application 'OrgAutoAttendant'; give its ID, not its file name, because two uploads can have the same file name). The menu action and the target of the call flow stay as they are. The first call shows the old greeting with its audio file ID, the new one, and other schedules of the auto attendant that use the same call flow and get the new greeting too. After the save the server reads the auto attendant again and reports whether the call flow has the greeting that was sent." + TwoCalls)]
    public Task<string> ReplaceHolidayGreeting(
        [Description("ID of the auto attendant.")] string autoAttendantId,
        [Description("ID of the holiday schedule whose greeting on this auto attendant changes.")] string scheduleId,
        [Description("The new greeting as text. Give this or audioFileId.")] string? greetingText = null,
        [Description("The ID of the audio file for the new greeting. Give this or greetingText.")] string? audioFileId = null,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var change = new GreetingChange(
                HolidayCallHandling.RequireId(autoAttendantId, "an auto attendant"),
                HolidayCallHandling.RequireId(scheduleId, "a schedule"),
                string.IsNullOrWhiteSpace(greetingText) ? null : greetingText.Trim(),
                string.IsNullOrWhiteSpace(audioFileId) ? null : audioFileId.Trim());
            var ids = new[] { change.AutoAttendantId };
            var state = await _reader.ReadAsync(ids, includeAutoAttendants: true, checkAuthorizedUsers: false, cancellationToken);
            var assessment = HolidayScheduleOperations.AssessGreeting(state, change);
            var preview = new
            {
                autoAttendant = assessment.AutoAttendant is null ? null : new { id = assessment.AutoAttendant.Id, name = assessment.AutoAttendant.Name },
                schedule = assessment.Schedule is null ? null : new { id = assessment.Schedule.Id, name = assessment.Schedule.Name },
                oldCallHandling = HolidayCallHandling.Describe(assessment.CallFlow),
                oldGreetings = assessment.CallFlow?.Greetings,
                newGreeting = change.AudioFileId is null ? new { text = change.Text, audioFileId = (string?)null } : new { text = (string?)null, audioFileId = (string?)change.AudioFileId },
                alsoChanges = assessment.OtherSchedulesOnTheCallFlow.Select(schedule => new { id = schedule.Id, name = schedule.Name }).ToList()
            };
            if (assessment.Blockers.Count > 0)
            {
                return Blocked(assessment.Blockers, preview);
            }

            if (assessment.NoChange)
            {
                return new { status = "noChange", success = true, note = "The call flow has this greeting already. Nothing was changed.", preview };
            }

            var attendant = assessment.AutoAttendant!;
            var applied = false;
            var outcome = await _changes.PlanOrApplyAsync(
                new ChangeRequest(
                    "ReplaceHolidayGreeting",
                    attendant.Name,
                    HolidayScheduleOperations.GetSetGreetingCommand(change),
                    $"Replace the holiday greeting of schedule '{assessment.Schedule?.Name ?? change.ScheduleId}' on auto attendant '{attendant.Name}'. " +
                    $"Now: {HolidayCallHandling.Describe(assessment.CallFlow)}. New greeting: " +
                    (change.AudioFileId is null ? $"text \"{change.Text}\"." : $"audio file {change.AudioFileId}."))
                {
                    ApprovedState = HolidayScheduleOperations.ApprovedState(assessment),
                    ScopedBackupAsync = _backups.For(ids, new[] { change.ScheduleId }),
                    OnSuccess = _ => applied = true,
                    ChangeWasMadeDespiteError = async (_, token) => GreetingSaved(await _reader.ReadAsync(ids, true, false, token), change)
                },
                planId,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(planId))
            {
                return new { preview, plan = outcome };
            }

            if (!applied)
            {
                return outcome;
            }

            var after = await _reader.ReadAsync(ids, true, false, cancellationToken);
            var savedAttendant = after.FindAutoAttendant(change.AutoAttendantId);
            var savedFlow = savedAttendant?.FindCallFlow(savedAttendant.HolidayLinkFor(change.ScheduleId)?.CallFlowId);
            return McpJson.With(outcome, "verification", new
            {
                greetingAsSent = GreetingSaved(after, change),
                // The menu and its target must be what they were before the save.
                routingUnchanged = assessment.CallFlow is not null && savedFlow is not null &&
                    HolidayCallHandling.Differences(assessment.CallFlow, savedFlow).All(difference => difference.StartsWith("greeting", StringComparison.Ordinal)),
                callHandling = HolidayCallHandling.Describe(savedFlow),
                callFlow = savedFlow
            });
        });

    [McpServerTool(Name = "retire_holiday_schedules", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Retires holiday schedules that exist: takes each one off every auto attendant that uses it as a holiday, and deletes the schedule. DELETES OBJECTS THAT THIS SERVER DID NOT CREATE: it needs the user's agreement for exactly the schedules named, by ID. The first call shows every schedule with its dates, whether all dates have passed, and every auto attendant link that would be removed; a call flow is removed only when no link that stays uses it. A schedule with a date that has not passed is refused unless allowNotExpired is true. A schedule that is used as opening hours, or by something that is not an auto attendant, is refused. Schedules you do not name are not touched. After the save the server reads the tenant again and reports schedules and links that are still there; calling it again with the same schedules retries only what is left. A deleted schedule cannot be restored; the backup taken before the change holds its dates." + TwoCalls)]
    public Task<string> RetireHolidaySchedules(
        [Description("IDs of the schedules to retire (list_holiday_schedules).")] string[] scheduleIds,
        [Description("True to retire a schedule that still has a date in the future. Callers lose that holiday.")] bool allowNotExpired = false,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var state = await _reader.ReadAsync(null, includeAutoAttendants: true, checkAuthorizedUsers: false, cancellationToken);
            var assessment = HolidayScheduleOperations.AssessRetire(
                state, scheduleIds ?? Array.Empty<string>(), _time.GetLocalNow().DateTime, allowNotExpired);
            var preview = new
            {
                schedules = assessment.Schedules.Select(item => new
                {
                    id = item.ScheduleId,
                    name = item.Schedule?.Name,
                    dateRanges = item.Schedule?.Windows.OrderBy(window => window.Start).Select(HolidayScheduleOperations.Format).ToList(),
                    expired = item.Expired,
                    alreadyGone = item.AlreadyGone,
                    removedLinks = item.References.Select(reference => new
                    {
                        autoAttendantId = reference.AutoAttendantId,
                        autoAttendant = reference.AutoAttendantName,
                        type = reference.Type,
                        callFlowId = reference.CallFlowId,
                        callFlowIsRemoved = reference.CallFlowIsRemoved
                    }).ToList()
                }).ToList()
            };
            if (assessment.Blockers.Count > 0)
            {
                return Blocked(assessment.Blockers, preview);
            }

            if (assessment.NothingLeft)
            {
                return new
                {
                    status = "alreadyRetired",
                    success = true,
                    note = "The tenant has none of these schedules any more. Nothing was changed.",
                    preview
                };
            }

            var names = string.Join(", ", assessment.Remaining.Select(item => $"'{item.Schedule!.Name}' ({item.ScheduleId})"));
            var links = assessment.Remaining.Sum(item => item.References.Count);
            var applied = false;
            var outcome = await _changes.PlanOrApplyAsync(
                new ChangeRequest(
                    "RetireHolidaySchedules",
                    string.Join(", ", assessment.Remaining.Select(item => item.Schedule!.Name)),
                    HolidayScheduleOperations.GetRetireCommand(assessment),
                    $"Retire {assessment.Remaining.Count} holiday schedule(s): {names}. {links} auto attendant link(s) are removed, then the schedules are deleted. This cannot be undone.")
                {
                    ApprovedState = HolidayScheduleOperations.ApprovedState(assessment),
                    ScopedBackupAsync = _backups.For(
                        assessment.Remaining.SelectMany(item => item.References).Select(reference => reference.AutoAttendantId),
                        assessment.Remaining.Select(item => item.ScheduleId)),
                    OnSuccess = _ => applied = true,
                    ChangeWasMadeDespiteError = async (_, token) =>
                    {
                        var left = HolidayScheduleOperations.RetireLeftovers(await _reader.ReadAsync(null, true, false, token), assessment);
                        return left.RemainingSchedules.Count == 0 && left.RemainingLinks.Count == 0;
                    }
                },
                planId,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(planId))
            {
                return new { preview, plan = outcome };
            }

            // Read in every case: after a partial failure the user must know what is gone and what is left.
            var after = await _reader.ReadAsync(null, true, false, cancellationToken);
            var leftovers = HolidayScheduleOperations.RetireLeftovers(after, assessment);
            return McpJson.With(outcome, "verification", new
            {
                retired = applied || (leftovers.RemainingSchedules.Count == 0 && leftovers.RemainingLinks.Count == 0),
                remainingSchedules = leftovers.RemainingSchedules,
                remainingLinks = leftovers.RemainingLinks.Select(reference => new { reference.AutoAttendantId, autoAttendant = reference.AutoAttendantName, reference.Type }).ToList(),
                next = leftovers.RemainingSchedules.Count == 0
                    ? null
                    : "Call the tool again with the schedules that remain. Schedules that are gone are not touched again."
            });
        });

    [McpServerTool(Name = "rollout_holiday_call_handling", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Attaches one holiday schedule to several auto attendants in one run, each with the call handling that another schedule has on that same auto attendant (see copy_holiday_call_handling for one auto attendant). Use it when one new schedule serves several main numbers that each handle the holiday differently. The first call reads the tenant and reports every auto attendant as verified (the schedule is attached already), planned or blocked, with the blockers. Blocked items are left out; the others are saved, one save per auto attendant. After the run the server reads every auto attendant again: an item counts as done only when the tenant shows the schedule there with the same call handling as its source, whatever the script printed. A run that stopped in the middle is continued by calling the tool again with the same items: verified items are skipped, so no schedule, call flow or link is made twice." + TwoCalls)]
    public Task<string> RolloutHolidayCallHandling(
        [Description("ID of the schedule to attach. It must exist (create_holiday_schedule).")] string targetScheduleId,
        [Description("The auto attendants, each with the schedule whose call handling it copies.")] RolloutItemInput[] items,
        [Description("True when the user accepts overlaps with holidays that are attached already.")] bool acceptOverlaps = false,
        [Description("True to take authorized users that no longer exist off each auto attendant in the same save.")] bool removeMissingAuthorizedUsers = false,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var target = HolidayCallHandling.RequireId(targetScheduleId, "the target schedule");
            var wanted = (items ?? Array.Empty<RolloutItemInput>())
                .Select(item => new RolloutItem(
                    HolidayCallHandling.RequireId(item.AutoAttendantId, "an auto attendant"),
                    HolidayCallHandling.RequireId(item.SourceScheduleId, "a source schedule"),
                    string.IsNullOrWhiteSpace(item.ReplaceScheduleId) ? null : HolidayCallHandling.RequireId(item.ReplaceScheduleId, "the schedule to replace")))
                .ToList();
            if (wanted.Count == 0)
            {
                throw new ArgumentException("No auto attendant was given.");
            }

            if (wanted.GroupBy(item => item.AutoAttendantId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            {
                throw new ArgumentException("An auto attendant is named twice. Each auto attendant gets the schedule once.");
            }

            var ids = wanted.Select(item => item.AutoAttendantId).ToArray();
            var topology = await _tenant.GetTopologyAsync(false, cancellationToken);
            var state = await _reader.ReadAsync(ids, includeAutoAttendants: true, checkAuthorizedUsers: true, cancellationToken);
            var planned = HolidayScheduleOperations.AssessRollout(
                state, target, wanted, acceptOverlaps, removeMissingAuthorizedUsers,
                id => HolidayCallHandlingTools.NowFor(_time, topology, id));
            var toSave = planned.Count(result => result.Outcome == RolloutOutcome.Planned);
            if (toSave == 0)
            {
                return new
                {
                    status = planned.All(result => result.Outcome is RolloutOutcome.Verified or RolloutOutcome.Different) ? "alreadyAttached" : "blocked",
                    success = planned.All(result => result.Outcome == RolloutOutcome.Verified),
                    note = "Nothing can be saved: every auto attendant has the schedule already or is blocked. Nothing was changed.",
                    items = planned.Select(result => ItemView(result, topology)).ToList()
                };
            }

            var outcome = await _changes.PlanOrApplyAsync(
                new ChangeRequest(
                    "RolloutHolidayCallHandling",
                    state.FindSchedule(target)?.Name ?? target,
                    HolidayScheduleOperations.GetRolloutCommand(target, planned, acceptOverlaps),
                    $"Attach schedule '{state.FindSchedule(target)?.Name ?? target}' to {toSave} auto attendant(s), one save each. " +
                    $"{planned.Count(result => result.Outcome == RolloutOutcome.Verified)} have it already; {planned.Count(result => result.Outcome == RolloutOutcome.Blocked)} are blocked and left out.")
                {
                    ApprovedState = HolidayScheduleOperations.ApprovedState(target, planned),
                    ScopedBackupAsync = _backups.For(
                        HolidayScheduleOperations.RolloutBackupScope(target, planned).AutoAttendants,
                        HolidayScheduleOperations.RolloutBackupScope(target, planned).Schedules),
                    // One auto attendant can fail while the others are saved: the tenant decides, per item.
                    IsSuccess = result => result.Succeeded,
                    ChangeWasMadeDespiteError = async (_, token) =>
                        HolidayScheduleOperations.VerifyRollout(await _reader.ReadAsync(ids, true, false, token), target, planned)
                            .Where(result => result.Outcome != RolloutOutcome.Blocked)
                            .All(result => result.Outcome == RolloutOutcome.Verified)
                },
                planId,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(planId))
            {
                return new { items = planned.Select(result => ItemView(result, topology)).ToList(), plan = outcome };
            }

            var verified = HolidayScheduleOperations.VerifyRollout(
                await _reader.ReadAsync(ids, true, false, cancellationToken), target, planned);
            var withItems = McpJson.With(outcome, "items", verified.Select(result => ItemView(result, topology)).ToList());
            return McpJson.With(withItems, "summary", new
            {
                verified = verified.Count(result => result.Outcome == RolloutOutcome.Verified),
                different = verified.Count(result => result.Outcome == RolloutOutcome.Different),
                failed = verified.Count(result => result.Outcome == RolloutOutcome.Failed),
                blocked = verified.Count(result => result.Outcome == RolloutOutcome.Blocked),
                next = verified.Any(result => result.Outcome == RolloutOutcome.Failed)
                    ? "Call the tool again with the same items: verified auto attendants are skipped."
                    : null
            });
        });

    [McpServerTool(Name = "backup_holiday_call_handling", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Writes a backup of the holiday call handling to a file on this computer: the call flows of the auto attendants, their links to schedules, and the schedules with their dates. It reads only these objects, so it works when backup_tenant fails because another part of the tenant (for example a call queue without agents) makes Teams print a warning. Give the auto attendants a change will touch, or none for all. The result says how many auto attendants, schedules, call flows and links were captured. The call fails when an auto attendant that was named is not in what the tenant returned. Only reads from the tenant. A holiday change takes this backup by itself when the full backup cannot be taken.")]
    public Task<string> BackupHolidayCallHandling(
        [Description("IDs of the auto attendants to back up. Omit to back up every auto attendant.")] string[]? autoAttendantIds = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var ids = (autoAttendantIds ?? Array.Empty<string>()).Select(id => HolidayCallHandling.RequireId(id, "an auto attendant")).ToList();
            var backup = await _backups.WriteAsync(ids, Array.Empty<string>(), cancellationToken);
            return new
            {
                success = true,
                backup,
                scope = ids.Count == 0 ? "every auto attendant and the schedules they use" : "the named auto attendants and the schedules they use",
                note = "This backup holds holiday call handling only. backup_tenant writes the whole phone system."
            };
        });

    private static object ItemView(RolloutItemResult result, TenantTopology topology)
        => new
        {
            autoAttendantId = result.Item.AutoAttendantId,
            autoAttendant = result.Assessment.AutoAttendant?.Name,
            phoneNumbers = PhoneNumbers(topology, result.Item.AutoAttendantId),
            outcome = result.Outcome.ToString(),
            source = result.Assessment.SourceSchedule is null ? null : new { id = result.Assessment.SourceSchedule.Id, name = result.Assessment.SourceSchedule.Name },
            copiedCallHandling = HolidayCallHandling.Describe(result.Assessment.SourceCallFlow),
            callHandlingInTeams = result.CallFlow is null ? null : HolidayCallHandling.Describe(result.CallFlow),
            differences = result.Differences,
            blockers = result.Outcome is RolloutOutcome.Blocked or RolloutOutcome.Different ? result.Assessment.Blockers : Array.Empty<CopyBlocker>(),
            replacedSchedule = result.Assessment.ReplacedSchedule?.Name,
            authorizedUsersToRemove = result.Assessment.AuthorizedUsersToRemove
        };

    private static object Blocked(IReadOnlyList<HolidayBlocker> blockers, object preview)
        => new
        {
            status = "blocked",
            success = false,
            error = "The change cannot be saved: " + string.Join(" ", blockers.Select(blocker => blocker.Message)),
            blockers,
            preview,
            next = "Nothing was changed and no plan was made. Remove the cause or change the arguments, then call the tool again."
        };

    private static bool HoldsDates(HolidayCallHandlingState state, string scheduleId, IReadOnlyList<ScheduleWindow> windows)
        => state.FindSchedule(scheduleId) is { } schedule && HoldsDates(schedule, windows);

    private static bool HoldsDates(ScheduleInfo schedule, IReadOnlyList<ScheduleWindow> windows)
        => schedule.Windows.Count == windows.Count && windows.All(window => schedule.Windows.Contains(window));

    private static bool GreetingSaved(HolidayCallHandlingState state, GreetingChange change)
    {
        var attendant = state.FindAutoAttendant(change.AutoAttendantId);
        var flow = attendant?.FindCallFlow(attendant.HolidayLinkFor(change.ScheduleId)?.CallFlowId);
        return flow is not null && HolidayScheduleOperations.GreetingIs(flow, change);
    }

    private static IReadOnlyList<string> PhoneNumbers(TenantTopology topology, string autoAttendantId)
    {
        var attendant = topology.AutoAttendants.FirstOrDefault(item => HolidayCallHandling.SameId(item.Identity, autoAttendantId));
        return attendant is null
            ? new List<string>()
            : topology.ResourceAccounts
                .Where(account => account.HasPhoneNumber &&
                                  attendant.ResourceAccountObjectIds.Contains(account.ObjectId, StringComparer.OrdinalIgnoreCase))
                .Select(account => account.PhoneNumber!)
                .ToList();
    }
}
