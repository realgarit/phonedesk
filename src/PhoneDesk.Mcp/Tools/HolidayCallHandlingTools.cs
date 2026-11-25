using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;
using PhoneDesk.Topology;

namespace PhoneDesk.Mcp.Tools;

/// <summary>
/// Holiday schedules that exist already: what callers get on them, per auto attendant, and the
/// copy of that call handling to a new schedule (the work of every new year).
/// </summary>
[McpServerToolType]
internal sealed class HolidayCallHandlingTools
{
    private const string PlanIdDescription = "The planId from the first call. Omit to get the plan.";

    private readonly HolidayCallHandlingReader _reader;
    private readonly TenantGateway _tenant;
    private readonly ChangeGate _changes;
    private readonly TimeProvider _time;

    public HolidayCallHandlingTools(HolidayCallHandlingReader reader, TenantGateway tenant, ChangeGate changes, TimeProvider time)
    {
        _reader = reader;
        _tenant = tenant;
        _changes = changes;
        _time = time;
    }

    [McpServerTool(Name = "list_holiday_schedules", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lists the holiday schedules of the tenant with their ID, their date ranges, whether all dates have passed, and the auto attendants that use each schedule. Reads the schedules only, not the whole tenant. Use the IDs with holiday_call_handling and copy_holiday_call_handling. Read-only.")]
    public Task<string> ListHolidaySchedules(
        [Description("Only schedules whose name or ID contains this text.")] string? search = null,
        [Description("False to leave out schedules whose dates have all passed.")] bool includeExpired = true,
        [Description("Maximum number of rows (default 50, at most 500).")] int limit = 50,
        [Description("Number of rows to skip, for the next page.")] int offset = 0,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var state = await _reader.ReadAsync(null, includeAutoAttendants: false, checkAuthorizedUsers: false, cancellationToken);
            var names = AttendantNames(await _tenant.GetTopologyAsync(false, cancellationToken));
            var now = Now;
            var wanted = search?.Trim();
            var rows = state.Schedules
                .Where(schedule => schedule.IsFixed)
                .Where(schedule => includeExpired || !schedule.IsExpired(now))
                .Where(schedule => string.IsNullOrEmpty(wanted) ||
                                   schedule.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                                   schedule.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                .OrderBy(schedule => schedule.Windows.Count == 0 ? DateTime.MaxValue : schedule.Windows.Min(window => window.Start))
                .ThenBy(schedule => schedule.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var skip = Math.Max(0, offset);
            var take = Math.Clamp(limit, 1, 500);
            var page = rows.Skip(skip).Take(take).Select(schedule => ScheduleView(schedule, now, names)).ToList();
            return new
            {
                total = rows.Count,
                offset = skip,
                nextOffset = skip + page.Count < rows.Count ? skip + page.Count : (int?)null,
                schedules = page
            };
        });

    [McpServerTool(Name = "holiday_call_handling", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Shows what callers get on each holiday schedule of an auto attendant: the greeting (text, or audio file with its ID), the menu action, the transfer target, the voicemail options and the priority. One schedule can have a different call handling on every auto attendant, so the result is per auto attendant. It also shows holiday schedules of the auto attendant that overlap, the number of call flows against the limit Teams allows, and the authorized users. Reads the named auto attendant and the schedules only. Read-only.")]
    public Task<string> HolidayCallHandlingOf(
        [Description("ID or exact name of the auto attendant. Omit to read every auto attendant.")] string? autoAttendant = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var topology = await _tenant.GetTopologyAsync(false, cancellationToken);
            string[]? ids = string.IsNullOrWhiteSpace(autoAttendant)
                ? null
                : [(await _reader.ResolveAutoAttendantAsync(autoAttendant, cancellationToken)).Id];
            var state = await _reader.ReadAsync(ids, includeAutoAttendants: true, checkAuthorizedUsers: false, cancellationToken);
            var names = AttendantNames(topology);
            var now = Now;
            return new
            {
                callFlowLimit = HolidayCallHandling.AdditionalCallFlowLimit,
                autoAttendants = state.AutoAttendants
                    .OrderBy(attendant => attendant.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(attendant => AttendantView(state, attendant, topology, names, now))
                    .ToList()
            };
        });

    [McpServerTool(Name = "copy_holiday_call_handling", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Attaches a holiday schedule to one auto attendant with the same call handling that another holiday schedule has on that auto attendant: greeting or no greeting, audio file, menu action, transfer target, voicemail options. Use it for the holidays of a new year: create the new schedule (create_holiday_schedule), then copy the call handling of last year's schedule, once per auto attendant. All three objects are given by ID (list_holiday_schedules, holiday_call_handling). The first call reads the tenant and shows the call handling that is copied, the date ranges, every overlap with a holiday that is attached already, and what stops Teams from saving: the call flow limit and authorized users that no longer exist. With a blocker no plan is made. After the save the server reads the auto attendant again and reports whether the schedule is attached and whether the call handling is the same as the source. A schedule that is attached already is reported and not changed, so a retry is safe. CHANGES THE TENANT. Call it first without planId: it changes nothing. Show the result to the user, then call it again with the same arguments and the planId to run it.")]
    public Task<string> CopyHolidayCallHandling(
        [Description("ID of the auto attendant.")] string autoAttendantId,
        [Description("ID of the schedule whose call handling on this auto attendant is copied, for example last year's schedule.")] string sourceScheduleId,
        [Description("ID of the schedule to attach, for example the new year's schedule. It must exist.")] string targetScheduleId,
        [Description("ID of an expired holiday schedule of this auto attendant to take off in the same save, when the auto attendant is at the call flow limit. Its call flow on this auto attendant is removed; the schedule itself stays in the tenant. Only a schedule whose dates have all passed is accepted.")] string? replaceScheduleId = null,
        [Description("True when the user accepts that the target schedule overlaps a holiday that is attached already. Two holidays that start at the same time are refused in every case.")] bool acceptOverlaps = false,
        [Description("True to take authorized users that no longer exist off the auto attendant in the same save. The plan names them.")] bool removeMissingAuthorizedUsers = false,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var request = new CopyRequest(
                HolidayCallHandling.RequireId(autoAttendantId, "an auto attendant"),
                HolidayCallHandling.RequireId(sourceScheduleId, "the source schedule"),
                HolidayCallHandling.RequireId(targetScheduleId, "the target schedule"),
                string.IsNullOrWhiteSpace(replaceScheduleId) ? null : HolidayCallHandling.RequireId(replaceScheduleId, "the schedule to replace"),
                acceptOverlaps,
                removeMissingAuthorizedUsers);

            var topology = await _tenant.GetTopologyAsync(false, cancellationToken);
            var names = AttendantNames(topology);
            var state = await _reader.ReadAsync([request.AutoAttendantId], includeAutoAttendants: true, checkAuthorizedUsers: true, cancellationToken);
            var now = Now;
            var assessment = HolidayCallHandling.Assess(state, request, now);
            var preview = Preview(state, assessment, topology, names, now);

            if (assessment.Blockers.Count > 0)
            {
                return new
                {
                    status = "blocked",
                    success = false,
                    error = "The copy cannot be saved: " + string.Join(" ", assessment.Blockers.Select(blocker => blocker.Message)),
                    blockers = assessment.Blockers,
                    preview,
                    next = "Nothing was changed and no plan was made. Remove the cause or change the arguments, then call the tool again."
                };
            }

            if (assessment.AlreadyAttached)
            {
                return new
                {
                    status = "alreadyAttached",
                    success = true,
                    note = "The target schedule is attached to this auto attendant already. Nothing was changed.",
                    verification = Verify(state, request, assessment.SourceCallFlow),
                    preview
                };
            }

            var attendant = assessment.AutoAttendant!;
            var applied = false;
            var summary =
                $"Attach schedule '{assessment.TargetSchedule!.Name}' to auto attendant '{attendant.Name}' with the call handling of schedule " +
                $"'{assessment.SourceSchedule?.Name ?? request.SourceScheduleId}': {HolidayCallHandling.Describe(assessment.SourceCallFlow)}." +
                (assessment.ReplacedSchedule is null ? string.Empty : $" Take the expired schedule '{assessment.ReplacedSchedule.Name}' off this auto attendant.") +
                (assessment.AuthorizedUsersToRemove.Count == 0 ? string.Empty : $" Remove {assessment.AuthorizedUsersToRemove.Count} authorized user(s) that no longer exist.");
            var outcome = await _changes.PlanOrApplyAsync(
                new ChangeRequest(
                    "CopyHolidayCallHandling",
                    attendant.Name,
                    HolidayCallHandling.GetCopyCommand(request, assessment.AuthorizedUsersToRemove),
                    summary)
                {
                    OnSuccess = _ => applied = true,
                    // Teams can report an error after it saved. The tenant decides what happened.
                    ChangeWasMadeDespiteError = async (_, token) =>
                    {
                        var after = await _reader.ReadAsync([request.AutoAttendantId], true, false, token);
                        return after.FindAutoAttendant(request.AutoAttendantId)?.HolidayLinkFor(request.TargetScheduleId) is not null;
                    }
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

            var saved = await _reader.ReadAsync([request.AutoAttendantId], true, false, cancellationToken);
            return McpJson.With(outcome, "verification", Verify(saved, request, assessment.SourceCallFlow));
        });

    /// <summary>
    /// Compares what the tenant holds now with what was asked for. The script prints SUCCESS on its
    /// own check; this is the second one, on the content of the call flow.
    /// </summary>
    internal static object Verify(HolidayCallHandlingState state, CopyRequest request, CallFlowInfo? expected)
    {
        var attendant = state.FindAutoAttendant(request.AutoAttendantId);
        var link = attendant?.HolidayLinkFor(request.TargetScheduleId);
        var flow = link is null ? null : attendant!.FindCallFlow(link.CallFlowId);
        var differences = expected is null || flow is null
            ? []
            : HolidayCallHandling.Differences(expected, flow);
        var replacedGone = request.ReplaceScheduleId is null
            ? (bool?)null
            : attendant?.HolidayLinkFor(request.ReplaceScheduleId) is null;
        return new
        {
            attached = link is not null,
            sameAsSource = link is not null && flow is not null && expected is not null && differences.Count == 0,
            differences,
            callHandling = HolidayCallHandling.Describe(flow),
            callFlow = flow,
            replacedScheduleTakenOff = replacedGone,
            replacedScheduleStillExists = request.ReplaceScheduleId is null
                ? (bool?)null
                : state.FindSchedule(request.ReplaceScheduleId) is not null
        };
    }

    private DateTime Now => _time.GetLocalNow().DateTime;

    private static object Preview(
        HolidayCallHandlingState state,
        CopyAssessment assessment,
        TenantTopology topology,
        IReadOnlyDictionary<string, string> names,
        DateTime now)
    {
        var attendant = assessment.AutoAttendant;
        return new
        {
            autoAttendant = attendant is null
                ? null
                : new
                {
                    id = attendant.Id,
                    name = attendant.Name,
                    phoneNumbers = PhoneNumbers(topology, attendant.Id),
                    additionalCallFlows = attendant.CallFlows.Count,
                    callFlowLimit = HolidayCallHandling.AdditionalCallFlowLimit
                },
            source = assessment.SourceSchedule is null
                ? null
                : new
                {
                    schedule = ScheduleView(assessment.SourceSchedule, now, names),
                    callHandling = HolidayCallHandling.Describe(assessment.SourceCallFlow),
                    callFlow = assessment.SourceCallFlow
                },
            target = assessment.TargetSchedule is null ? null : ScheduleView(assessment.TargetSchedule, now, names),
            overlaps = assessment.Overlaps.Select(OverlapView).ToList(),
            replaced = assessment.ReplacedSchedule is null
                ? null
                : new
                {
                    schedule = ScheduleView(assessment.ReplacedSchedule, now, names),
                    removedCallHandling = HolidayCallHandling.Describe(assessment.ReplacedCallFlow),
                    removedCallFlow = assessment.ReplacedCallFlow
                },
            expiredSchedules = assessment.ExpiredSchedules.Select(schedule => ScheduleView(schedule, now, names)).ToList(),
            missingAuthorizedUsers = attendant is null
                ? []
                : state.MissingAuthorizedUsers.Where(user => HolidayCallHandling.SameId(user.AutoAttendantId, attendant.Id)).Select(user => user.UserId).ToList(),
            authorizedUsersToRemove = assessment.AuthorizedUsersToRemove,
            uncheckedAuthorizedUsers = state.UncheckedAuthorizedUsers
        };
    }

    private static object AttendantView(
        HolidayCallHandlingState state,
        AutoAttendantCallHandling attendant,
        TenantTopology topology,
        IReadOnlyDictionary<string, string> names,
        DateTime now)
        => new
        {
            id = attendant.Id,
            name = attendant.Name,
            phoneNumbers = PhoneNumbers(topology, attendant.Id),
            additionalCallFlows = attendant.CallFlows.Count,
            authorizedUsers = attendant.AuthorizedUsers,
            holidays = attendant.HolidayLinks
                .Select(link => (Link: link, Schedule: state.FindSchedule(link.ScheduleId), Flow: attendant.FindCallFlow(link.CallFlowId)))
                .OrderBy(item => item.Schedule?.Windows.Count > 0 ? item.Schedule.Windows.Min(window => window.Start) : DateTime.MaxValue)
                .Select(item => new
                {
                    schedule = item.Schedule is null
                        ? new { id = item.Link.ScheduleId, missing = true } as object
                        : ScheduleView(item.Schedule, now, names),
                    enabled = item.Link.Enabled,
                    priority = item.Link.Priority,
                    callHandling = HolidayCallHandling.Describe(item.Flow),
                    callFlow = item.Flow
                })
                .ToList(),
            overlaps = HolidayCallHandling.FindOverlaps(state, attendant).Select(OverlapView).ToList()
        };

    private static object ScheduleView(ScheduleInfo schedule, DateTime now, IReadOnlyDictionary<string, string> names)
        => new
        {
            id = schedule.Id,
            name = schedule.Name,
            dateRanges = schedule.Windows.OrderBy(window => window.Start).Select(WindowView).ToList(),
            expired = schedule.IsExpired(now),
            lastEnd = schedule.LastEnd?.ToString("yyyy-MM-dd HH:mm"),
            usedBy = schedule.UsedBy
                .Select(id => new { id, name = names.TryGetValue(id, out var name) ? name : null })
                .ToList()
        };

    internal static object OverlapView(HolidayOverlap overlap)
        => new
        {
            schedule = new { id = overlap.Schedule.Id, name = overlap.Schedule.Name, dateRange = WindowView(overlap.Window) },
            otherSchedule = new { id = overlap.OtherSchedule.Id, name = overlap.OtherSchedule.Name, dateRange = WindowView(overlap.OtherWindow) },
            otherCallHandling = HolidayCallHandling.Describe(overlap.OtherCallFlow),
            sameStart = overlap.SameStart
        };

    private static object WindowView(ScheduleWindow window)
        => new { start = window.Start.ToString("yyyy-MM-dd HH:mm"), end = window.End.ToString("yyyy-MM-dd HH:mm") };

    private static IReadOnlyDictionary<string, string> AttendantNames(TenantTopology topology)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attendant in topology.AutoAttendants)
        {
            names.TryAdd(attendant.Identity, attendant.Name);
        }

        return names;
    }

    /// <summary>The numbers callers dial to reach the auto attendant, from its resource accounts.</summary>
    private static IReadOnlyList<string> PhoneNumbers(TenantTopology topology, string autoAttendantId)
    {
        var attendant = topology.AutoAttendants.FirstOrDefault(item => HolidayCallHandling.SameId(item.Identity, autoAttendantId));
        return attendant is null
            ? []
            : topology.ResourceAccounts
                .Where(account => account.HasPhoneNumber &&
                                  attendant.ResourceAccountObjectIds.Contains(account.ObjectId, StringComparer.OrdinalIgnoreCase))
                .Select(account => account.PhoneNumber!)
                .ToList();
    }
}
