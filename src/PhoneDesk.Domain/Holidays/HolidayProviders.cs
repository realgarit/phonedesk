using System;
using System.Collections.Generic;
using System.Linq;
using PhoneDesk.Models;

namespace PhoneDesk.Holidays
{
    /// <summary>A part of a country with its own public holidays.</summary>
    public sealed record HolidayRegion(string Code, string Name);

    /// <summary>
    /// The public holidays of one country. A provider computes the dates of a year from rules; it
    /// reads nothing and keeps no state.
    /// </summary>
    public interface IHolidayProvider
    {
        /// <summary>The ISO 3166 code of the country, for example DE.</summary>
        string CountryCode { get; }

        string CountryName { get; }

        /// <summary>The regions with own holidays. Empty when the whole country has one list.</summary>
        IReadOnlyList<HolidayRegion> Regions { get; }

        /// <summary>True when a list cannot be given without a region.</summary>
        bool RegionRequired { get; }

        /// <summary>What the list does not cover, for the person who uses it.</summary>
        string Note { get; }

        /// <summary>The holidays of a year, sorted by date. Throws for a region the country does not have.</summary>
        IReadOnlyList<HolidayDate> GetHolidays(int year, string? regionCode);
    }

    /// <summary>The holiday providers the application has. Both heads list and use them through this class.</summary>
    public static class HolidayProviders
    {
        public const int FirstYear = 2025;
        public const int LastYear = 2030;

        public static IReadOnlyList<IHolidayProvider> All { get; } = new IHolidayProvider[]
        {
            new SwissHolidayProvider(),
            new GermanHolidayProvider(),
            new AustrianHolidayProvider(),
            new FrenchHolidayProvider(),
            new UnitedKingdomHolidayProvider(),
            new UnitedStatesHolidayProvider()
        };

        /// <summary>Finds a provider by country code or name. Null when there is none.</summary>
        public static IHolidayProvider? Find(string? country)
        {
            var text = (country ?? string.Empty).Trim();
            return All.FirstOrDefault(provider =>
                string.Equals(provider.CountryCode, text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(provider.CountryName, text, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The region of a provider by code or name. Null when there is none.</summary>
        public static HolidayRegion? FindRegion(IHolidayProvider provider, string? region)
        {
            var text = (region ?? string.Empty).Trim();
            return text.Length == 0
                ? null
                : provider.Regions.FirstOrDefault(candidate =>
                    string.Equals(candidate.Code, text, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.Name, text, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Shared date rules of the providers.</summary>
    internal abstract class HolidayProviderBase : IHolidayProvider
    {
        public abstract string CountryCode { get; }

        public abstract string CountryName { get; }

        public virtual IReadOnlyList<HolidayRegion> Regions => Array.Empty<HolidayRegion>();

        public virtual bool RegionRequired => false;

        public abstract string Note { get; }

        public IReadOnlyList<HolidayDate> GetHolidays(int year, string? regionCode)
        {
            if (year < 1900 || year > 2200)
            {
                throw new ArgumentOutOfRangeException(nameof(year), year, "The year is outside the range the holiday rules cover.");
            }

            var region = HolidayProviders.FindRegion(this, regionCode);
            if (region is null && !string.IsNullOrWhiteSpace(regionCode))
            {
                throw new ArgumentOutOfRangeException(nameof(regionCode), regionCode, $"{CountryName} has no holiday region '{regionCode}'.");
            }

            if (region is null && RegionRequired)
            {
                throw new ArgumentOutOfRangeException(nameof(regionCode), regionCode, $"The holidays of {CountryName} depend on the region.");
            }

            return Compute(year, region?.Code)
                .GroupBy(holiday => (holiday.Date, holiday.Name))
                .Select(group => group.First())
                .OrderBy(holiday => holiday.Date)
                .ThenBy(holiday => holiday.Name, StringComparer.Ordinal)
                .ToList();
        }

        protected abstract IEnumerable<HolidayDate> Compute(int year, string? region);

        protected static HolidayDate Day(DateTime date, string name, HolidayCategory category = HolidayCategory.PublicHoliday)
            => new(date, TimeSpan.Zero, name, null, null, category);

        protected static HolidayDate Day(int year, int month, int day, string name)
            => Day(new DateTime(year, month, day), name);

        protected static DateTime Easter(int year) => PhoneDesk.Services.Holidays.SwissHolidayFormulaEvaluator.EasterSunday(year);

        /// <summary>The n-th given weekday of a month, counted from 1.</summary>
        protected static DateTime Nth(int year, int month, DayOfWeek day, int n)
        {
            var date = new DateTime(year, month, 1);
            while (date.DayOfWeek != day)
            {
                date = date.AddDays(1);
            }

            return date.AddDays(7 * (n - 1));
        }

        /// <summary>The last given weekday of a month.</summary>
        protected static DateTime Last(int year, int month, DayOfWeek day)
        {
            var date = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            while (date.DayOfWeek != day)
            {
                date = date.AddDays(-1);
            }

            return date;
        }

        protected static bool IsWeekend(DateTime date)
            => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }

    /// <summary>Switzerland: the existing catalog, unchanged. The region is a canton, or canton/region for AG, FR and SO.</summary>
    internal sealed class SwissHolidayProvider : IHolidayProvider
    {
        public string CountryCode => "CH";

        public string CountryName => "Switzerland";

        public IReadOnlyList<HolidayRegion> Regions { get; } = Enum.GetValues<SwissCanton>()
            .Select(canton => new HolidayRegion(canton.ToString(), canton.ToString()))
            .ToList();

        public bool RegionRequired => true;

        public string Note =>
            "The region is the canton. In Aargau, Fribourg and Solothurn the holidays also depend on the district or the denomination of the municipality: " +
            "give it after the canton, for example FR/fr-catholic. Additional cantonal rest days are marked; they are not statutory holidays.";

        public IReadOnlyList<HolidayDate> GetHolidays(int year, string? regionCode)
        {
            var parts = (regionCode ?? string.Empty).Split('/', 2, StringSplitOptions.TrimEntries);
            if (!Enum.TryParse<SwissCanton>(parts[0], ignoreCase: true, out var canton) || !Enum.IsDefined(canton))
            {
                throw new ArgumentOutOfRangeException(nameof(regionCode), regionCode, "The holidays of Switzerland depend on the canton.");
            }

            return SwissHolidayCatalog.Compute(canton, parts.Length > 1 ? parts[1] : null, year).Holidays;
        }
    }

    /// <summary>
    /// Germany: the statutory holidays of the sixteen states (Feiertagsgesetze der Länder; the Day
    /// of German Unity by the Unification Treaty). Holidays that apply to a part of a state have
    /// their own region.
    /// </summary>
    internal sealed class GermanHolidayProvider : HolidayProviderBase
    {
        public override string CountryCode => "DE";

        public override string CountryName => "Germany";

        public override bool RegionRequired => true;

        public override IReadOnlyList<HolidayRegion> Regions { get; } = new HolidayRegion[]
        {
            new("BW", "Baden-Württemberg"),
            new("BY", "Bayern (Gemeinden mit überwiegend evangelischer Bevölkerung)"),
            new("BY-KATH", "Bayern (Gemeinden mit überwiegend katholischer Bevölkerung: mit Mariä Himmelfahrt)"),
            new("BY-AUGSBURG", "Bayern (Stadt Augsburg: mit Friedensfest)"),
            new("BE", "Berlin"),
            new("BB", "Brandenburg"),
            new("HB", "Bremen"),
            new("HH", "Hamburg"),
            new("HE", "Hessen"),
            new("MV", "Mecklenburg-Vorpommern"),
            new("NI", "Niedersachsen"),
            new("NW", "Nordrhein-Westfalen"),
            new("RP", "Rheinland-Pfalz"),
            new("SL", "Saarland"),
            new("SN", "Sachsen"),
            new("SN-FRONLEICHNAM", "Sachsen (katholisch geprägte Gemeinden im Landkreis Bautzen: mit Fronleichnam)"),
            new("ST", "Sachsen-Anhalt"),
            new("SH", "Schleswig-Holstein"),
            new("TH", "Thüringen"),
            new("TH-FRONLEICHNAM", "Thüringen (Landkreis Eichsfeld und weitere katholisch geprägte Gemeinden: mit Fronleichnam)")
        };

        public override string Note =>
            "Statutory holidays of the state. In Bayern, Sachsen and Thüringen some holidays apply to a part of the state only: choose the region of the workplace. " +
            "Easter Sunday and Whit Sunday are holidays by law in Brandenburg only and are listed there. Days that are not statutory, such as 24 and 31 December or carnival, are not in the list.";

        protected override IEnumerable<HolidayDate> Compute(int year, string? region)
        {
            var code = region ?? string.Empty;
            var state = code.Split('-')[0];
            var easter = Easter(year);
            bool In(params string[] states) => states.Contains(state, StringComparer.Ordinal);

            yield return Day(year, 1, 1, "Neujahr");
            if (In("BW", "BY", "ST"))
            {
                yield return Day(year, 1, 6, "Heilige Drei Könige");
            }

            if (In("BE", "MV"))
            {
                yield return Day(year, 3, 8, "Internationaler Frauentag");
            }

            yield return Day(easter.AddDays(-2), "Karfreitag");
            if (In("BB"))
            {
                yield return Day(easter, "Ostersonntag");
            }

            yield return Day(easter.AddDays(1), "Ostermontag");
            yield return Day(year, 5, 1, "Tag der Arbeit");
            yield return Day(easter.AddDays(39), "Christi Himmelfahrt");
            if (In("BB"))
            {
                yield return Day(easter.AddDays(49), "Pfingstsonntag");
            }

            yield return Day(easter.AddDays(50), "Pfingstmontag");
            if (In("BW", "BY", "HE", "NW", "RP", "SL") || code is "SN-FRONLEICHNAM" or "TH-FRONLEICHNAM")
            {
                yield return Day(easter.AddDays(60), "Fronleichnam");
            }

            if (code == "BY-AUGSBURG")
            {
                yield return Day(year, 8, 8, "Augsburger Friedensfest");
            }

            if (In("SL") || code is "BY-KATH" or "BY-AUGSBURG")
            {
                yield return Day(year, 8, 15, "Mariä Himmelfahrt");
            }

            if (In("TH"))
            {
                yield return Day(year, 9, 20, "Weltkindertag");
            }

            yield return Day(year, 10, 3, "Tag der Deutschen Einheit");
            if (In("BB", "HB", "HH", "MV", "NI", "SN", "ST", "SH", "TH"))
            {
                yield return Day(year, 10, 31, "Reformationstag");
            }

            if (In("BW", "BY", "NW", "RP", "SL"))
            {
                yield return Day(year, 11, 1, "Allerheiligen");
            }

            if (In("SN"))
            {
                // The Wednesday before 23 November.
                var day = new DateTime(year, 11, 22);
                while (day.DayOfWeek != DayOfWeek.Wednesday)
                {
                    day = day.AddDays(-1);
                }

                yield return Day(day, "Buß- und Bettag");
            }

            yield return Day(year, 12, 25, "1. Weihnachtstag");
            yield return Day(year, 12, 26, "2. Weihnachtstag");
        }
    }

    /// <summary>Austria: the holidays of the Feiertagsruhegesetz, the same in all states.</summary>
    internal sealed class AustrianHolidayProvider : HolidayProviderBase
    {
        public override string CountryCode => "AT";

        public override string CountryName => "Austria";

        public override string Note =>
            "The thirteen statutory holidays, the same in all states. The patron saint days of the states (for example 15 November in Wien and Niederösterreich) " +
            "are not statutory holidays for businesses and are not in the list; add them when the customer is closed.";

        protected override IEnumerable<HolidayDate> Compute(int year, string? region)
        {
            var easter = Easter(year);
            yield return Day(year, 1, 1, "Neujahr");
            yield return Day(year, 1, 6, "Heilige Drei Könige");
            yield return Day(easter.AddDays(1), "Ostermontag");
            yield return Day(year, 5, 1, "Staatsfeiertag");
            yield return Day(easter.AddDays(39), "Christi Himmelfahrt");
            yield return Day(easter.AddDays(50), "Pfingstmontag");
            yield return Day(easter.AddDays(60), "Fronleichnam");
            yield return Day(year, 8, 15, "Mariä Himmelfahrt");
            yield return Day(year, 10, 26, "Nationalfeiertag");
            yield return Day(year, 11, 1, "Allerheiligen");
            yield return Day(year, 12, 8, "Mariä Empfängnis");
            yield return Day(year, 12, 25, "Christtag");
            yield return Day(year, 12, 26, "Stefanitag");
        }
    }

    /// <summary>France: the holidays of Code du travail L3133-1, and the two more of Alsace and Moselle (L3134-13).</summary>
    internal sealed class FrenchHolidayProvider : HolidayProviderBase
    {
        public override string CountryCode => "FR";

        public override string CountryName => "France";

        public override IReadOnlyList<HolidayRegion> Regions { get; } = new HolidayRegion[]
        {
            new("METROPOLE", "France métropolitaine"),
            new("ALSACE-MOSELLE", "Alsace-Moselle (Bas-Rhin, Haut-Rhin, Moselle)")
        };

        public override string Note =>
            "The eleven legal holidays of metropolitan France. Alsace-Moselle also has Good Friday and 26 December. " +
            "The overseas departments have further days (abolition of slavery) that are not in the list.";

        protected override IEnumerable<HolidayDate> Compute(int year, string? region)
        {
            var easter = Easter(year);
            yield return Day(year, 1, 1, "Jour de l'An");
            if (region == "ALSACE-MOSELLE")
            {
                yield return Day(easter.AddDays(-2), "Vendredi saint");
            }

            yield return Day(easter.AddDays(1), "Lundi de Pâques");
            yield return Day(year, 5, 1, "Fête du Travail");
            yield return Day(year, 5, 8, "Victoire 1945");
            yield return Day(easter.AddDays(39), "Ascension");
            yield return Day(easter.AddDays(50), "Lundi de Pentecôte");
            yield return Day(year, 7, 14, "Fête nationale");
            yield return Day(year, 8, 15, "Assomption");
            yield return Day(year, 11, 1, "Toussaint");
            yield return Day(year, 11, 11, "Armistice 1918");
            yield return Day(year, 12, 25, "Noël");
            if (region == "ALSACE-MOSELLE")
            {
                yield return Day(year, 12, 26, "Saint-Étienne");
            }
        }
    }

    /// <summary>
    /// United Kingdom: the bank holidays of the three jurisdictions (Banking and Financial Dealings
    /// Act 1971 and the yearly proclamations). A holiday on a weekend has a substitute day on the
    /// next working day; both dates are listed.
    /// </summary>
    internal sealed class UnitedKingdomHolidayProvider : HolidayProviderBase
    {
        public override string CountryCode => "GB";

        public override string CountryName => "United Kingdom";

        public override bool RegionRequired => true;

        public override IReadOnlyList<HolidayRegion> Regions { get; } = new HolidayRegion[]
        {
            new("ENG-WLS", "England and Wales"),
            new("SCT", "Scotland"),
            new("NIR", "Northern Ireland")
        };

        public override string Note =>
            "Bank holidays. When a holiday falls on a weekend, the next working day is the substitute day; the list has both dates. " +
            "A bank holiday for a single occasion (a coronation or a jubilee) is proclaimed for its year and is not in the list. " +
            "In Scotland local holidays differ from town to town and are not in the list.";

        protected override IEnumerable<HolidayDate> Compute(int year, string? region)
        {
            var easter = Easter(year);
            var fixedDays = new List<(DateTime Date, string Name)> { (new DateTime(year, 1, 1), "New Year's Day") };
            var moving = new List<HolidayDate> { Day(easter.AddDays(-2), "Good Friday") };
            if (region == "SCT")
            {
                fixedDays.Add((new DateTime(year, 1, 2), "2 January"));
                fixedDays.Add((new DateTime(year, 11, 30), "St Andrew's Day"));
            }
            else
            {
                moving.Add(Day(easter.AddDays(1), "Easter Monday"));
            }

            if (region == "NIR")
            {
                fixedDays.Add((new DateTime(year, 3, 17), "St Patrick's Day"));
                fixedDays.Add((new DateTime(year, 7, 12), "Battle of the Boyne (Orangemen's Day)"));
            }

            fixedDays.Add((new DateTime(year, 12, 25), "Christmas Day"));
            fixedDays.Add((new DateTime(year, 12, 26), "Boxing Day"));
            moving.Add(Day(Nth(year, 5, DayOfWeek.Monday, 1), "Early May bank holiday"));
            moving.Add(Day(Last(year, 5, DayOfWeek.Monday), "Spring bank holiday"));
            moving.Add(Day(region == "SCT" ? Nth(year, 8, DayOfWeek.Monday, 1) : Last(year, 8, DayOfWeek.Monday), "Summer bank holiday"));

            // A holiday on a working day keeps its date. One on a weekend also gets the next working
            // day that is not a holiday already.
            var taken = new HashSet<DateTime>(moving.Select(holiday => holiday.Date));
            foreach (var (date, _) in fixedDays.Where(day => !IsWeekend(day.Date)))
            {
                taken.Add(date);
            }

            foreach (var holiday in moving)
            {
                yield return holiday;
            }

            foreach (var (date, name) in fixedDays.OrderBy(day => day.Date))
            {
                yield return Day(date, name);
                if (!IsWeekend(date))
                {
                    continue;
                }

                var substitute = date.AddDays(1);
                while (IsWeekend(substitute) || taken.Contains(substitute))
                {
                    substitute = substitute.AddDays(1);
                }

                taken.Add(substitute);
                if (substitute.Year == year)
                {
                    yield return Day(substitute, name + " (substitute day)");
                }
            }
        }
    }

    /// <summary>
    /// United States: the federal holidays of 5 U.S.C. 6103. A holiday on a Saturday is observed
    /// on the Friday before, one on a Sunday on the Monday after; both dates are listed.
    /// </summary>
    internal sealed class UnitedStatesHolidayProvider : HolidayProviderBase
    {
        public override string CountryCode => "US";

        public override string CountryName => "United States";

        public override string Note =>
            "The eleven federal holidays. A holiday on a Saturday is observed on the Friday before and one on a Sunday on the Monday after; the list has both dates. " +
            "State holidays and days many businesses close without a law, such as the day after Thanksgiving, are not in the list.";

        protected override IEnumerable<HolidayDate> Compute(int year, string? region)
        {
            var fixedDays = new (DateTime Date, string Name)[]
            {
                (new DateTime(year, 1, 1), "New Year's Day"),
                (new DateTime(year, 6, 19), "Juneteenth National Independence Day"),
                (new DateTime(year, 7, 4), "Independence Day"),
                (new DateTime(year, 11, 11), "Veterans Day"),
                (new DateTime(year, 12, 25), "Christmas Day")
            };
            foreach (var (date, name) in fixedDays)
            {
                yield return Day(date, name);
                var observed = date.DayOfWeek switch
                {
                    DayOfWeek.Saturday => date.AddDays(-1),
                    DayOfWeek.Sunday => date.AddDays(1),
                    _ => date
                };
                if (observed != date && observed.Year == year)
                {
                    yield return Day(observed, name + " (observed)");
                }
            }

            // New Year's Day of the next year on a Saturday is observed on 31 December of this one.
            if (new DateTime(year + 1, 1, 1).DayOfWeek == DayOfWeek.Saturday)
            {
                yield return Day(year, 12, 31, "New Year's Day (observed)");
            }

            yield return Day(Nth(year, 1, DayOfWeek.Monday, 3), "Birthday of Martin Luther King, Jr.");
            yield return Day(Nth(year, 2, DayOfWeek.Monday, 3), "Washington's Birthday");
            yield return Day(Last(year, 5, DayOfWeek.Monday), "Memorial Day");
            yield return Day(Nth(year, 9, DayOfWeek.Monday, 1), "Labor Day");
            yield return Day(Nth(year, 10, DayOfWeek.Monday, 2), "Columbus Day");
            yield return Day(Nth(year, 11, DayOfWeek.Thursday, 4), "Thanksgiving Day");
        }
    }
}
