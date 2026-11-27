using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneDesk.Operations;

/// <summary>One line of a customer's holiday list: when the office is closed, in the customer's own words.</summary>
public sealed class CustomerHolidayEntry
{
    /// <summary>The line as the customer wrote it.</summary>
    public string Original { get; init; } = string.Empty;

    public ScheduleWindow Window { get; init; } = new(default, default);

    public string Wording { get; init; } = string.Empty;

    /// <summary>
    /// G statutory holiday, B business closure, R additional rest day, H half day. Taken from the
    /// start of the line when the customer wrote one, otherwise H for a part of a day and B for
    /// several days. Empty when nothing says what kind of day it is.
    /// </summary>
    public string Marker { get; init; } = string.Empty;

    /// <summary>The line had no year; the year that was assumed is in <see cref="Window"/>.</summary>
    public bool YearWasAssumed { get; init; }
}

/// <summary>A line that could not be read as a date.</summary>
public sealed record UnreadableLine(int LineNumber, string Text, string Reason);

public sealed class CustomerHolidayList
{
    public IReadOnlyList<CustomerHolidayEntry> Entries { get; init; } = Array.Empty<CustomerHolidayEntry>();

    public IReadOnlyList<UnreadableLine> Unreadable { get; init; } = Array.Empty<UnreadableLine>();

    /// <summary>The years the list spans. More than one year in one list is worth a look.</summary>
    public IReadOnlyList<int> Years => Entries.Select(entry => entry.Window.Start.Year).Distinct().OrderBy(year => year).ToList();
}

public enum HolidayListStatus
{
    /// <summary>A schedule of the auto attendant has exactly this date range.</summary>
    Match,

    /// <summary>A longer date range of a schedule of the auto attendant contains this one.</summary>
    Covered,

    /// <summary>A schedule starts at the same time but ends at another: Teams refuses a second holiday with this start.</summary>
    SameStart,

    /// <summary>A schedule overlaps a part of this date range.</summary>
    PartialOverlap,

    /// <summary>No holiday of the auto attendant applies in this date range.</summary>
    Missing
}

/// <summary>How one line of the customer's list compares with one auto attendant.</summary>
public sealed class HolidayListRow
{
    public CustomerHolidayEntry Entry { get; init; } = new();

    public string AutoAttendantId { get; init; } = string.Empty;

    public string AutoAttendantName { get; init; } = string.Empty;

    public HolidayListStatus Status { get; init; }

    /// <summary>The schedule that matches, covers or overlaps, with its range.</summary>
    public ScheduleInfo? Schedule { get; init; }

    public ScheduleWindow? ScheduleWindow { get; init; }

    /// <summary>What callers get on that schedule on this auto attendant.</summary>
    public string CallHandling { get; init; } = string.Empty;
}

/// <summary>A date range the tenant has in the period of the list that the customer did not name.</summary>
public sealed record TenantOnlyRange(string AutoAttendantId, string AutoAttendantName, ScheduleInfo Schedule, ScheduleWindow Window);

public sealed class HolidayListComparison
{
    public IReadOnlyList<HolidayListRow> Rows { get; init; } = Array.Empty<HolidayListRow>();

    public IReadOnlyList<TenantOnlyRange> OnlyInTenant { get; init; } = Array.Empty<TenantOnlyRange>();
}

public enum HolidayChangeResult
{
    /// <summary>The tenant did not have the range before and has it now.</summary>
    Done,

    /// <summary>The tenant had the range before and still has it.</summary>
    NoChange,

    /// <summary>The tenant does not have the range.</summary>
    Pending
}

public sealed class HolidayChangeRow
{
    public CustomerHolidayEntry Entry { get; init; } = new();

    public string AutoAttendantName { get; init; } = string.Empty;

    public HolidayChangeResult Result { get; init; }

    public HolidayListStatus Before { get; init; }

    public HolidayListStatus Now { get; init; }

    public string ScheduleName { get; init; } = string.Empty;

    public string CallHandling { get; init; } = string.Empty;

    /// <summary>The markers of the line, with W when an existing longer schedule covers the range.</summary>
    public string Markers { get; init; } = string.Empty;
}

/// <summary>
/// Compares a customer's holiday list with what the tenant holds, per auto attendant and
/// whatever the schedules are called, and writes the table of changes for the customer record:
/// only what a new read of the tenant confirms counts as done.
/// </summary>
public static partial class HolidayListComparer
{
    private static readonly string[] DateFormats = { "yyyy-MM-dd", "d.M.yyyy", "d.M.yy", "d/M/yyyy" };
    private static readonly string[] TimeFormats = { "H:mm", "HH:mm", "H.mm", "HH.mm" };

    /// <summary>
    /// Reads a list of one entry per line. An entry starts with an optional marker (G, B, R or H),
    /// then a day or a range, then the customer's wording:
    /// "25.12.2027 Weihnachten", "24.12.2027 12:00-24:00 Heiligabend", "G 01.08. Bundesfeier",
    /// "2027-12-27 to 2027-12-31 Betriebsferien". A day without a year takes
    /// <paramref name="defaultYear"/> and is marked, so the reader can check it.
    /// </summary>
    public static CustomerHolidayList Parse(string? text, int defaultYear)
    {
        var entries = new List<CustomerHolidayEntry>();
        var unreadable = new List<UnreadableLine>();
        var number = 0;
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            number++;
            var line = raw.Trim().Trim('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var match = EntryPattern().Match(line);
            if (!match.Success)
            {
                unreadable.Add(new UnreadableLine(number, line, "The line does not start with a date."));
                continue;
            }

            try
            {
                var assumed = false;
                var startDay = ReadDay(match.Groups["d1"].Value, defaultYear, ref assumed);
                var hasEndDay = match.Groups["d2"].Success && match.Groups["d2"].Value.Length > 0;
                var endDay = hasEndDay ? ReadDay(match.Groups["d2"].Value, startDay.Year, ref assumed) : startDay;
                var hasStartTime = match.Groups["t1"].Success && match.Groups["t1"].Value.Length > 0;
                var hasEndTime = match.Groups["t2"].Success && match.Groups["t2"].Value.Length > 0;
                var start = hasStartTime ? startDay + ReadTime(match.Groups["t1"].Value) : startDay;
                DateTime end;
                if (hasEndTime)
                {
                    // 24:00 and 00:00 as an end mean the end of the day.
                    var time = ReadTime(match.Groups["t2"].Value);
                    end = time == TimeSpan.Zero || time == TimeSpan.FromHours(24) ? endDay.AddDays(1) : endDay + time;
                }
                else
                {
                    end = endDay.AddDays(1);
                }

                if (end <= start)
                {
                    // A range without years that crosses the turn of the year: 27.12. to 02.01.
                    if (hasEndDay && assumed && endDay < startDay)
                    {
                        end = end.AddYears(1);
                    }
                    else
                    {
                        unreadable.Add(new UnreadableLine(number, line, "The range ends before it starts."));
                        continue;
                    }
                }

                var window = new ScheduleWindow(start, end);
                var marker = match.Groups["m"].Success ? match.Groups["m"].Value.ToUpperInvariant() : string.Empty;
                if (marker.Length == 0)
                {
                    marker = IsPartOfADay(window) ? "H" : end - start > TimeSpan.FromDays(1) ? "B" : string.Empty;
                }

                entries.Add(new CustomerHolidayEntry
                {
                    Original = line,
                    Window = window,
                    Wording = match.Groups["w"].Value.Trim(),
                    Marker = marker,
                    YearWasAssumed = assumed
                });
            }
            catch (FormatException ex)
            {
                unreadable.Add(new UnreadableLine(number, line, ex.Message));
            }
        }

        return new CustomerHolidayList { Entries = entries, Unreadable = unreadable };
    }

    /// <summary>
    /// Compares every entry with every holiday schedule that is attached to each of the auto
    /// attendants. The name of a schedule plays no part.
    /// </summary>
    public static HolidayListComparison Compare(
        HolidayCallHandlingState state, CustomerHolidayList list, IReadOnlyCollection<string>? autoAttendantIds = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(list);
        var attendants = state.AutoAttendants
            .Where(attendant => autoAttendantIds is null || autoAttendantIds.Count == 0 ||
                                autoAttendantIds.Any(id => HolidayCallHandling.SameId(id, attendant.Id)))
            .OrderBy(attendant => attendant.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = new List<HolidayListRow>();
        var onlyInTenant = new List<TenantOnlyRange>();
        foreach (var attendant in attendants)
        {
            var ranges = (from link in attendant.HolidayLinks
                          let schedule = state.FindSchedule(link.ScheduleId)
                          where schedule is not null
                          from window in schedule.Windows
                          select (Schedule: schedule, Window: window, Flow: attendant.FindCallFlow(link.CallFlowId))).ToList();
            foreach (var entry in list.Entries)
            {
                var found = Find(entry.Window, ranges.Select(range => (range.Schedule, range.Window)).ToList());
                var flow = found.Schedule is null ? null : ranges.First(range => ReferenceEquals(range.Schedule, found.Schedule)).Flow;
                rows.Add(new HolidayListRow
                {
                    Entry = entry,
                    AutoAttendantId = attendant.Id,
                    AutoAttendantName = attendant.Name,
                    Status = found.Status,
                    Schedule = found.Schedule,
                    ScheduleWindow = found.Window,
                    CallHandling = found.Schedule is null ? string.Empty : HolidayCallHandling.Describe(flow)
                });
            }

            if (list.Entries.Count > 0)
            {
                // What the tenant has in the period of the list and the customer did not name.
                var from = list.Entries.Min(entry => entry.Window.Start);
                var to = list.Entries.Max(entry => entry.Window.End);
                onlyInTenant.AddRange(ranges
                    .Where(range => range.Window.Start < to && from < range.Window.End)
                    .Where(range => !list.Entries.Any(entry => entry.Window.Overlaps(range.Window)))
                    .Select(range => new TenantOnlyRange(attendant.Id, attendant.Name, range.Schedule, range.Window)));
            }
        }

        return new HolidayListComparison { Rows = rows, OnlyInTenant = onlyInTenant };
    }

    /// <summary>
    /// The changes since <paramref name="before"/>: an entry counts as done only when the tenant did
    /// not have it then and has it now. What is still missing is pending, and what was there
    /// already is no change.
    /// </summary>
    public static IReadOnlyList<HolidayChangeRow> Changes(
        HolidayCallHandlingState before,
        HolidayCallHandlingState now,
        CustomerHolidayList list,
        IReadOnlyCollection<string>? autoAttendantIds = null)
    {
        var earlier = Compare(before, list, autoAttendantIds).Rows
            .ToDictionary(row => (row.AutoAttendantId.ToUpperInvariant(), row.Entry.Original, row.Entry.Window));
        var rows = new List<HolidayChangeRow>();
        foreach (var row in Compare(now, list, autoAttendantIds).Rows)
        {
            var was = earlier.TryGetValue((row.AutoAttendantId.ToUpperInvariant(), row.Entry.Original, row.Entry.Window), out var old)
                ? old.Status
                : HolidayListStatus.Missing;
            var present = row.Status is HolidayListStatus.Match or HolidayListStatus.Covered;
            var wasPresent = was is HolidayListStatus.Match or HolidayListStatus.Covered;
            rows.Add(new HolidayChangeRow
            {
                Entry = row.Entry,
                AutoAttendantName = row.AutoAttendantName,
                Before = was,
                Now = row.Status,
                Result = !present ? HolidayChangeResult.Pending : wasPresent ? HolidayChangeResult.NoChange : HolidayChangeResult.Done,
                ScheduleName = row.Schedule?.Name ?? string.Empty,
                CallHandling = row.CallHandling,
                Markers = row.Entry.Marker + (row.Status == HolidayListStatus.Covered ? "W" : string.Empty)
            });
        }

        return rows;
    }

    /// <summary>
    /// The change table as tab-separated text with a header line, for a customer record. The
    /// customer's wording is kept as it was written.
    /// </summary>
    public static string ToTable(IEnumerable<HolidayChangeRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        text.Append("Result\tMarkers\tFrom\tTo\tCustomer wording\tAuto attendant\tSchedule\tCall handling\n");
        foreach (var row in rows)
        {
            text.Append(row.Result switch
                {
                    HolidayChangeResult.Done => "done",
                    HolidayChangeResult.NoChange => "no change",
                    _ => "pending"
                }).Append('\t')
                .Append(row.Markers).Append('\t')
                .Append(Moment(row.Entry.Window.Start)).Append('\t')
                .Append(Moment(row.Entry.Window.End)).Append('\t')
                .Append(Cell(row.Entry.Original)).Append('\t')
                .Append(Cell(row.AutoAttendantName)).Append('\t')
                .Append(Cell(row.ScheduleName)).Append('\t')
                .Append(Cell(row.CallHandling)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The legend of the marker letters.</summary>
    public const string MarkerLegend =
        "G statutory holiday, B business closure, R additional rest day, H half day, W covered by a longer schedule that exists.";

    private static (HolidayListStatus Status, ScheduleInfo? Schedule, ScheduleWindow? Window) Find(
        ScheduleWindow wanted, IReadOnlyList<(ScheduleInfo Schedule, ScheduleWindow Window)> ranges)
    {
        foreach (var range in ranges.Where(range => range.Window == wanted))
        {
            return (HolidayListStatus.Match, range.Schedule, range.Window);
        }

        foreach (var range in ranges.Where(range => range.Window.Start <= wanted.Start && wanted.End <= range.Window.End))
        {
            return (HolidayListStatus.Covered, range.Schedule, range.Window);
        }

        foreach (var range in ranges.Where(range => range.Window.Start == wanted.Start))
        {
            return (HolidayListStatus.SameStart, range.Schedule, range.Window);
        }

        foreach (var range in ranges.Where(range => range.Window.Overlaps(wanted)))
        {
            return (HolidayListStatus.PartialOverlap, range.Schedule, range.Window);
        }

        return (HolidayListStatus.Missing, null, null);
    }

    private static bool IsPartOfADay(ScheduleWindow window)
        => window.End - window.Start < TimeSpan.FromDays(1);

    private static DateTime ReadDay(string text, int defaultYear, ref bool assumed)
    {
        var value = text.Trim();
        if (DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return day;
        }

        // A day without a year: 25.12. or 25.12
        var parts = value.TrimEnd('.').Split('.');
        if (parts.Length == 2 && int.TryParse(parts[0], out var dayOfMonth) && int.TryParse(parts[1], out var month) &&
            month is >= 1 and <= 12 && dayOfMonth >= 1 && dayOfMonth <= DateTime.DaysInMonth(defaultYear, month))
        {
            assumed = true;
            return new DateTime(defaultYear, month, dayOfMonth);
        }

        throw new FormatException($"'{text}' is not a date.");
    }

    private static TimeSpan ReadTime(string text)
    {
        var value = text.Trim();
        if (value is "24:00" or "24.00")
        {
            return TimeSpan.FromHours(24);
        }

        return DateTime.TryParseExact(value, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.TimeOfDay
            : throw new FormatException($"'{text}' is not a time.");
    }

    private static string Moment(DateTime value) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Cell(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    // marker, day, optional time, optional end (day and/or time), wording
    [GeneratedRegex(
        @"^(?:(?<m>[GBRHgbrh])\s+)?" +
        @"(?<d1>\d{4}-\d{2}-\d{2}|\d{1,2}[./]\d{1,2}[./]\d{2,4}|\d{1,2}\.\d{1,2}\.?)" +
        @"(?:\s+(?<t1>\d{1,2}[:.]\d{2}))?" +
        @"(?:\s*(?:to|bis|-|–)\s*" +
        @"(?<d2>\d{4}-\d{2}-\d{2}|\d{1,2}[./]\d{1,2}[./]\d{2,4}|\d{1,2}\.\d{1,2}\.?(?![:\d]))?" +
        @"\s*(?<t2>\d{1,2}[:.]\d{2})?)?" +
        @"\s*(?<w>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EntryPattern();
}
