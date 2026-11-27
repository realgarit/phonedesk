using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Holidays;
using PhoneDesk.Models;
using PhoneDesk.Operations;
using PhoneDesk.Portability;

namespace PhoneDesk.Mcp.Tools;

/// <summary>
/// Public holidays of the countries beside Switzerland, and holidays from a calendar file. The
/// desktop holiday picker offers the same countries and the same file import; both use the
/// providers and the calendar reader of the Domain layer.
/// </summary>
[McpServerToolType]
internal sealed class HolidayProviderTools
{
    private const string CountryDescription = "Country code or name: DE, AT, FR, GB or US. Call public_holidays without a country to list them with their regions.";
    private const string YearDescription = "Calendar year, 2025 to 2030.";
    private const string RegionDescription = "Region code of the country. Needed for DE (state) and GB (England and Wales, Scotland, Northern Ireland). Call without it to get the valid codes. Never guess it; ask the user where the workplace is.";
    private const string ExcludeDescription = "Dates to leave out, as yyyy-MM-dd, for days the customer is open. Show the list to the user first.";

    private readonly ConfigurationStore _store;

    public HolidayProviderTools(ConfigurationStore store)
    {
        _store = store;
    }

    [McpServerTool(Name = "public_holidays", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the public holidays of a country and year: Germany (per state, with own regions where a holiday applies to a part of a state), Austria, France (with Alsace-Moselle), the United Kingdom (per jurisdiction, with substitute days) and the United States (federal, with observed days). Without a country it lists the countries, their regions and what each list does not cover. For Switzerland use swiss_holidays, which knows the cantons. The dates are computed from the legal rules; days a business closes without a law (24 December, the day after Thanksgiving, local customs) are not in the list, so ask the user about them. Changes nothing; use public_holidays_apply to write the holidays to the configuration.")]
    public string PublicHolidays(
        [Description(CountryDescription)] string? country = null,
        [Description(YearDescription)] int year = 0,
        [Description(RegionDescription)] string? region = null)
        => McpJson.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(country))
            {
                return Countries();
            }

            var selection = Select(country, year, region);
            if (selection.Holidays is null)
            {
                return selection.Question!;
            }

            return new
            {
                country = selection.Provider.CountryCode,
                year,
                region = selection.Region?.Code,
                regionRequired = false,
                note = selection.Provider.Note,
                holidays = selection.Holidays.Select(Describe)
            };
        });

    [McpServerTool(Name = "public_holidays_apply", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Writes the public holidays of a country, year and region to the holiday entries of the configuration (holidayTemplate.series), the list create_holiday_schedule works from. Local only; the tenant is not touched. It only adds: entries that are already there stay, and a date that is already there with the same time and name is not added twice. Show the user the list from public_holidays first and pass the dates the customer is open on as excludeDates. For Switzerland use swiss_holidays_apply.")]
    public string ApplyPublicHolidays(
        [Description(CountryDescription)] string country,
        [Description(YearDescription)] int year,
        [Description(RegionDescription)] string? region = null,
        [Description(ExcludeDescription)] string[]? excludeDates = null)
        => McpJson.Run(() =>
        {
            var selection = Select(country, year, region);
            if (selection.Holidays is null)
            {
                return selection.Question!;
            }

            var excluded = ReadDates(excludeDates);
            var wanted = selection.Holidays.Where(holiday => !excluded.Contains(DateOnly.FromDateTime(holiday.Date))).ToList();
            return Add(wanted, new
            {
                country = selection.Provider.CountryCode,
                year,
                region = selection.Region?.Code,
                note = selection.Provider.Note,
                leftOut = selection.Holidays.Count - wanted.Count
            });
        });

    [McpServerTool(Name = "import_holiday_calendar", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Reads the events of a calendar file (.ics, iCalendar) as holiday entries, for countries and customers the built-in lists do not cover. With apply=false (the default) it only lists the events with what the reader noticed: an all-day event is a whole day or a range of days, a timed event keeps its times as they are written, a repeating event is read with its first date only, and cancelled events are left out. Show the list to the user. With apply=true it adds the events to the holiday entries of the configuration (holidayTemplate.series); it only adds, and an entry that is already there is not added twice. Local only; the tenant is not touched.")]
    public string ImportHolidayCalendar(
        [Description("Full path of the .ics file on this computer.")] string path,
        [Description("Only the events of this year. Omit for all events of the file.")] int? year = null,
        [Description("False to list the events, true to add them to the configuration.")] bool apply = false,
        [Description(ExcludeDescription)] string[]? excludeDates = null)
        => McpJson.Run(() =>
        {
            var result = IcsHolidayImport.Parse(McpFiles.ReadCalendarFile(path, maxBytes: 2 * 1024 * 1024), year);
            var excluded = ReadDates(excludeDates);
            var wanted = result.Holidays.Where(holiday => !excluded.Contains(DateOnly.FromDateTime(holiday.Entry.Date))).ToList();
            var events = wanted.Select(holiday => new
            {
                date = DateOnly.FromDateTime(holiday.Entry.Date),
                time = TimeOnly.FromTimeSpan(holiday.Entry.Time),
                endDate = holiday.Entry.EndDate is { } end ? DateOnly.FromDateTime(end) : (DateOnly?)null,
                endTime = holiday.Entry.EndTime is { } endTime ? TimeOnly.FromTimeSpan(endTime) : (TimeOnly?)null,
                name = holiday.Entry.Name,
                note = holiday.Note.Length == 0 ? null : holiday.Note
            }).ToList();
            if (!apply)
            {
                return new { applied = false, events, notRead = result.Skipped, leftOut = result.Holidays.Count - wanted.Count };
            }

            var unnamed = wanted.Count(holiday => string.IsNullOrWhiteSpace(holiday.Entry.Name));
            if (unnamed > 0)
            {
                throw new InvalidOperationException(
                    $"{unnamed} event(s) of the file have no title. Leave them out with excludeDates, or add them by hand with a name.");
            }

            return Add(wanted.Select(holiday => holiday.Entry).ToList(), new { notRead = result.Skipped, leftOut = result.Holidays.Count - wanted.Count });
        });

    /// <summary>Adds entries to the holiday series of the configuration. It only ever adds.</summary>
    private object Add(IReadOnlyList<HolidayDate> wanted, object source)
    {
        var series = _store.Document.Configuration.HolidayTemplate.Series.ToList();
        var added = HolidaySeriesMerge.MissingFrom(_store.Variables.HolidaySeries, wanted);
        series.AddRange(added.Select(holiday => new PortableHolidayEntry(
            DateOnly.FromDateTime(holiday.Date),
            TimeOnly.FromTimeSpan(holiday.Time),
            holiday.Name,
            holiday.EndDate is { } end ? DateOnly.FromDateTime(end) : null,
            holiday.EndTime is { } endTime ? TimeOnly.FromTimeSpan(endTime) : null,
            holiday.Category)));
        if (added.Count > 0)
        {
            _store.SetHolidaySeries(series);
        }

        return new
        {
            applied = true,
            source,
            added = added.Select(Describe),
            alreadyPresent = wanted.Count - added.Count,
            holidayEntriesNow = series.Count,
            holidaySchedule = _store.Variables.HolidayName
        };
    }

    private static object Countries() => new
    {
        message = "Give a country and a year. For Switzerland use swiss_holidays.",
        years = new { first = HolidayProviders.FirstYear, last = HolidayProviders.LastYear },
        countries = HolidayProviders.All
            .Where(provider => provider.CountryCode != "CH")
            .Select(provider => new
            {
                code = provider.CountryCode,
                name = provider.CountryName,
                regionRequired = provider.RegionRequired,
                regions = provider.Regions.Select(item => new { code = item.Code, name = item.Name }),
                note = provider.Note
            })
    };

    private static Selection Select(string country, int year, string? region)
    {
        var provider = HolidayProviders.Find(country)
            ?? throw new ArgumentException(
                $"'{country}' is not a country with a holiday list. Valid codes: " +
                string.Join(", ", HolidayProviders.All.Select(item => item.CountryCode)) + ". For another country use import_holiday_calendar with an .ics file.");
        if (provider.CountryCode == "CH")
        {
            throw new ArgumentException("For Switzerland use swiss_holidays and swiss_holidays_apply: they know the cantons and their regions.");
        }

        if (year is < HolidayProviders.FirstYear or > HolidayProviders.LastYear)
        {
            throw new ArgumentException(
                $"The holiday rules are verified for {HolidayProviders.FirstYear} to {HolidayProviders.LastYear}. {year} is outside that range.");
        }

        var wanted = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
        HolidayRegion? selected = null;
        if (wanted is not null)
        {
            selected = HolidayProviders.FindRegion(provider, wanted)
                ?? throw new ArgumentException(
                    provider.Regions.Count == 0
                        ? $"{provider.CountryName} has one holiday list for the whole country; give no region."
                        : $"'{wanted}' is not a region of {provider.CountryName}. Valid codes: " +
                          string.Join("; ", provider.Regions.Select(item => $"{item.Code} ({item.Name})")));
        }
        else if (provider.RegionRequired)
        {
            return new Selection(provider, null, null, new
            {
                country = provider.CountryCode,
                year,
                regionRequired = true,
                applied = false,
                message = $"The holidays of {provider.CountryName} depend on the region. Ask the user where the workplace is and call again with the code as 'region'. Nothing was changed.",
                regions = provider.Regions.Select(item => new { code = item.Code, name = item.Name })
            });
        }

        return new Selection(provider, selected, provider.GetHolidays(year, selected?.Code), null);
    }

    private static HashSet<DateOnly> ReadDates(string[]? dates)
    {
        var result = new HashSet<DateOnly>();
        foreach (var text in dates ?? Array.Empty<string>())
        {
            if (!DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            {
                throw new ArgumentException($"'{text}' is not a date in the form yyyy-MM-dd.");
            }

            result.Add(date);
        }

        return result;
    }

    private static object Describe(HolidayDate holiday) => new
    {
        date = DateOnly.FromDateTime(holiday.Date),
        weekday = holiday.Date.DayOfWeek.ToString(),
        time = TimeOnly.FromTimeSpan(holiday.Time),
        name = holiday.Name,
        category = holiday.Category,
        label = ConfigurationTools.Label(holiday.Category)
    };

    private sealed record Selection(IHolidayProvider Provider, HolidayRegion? Region, IReadOnlyList<HolidayDate>? Holidays, object? Question);
}
