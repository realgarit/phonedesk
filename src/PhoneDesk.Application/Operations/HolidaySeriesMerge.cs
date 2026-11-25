using PhoneDesk.Models;
using System.Collections.Generic;
using System.Linq;

namespace PhoneDesk.Operations
{
    /// <summary>
    /// Adds suggested holidays (for example the public holidays of a canton) to the holiday entries
    /// of the configuration. Both heads use it, so the desktop picker and the MCP server agree on
    /// when two entries are the same.
    /// </summary>
    public static class HolidaySeriesMerge
    {
        /// <summary>Two entries are the same when date, time, name and end are equal.</summary>
        public static bool IsSameEntry(IHolidayEntry first, IHolidayEntry second)
            => first.Date == second.Date && first.Time == second.Time && first.Name == second.Name
               && first.EndDate == second.EndDate && first.EndTime == second.EndTime;

        /// <summary>
        /// The candidates that are not in <paramref name="existing"/> yet, in their order and
        /// without repeats. Applying the same selection twice adds nothing the second time.
        /// </summary>
        public static IReadOnlyList<T> MissingFrom<T>(IEnumerable<IHolidayEntry> existing, IEnumerable<T> candidates)
            where T : IHolidayEntry
        {
            var known = existing.ToList();
            var missing = new List<T>();
            foreach (var candidate in candidates)
            {
                if (known.Any(entry => IsSameEntry(entry, candidate)))
                {
                    continue;
                }

                known.Add(candidate);
                missing.Add(candidate);
            }

            return missing;
        }
    }
}
