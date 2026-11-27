using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PhoneDesk.Models;

namespace PhoneDesk.Holidays
{
    /// <summary>One event of a calendar file, as a holiday entry, with what the reader noticed about it.</summary>
    public sealed record IcsHoliday(HolidayDate Entry, string Note)
    {
        /// <summary>The event repeats; only its first date was read.</summary>
        public bool Repeats { get; init; }

        /// <summary>The times are written in UTC and were kept as they are.</summary>
        public bool IsUtc { get; init; }
    }

    public sealed record IcsImportResult(IReadOnlyList<IcsHoliday> Holidays, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Reads the events of an iCalendar file (RFC 5545) as holiday entries. An all-day event becomes
    /// a whole day or a range of days; a timed event keeps its times as they are written. The reader
    /// does not expand a repeating event: it takes the first date and says so, because a holiday
    /// calendar lists each year on its own.
    /// </summary>
    public static class IcsHolidayImport
    {
        public const int MaximumEvents = 500;

        public static IcsImportResult Parse(string? content, int? year = null)
        {
            var holidays = new List<IcsHoliday>();
            var skipped = new List<string>();
            Dictionary<string, (string Parameters, string Value)>? current = null;
            foreach (var line in Unfold(content ?? string.Empty))
            {
                if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    current = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    if (current is not null)
                    {
                        Add(current, year, holidays, skipped);
                    }

                    current = null;
                    continue;
                }

                if (current is null)
                {
                    continue;
                }

                var colon = IndexOfValue(line);
                if (colon <= 0)
                {
                    continue;
                }

                var head = line[..colon];
                var semicolon = head.IndexOf(';', StringComparison.Ordinal);
                var name = semicolon < 0 ? head : head[..semicolon];
                current.TryAdd(name, (semicolon < 0 ? string.Empty : head[(semicolon + 1)..], line[(colon + 1)..]));
            }

            if (holidays.Count > MaximumEvents)
            {
                skipped.Add($"The file has more than {MaximumEvents} events; the first {MaximumEvents} were read.");
                holidays = holidays.Take(MaximumEvents).ToList();
            }

            return new IcsImportResult(
                holidays.OrderBy(holiday => holiday.Entry.DateTime).ThenBy(holiday => holiday.Entry.Name, StringComparer.Ordinal).ToList(),
                skipped);
        }

        private static void Add(
            Dictionary<string, (string Parameters, string Value)> properties, int? year, List<IcsHoliday> holidays, List<string> skipped)
        {
            var summary = properties.TryGetValue("SUMMARY", out var text) ? Unescape(text.Value).Trim() : string.Empty;
            var label = summary.Length == 0 ? "(no title)" : summary;
            if (properties.TryGetValue("STATUS", out var status) && status.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add($"{label}: the event is cancelled.");
                return;
            }

            if (!properties.TryGetValue("DTSTART", out var start) || !TryRead(start, out var from, out var allDay, out var utc))
            {
                skipped.Add($"{label}: the event has no start date that could be read.");
                return;
            }

            var notes = new List<string>();
            DateTime? to = null;
            if (properties.TryGetValue("DTEND", out var end) && TryRead(end, out var until, out _, out _))
            {
                to = until;
            }

            if (to is not null && to <= from)
            {
                skipped.Add($"{label}: the event ends before it starts.");
                return;
            }

            var repeats = properties.ContainsKey("RRULE") || properties.ContainsKey("RDATE");
            if (repeats)
            {
                notes.Add("The event repeats; only its first date was read.");
            }

            if (utc)
            {
                notes.Add("The times are written in UTC and were kept as they are; correct them for the time zone of the auto attendant.");
            }

            if (year is not null && from.Year != year && (to ?? from).Year != year)
            {
                return;
            }

            HolidayDate entry;
            if (allDay)
            {
                // The end of an all-day event is the day after its last day, at 00:00: the same
                // form a range of whole days has everywhere else in the application.
                var dayAfter = (to ?? from.AddDays(1)).Date;
                entry = dayAfter > from.Date.AddDays(1)
                    ? new HolidayDate(from.Date, TimeSpan.Zero, summary, dayAfter, TimeSpan.Zero)
                    : new HolidayDate(from.Date, TimeSpan.Zero, summary);
            }
            else
            {
                var finish = to ?? from.AddHours(1);
                if (to is null)
                {
                    notes.Add("The event has no end; one hour was taken.");
                }

                entry = new HolidayDate(from.Date, from.TimeOfDay, summary, finish.Date, finish.TimeOfDay);
            }

            if (summary.Length == 0)
            {
                notes.Add("The event has no title; give the entry a name.");
            }

            holidays.Add(new IcsHoliday(entry, string.Join(" ", notes)) { Repeats = repeats, IsUtc = utc && !allDay });
        }

        /// <summary>Reads DATE (yyyyMMdd) and DATE-TIME (yyyyMMddTHHmmss, with or without Z).</summary>
        private static bool TryRead((string Parameters, string Value) property, out DateTime value, out bool allDay, out bool utc)
        {
            var text = property.Value.Trim();
            utc = text.EndsWith('Z');
            allDay = false;
            if (DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            {
                allDay = true;
                return true;
            }

            return DateTime.TryParseExact(
                text.TrimEnd('Z'),
                new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value);
        }

        /// <summary>Joins folded lines: a line that starts with a space or a tab continues the line before.</summary>
        private static IEnumerable<string> Unfold(string content)
        {
            var builder = new StringBuilder();
            var any = false;
            foreach (var raw in content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
            {
                if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
                {
                    builder.Append(raw, 1, raw.Length - 1);
                    continue;
                }

                if (any)
                {
                    yield return builder.ToString();
                }

                builder.Clear();
                builder.Append(raw);
                any = true;
            }

            if (any)
            {
                yield return builder.ToString();
            }
        }

        /// <summary>The colon that ends the name and its parameters. A colon inside a quoted parameter does not count.</summary>
        private static int IndexOfValue(string line)
        {
            var quoted = false;
            for (var index = 0; index < line.Length; index++)
            {
                if (line[index] == '"')
                {
                    quoted = !quoted;
                }
                else if (line[index] == ':' && !quoted)
                {
                    return index;
                }
            }

            return -1;
        }

        private static string Unescape(string value)
            => value
                .Replace("\\n", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("\\,", ",", StringComparison.Ordinal)
                .Replace("\\;", ";", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
    }
}
