using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;

namespace PhoneDesk.Mcp.Tools;

/// <summary>
/// A customer's holiday list against the tenant: what is there, what is missing, what collides,
/// and after the work the table of changes for the customer record.
/// </summary>
[McpServerToolType]
internal sealed class HolidayListTools
{
    private const string ListDescription = "The customer's list, one entry per line: a day or a range, then the customer's wording. Examples: '25.12.2027 Weihnachten', '24.12.2027 12:00-24:00 Heiligabend', '2027-12-27 to 2027-12-31 Betriebsferien', 'G 01.08. Bundesfeier'. A leading letter marks the kind of day: G statutory holiday, B business closure, R additional rest day, H half day. Keep the customer's wording as it is.";
    private const string YearDescription = "The year for entries that have no year, for example 2027. Such entries are flagged in the result so the user can check them.";

    private readonly HolidayCallHandlingReader _reader;
    private readonly TenantGateway _tenant;
    private readonly McpEnvironment _environment;

    public HolidayListTools(HolidayCallHandlingReader reader, TenantGateway tenant, McpEnvironment environment)
    {
        _reader = reader;
        _tenant = tenant;
        _environment = environment;
    }

    [McpServerTool(Name = "compare_holiday_list", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Compares a customer's holiday list with the holidays the auto attendants have, whatever the schedules are called. For every entry and auto attendant it says: match (a schedule has exactly this range), covered (a longer range of a schedule contains it, for example company holidays that include New Year), sameStart (a schedule starts at the same time and ends at another; Teams refuses a second holiday with this start), partialOverlap, or missing. It also lists ranges the tenant has in the period of the list that the customer did not name, lines that could not be read, entries whose year was assumed, and whether the list spans more than one year. Each row shows what callers get on the matching schedule, so different greetings for the same holiday on different auto attendants are visible. Use it before you create or change schedules. Read-only.")]
    public Task<string> CompareHolidayList(
        [Description(ListDescription)] string list,
        [Description(YearDescription)] int defaultYear,
        [Description("IDs of the auto attendants to compare with. Omit to compare with every auto attendant.")] string[]? autoAttendantIds = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var parsed = HolidayListComparer.Parse(list, RequireYear(defaultYear));
            var ids = Ids(autoAttendantIds);
            var state = await _reader.ReadAsync(ids.Count == 0 ? null : ids, includeAutoAttendants: true, checkAuthorizedUsers: false, cancellationToken);
            var comparison = HolidayListComparer.Compare(state, parsed, ids);
            return new
            {
                entries = parsed.Entries.Count,
                years = parsed.Years,
                severalYears = parsed.Years.Count > 1,
                yearAssumedFor = parsed.Entries.Where(entry => entry.YearWasAssumed).Select(entry => entry.Original).ToList(),
                unreadableLines = parsed.Unreadable,
                summary = comparison.Rows.GroupBy(row => row.Status).ToDictionary(group => group.Key.ToString(), group => group.Count()),
                rows = comparison.Rows.Select(row => new
                {
                    customerLine = row.Entry.Original,
                    from = Moment(row.Entry.Window.Start),
                    to = Moment(row.Entry.Window.End),
                    marker = row.Entry.Marker,
                    autoAttendantId = row.AutoAttendantId,
                    autoAttendant = row.AutoAttendantName,
                    status = row.Status.ToString(),
                    schedule = row.Schedule is null ? null : new { id = row.Schedule.Id, name = row.Schedule.Name },
                    scheduleRange = row.ScheduleWindow is null ? null : HolidayScheduleOperations.Format(row.ScheduleWindow),
                    callHandling = row.CallHandling.Length == 0 ? null : row.CallHandling
                }).ToList(),
                onlyInTenant = comparison.OnlyInTenant.Select(range => new
                {
                    autoAttendant = range.AutoAttendantName,
                    schedule = new { id = range.Schedule.Id, name = range.Schedule.Name },
                    range = HolidayScheduleOperations.Format(range.Window)
                }).ToList(),
                markers = HolidayListComparer.MarkerLegend
            };
        });

    [McpServerTool(Name = "holiday_change_table", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Writes the table of holiday changes for the customer record, after the work is done. It compares a backup from before the work with the tenant as it is now, for the customer's list: 'done' only when the tenant did not have the range before and has it now, 'no change' when it was there already, 'pending' when it is still missing. So only changes that a new read of the tenant confirms are reported as done. Each row keeps the customer's wording and the time range, and the marker letters (G, B, R, H, and W for a range that a longer existing schedule covers). The result has the rows and the same table as tab-separated text to paste. The 'before' state is a holiday backup file: take one with backup_holiday_call_handling BEFORE the work, or name the backup a holiday change wrote. Without a file the newest holiday backup of the tenant is used. Read-only.")]
    public Task<string> HolidayChangeTable(
        [Description(ListDescription)] string list,
        [Description(YearDescription)] int defaultYear,
        [Description("Full path of the holiday backup from before the work. Omit to use the newest holiday backup of this tenant.")] string? backupFile = null,
        [Description("IDs of the auto attendants to report. Omit to report every auto attendant of the backup.")] string[]? autoAttendantIds = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var parsed = HolidayListComparer.Parse(list, RequireYear(defaultYear));
            _tenant.RequireReady();
            var file = string.IsNullOrWhiteSpace(backupFile) ? NewestBackup() : Path.GetFullPath(backupFile);
            if (file is null || !File.Exists(file))
            {
                throw new InvalidOperationException(
                    "No holiday backup from before the work was found. Take one with backup_holiday_call_handling before the next change; " +
                    "for work that is done already, name the backup file a holiday change wrote (backupFile in its result).");
            }

            var document = HolidayBackup.Deserialize(await File.ReadAllTextAsync(file, cancellationToken));
            var before = new HolidayCallHandlingState { AutoAttendants = document.AutoAttendants, Schedules = document.Schedules };
            var ids = Ids(autoAttendantIds);
            var scope = ids.Count == 0 ? document.AutoAttendants.Select(attendant => attendant.Id).ToList() : ids;
            if (scope.Count == 0)
            {
                throw new InvalidOperationException("The backup holds no auto attendant.");
            }

            var now = await _reader.ReadAsync(scope, includeAutoAttendants: true, checkAuthorizedUsers: false, cancellationToken);
            var rows = HolidayListComparer.Changes(before, now, parsed, scope);
            return new
            {
                before = new { file, takenUtc = document.RetrievedAtUtc },
                done = rows.Count(row => row.Result == HolidayChangeResult.Done),
                noChange = rows.Count(row => row.Result == HolidayChangeResult.NoChange),
                pending = rows.Count(row => row.Result == HolidayChangeResult.Pending),
                unreadableLines = parsed.Unreadable,
                markers = HolidayListComparer.MarkerLegend,
                rows = rows.Select(row => new
                {
                    result = row.Result.ToString(),
                    markers = row.Markers,
                    from = Moment(row.Entry.Window.Start),
                    to = Moment(row.Entry.Window.End),
                    customerLine = row.Entry.Original,
                    autoAttendant = row.AutoAttendantName,
                    schedule = row.ScheduleName.Length == 0 ? null : row.ScheduleName,
                    callHandling = row.CallHandling.Length == 0 ? null : row.CallHandling
                }).ToList(),
                table = HolidayListComparer.ToTable(rows)
            };
        });

    private string? NewestBackup()
    {
        var directory = _environment.BackupDirectory(_tenant.TenantId);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "phonedesk-holiday-backup-*.json").OrderByDescending(file => file, StringComparer.Ordinal).FirstOrDefault()
            : null;
    }

    private static List<string> Ids(string[]? autoAttendantIds)
        => (autoAttendantIds ?? Array.Empty<string>()).Select(id => HolidayCallHandling.RequireId(id, "an auto attendant")).ToList();

    private static int RequireYear(int year)
        => year is >= 2000 and <= 2100 ? year : throw new ArgumentException($"{year} is not a year for a holiday list. Give the year the list is for, for example 2027.");

    private static string Moment(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
