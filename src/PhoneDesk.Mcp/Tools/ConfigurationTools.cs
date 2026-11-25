using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using PhoneDesk.Holidays;
using PhoneDesk.Models;
using PhoneDesk.Operations;
using PhoneDesk.Portability;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Mcp.Tools;

[McpServerToolType]
internal sealed class ConfigurationTools
{
    private readonly ConfigurationStore _store;
    private readonly IValidationService _validation;
    private readonly McpEnvironment _environment;

    public ConfigurationTools(ConfigurationStore store, IValidationService validation, McpEnvironment environment)
    {
        _store = store;
        _validation = validation;
        _environment = environment;
    }

    [McpServerTool(Name = "configuration_get", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the current configuration (customer, naming, language, opening hours, call queue and auto attendant settings, holidays), the object names computed from it, and any validation problems. The provisioning tools build their scripts from this configuration. It is local to this computer; reading or changing it does not touch the tenant.")]
    public string Get() => McpJson.Run(Describe);

    [McpServerTool(Name = "configuration_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Changes single configuration values. Local only; the tenant is not touched. 'changes' maps a path to its new JSON value, for example {\"general.customer\": \"contoso\", \"general.customerGroupName\": \"reception\", \"businessHoursTemplate.openingHours1Start\": \"08:00:00\"}. Use configuration_get to see every path and the current values. Arrays such as businessHoursTemplate.weeklySchedule and holidayTemplate.series are replaced as a whole.")]
    public string Update(
        [Description("Map of configuration path to new JSON value.")] Dictionary<string, JsonElement> changes)
        => McpJson.Run(() => WithChanges(_store.Patch(changes)));

    [McpServerTool(Name = "configuration_replace", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Replaces the whole configuration with a PhoneDesk configuration document (schemaVersion 1, kind 'phonedesk.configuration'), the same JSON the desktop app exports. Local only.")]
    public string Replace(
        [Description("The complete configuration document as JSON text.")] string json)
        => McpJson.Run(() => WithChanges(_store.Replace(json)));

    [McpServerTool(Name = "configuration_reset", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Resets the configuration to an empty one with the default Teams Phone license and application IDs. Local only.")]
    public string Reset() => McpJson.Run(() => WithChanges(_store.Reset()));

    [McpServerTool(Name = "configuration_export", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Writes the current configuration to a JSON file that the desktop app can import. The file goes to the server's export folder on this computer; the result gives the full path. An existing file with the same name is not replaced.")]
    public string Export(
        [Description("File name only, for example contoso-reception.json. No folder.")] string fileName)
        => McpJson.Run(() =>
        {
            var name = (fileName ?? string.Empty).Trim();
            if (name.Length == 0 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("fileName must be a plain file name that ends with .json, without a folder.");
            }

            Directory.CreateDirectory(_environment.ExportDirectory);
            var fullPath = Path.Combine(_environment.ExportDirectory, name);
            using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(_store.ToJson());
            }

            return new { success = true, file = fullPath };
        });

    [McpServerTool(Name = "configuration_import", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Reads a PhoneDesk configuration file (from the desktop app or configuration_export). With apply=false it only lists what would change. With apply=true it replaces the current configuration. Local only. The file content is never returned, only the differences to the current configuration.")]
    public string Import(
        [Description("Full path of the configuration .json file.")] string path,
        [Description("False to preview the differences, true to apply them.")] bool apply = false)
        => McpJson.Run(() =>
        {
            var json = McpFiles.ReadJsonFile(path, maxBytes: 1024 * 1024);
            return apply
                ? WithChanges(_store.Replace(json))
                : new { applied = false, changes = _store.Preview(json) };
        });

    [McpServerTool(Name = "swiss_holidays", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the public holidays of a Swiss canton for a year, with their legal category (statutory, additional cantonal rest day, Sunday observance). Switzerland is the only country. In Aargau (AG), Fribourg (FR) and Solothurn (SO) the holidays depend on the region: call without 'region' to get the list of regions and the localities each one covers, ask the user which one applies, and call again with its code. Changes nothing; use swiss_holidays_apply to write the holidays to the configuration.")]
    public string SwissHolidays(
        [Description(CantonDescription)] string canton,
        [Description(YearDescription)] int year,
        [Description(RegionDescription)] string? region = null,
        [Description(RestDaysDescription)] bool includeAdditionalRestDays = true)
        => McpJson.Run(() =>
        {
            var selection = Select(canton, year, region, includeAdditionalRestDays);
            if (selection.Holidays is null)
            {
                return selection.RegionQuestion();
            }

            return new
            {
                canton = selection.Canton.ToString(),
                year,
                region = selection.Region?.Code,
                regionRequired = false,
                completeness = HolidayResultCompleteness.Complete,
                note = selection.Note,
                holidays = selection.Holidays.Select(Describe)
            };
        });

    [McpServerTool(Name = "swiss_holidays_apply", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Writes the public holidays of a Swiss canton and year to the holiday entries of the configuration (holidayTemplate.series), the list create_holiday_schedule works from. Local only; the tenant is not touched. Entries that are already there stay, and a date that is already there with the same time and name is not added twice. For Aargau (AG), Fribourg (FR) and Solothurn (SO) a region is needed; without it nothing is written and the result lists the regions. Show the user the holidays from swiss_holidays first.")]
    public string ApplySwissHolidays(
        [Description(CantonDescription)] string canton,
        [Description(YearDescription)] int year,
        [Description(RegionDescription)] string? region = null,
        [Description(RestDaysDescription)] bool includeAdditionalRestDays = true,
        [Description("True to remove the holiday entries that are in the configuration now and keep only the ones of this call. False (default) adds to them.")] bool replaceExisting = false)
        => McpJson.Run(() =>
        {
            var selection = Select(canton, year, region, includeAdditionalRestDays);
            if (selection.Holidays is null)
            {
                return selection.RegionQuestion();
            }

            var series = replaceExisting
                ? new List<PortableHolidayEntry>()
                : _store.Document.Configuration.HolidayTemplate.Series.ToList();
            var added = HolidaySeriesMerge.MissingFrom(
                replaceExisting ? Array.Empty<IHolidayEntry>() : _store.Variables.HolidaySeries, selection.Holidays);
            series.AddRange(added.Select(holiday => new PortableHolidayEntry(
                DateOnly.FromDateTime(holiday.Date), TimeOnly.FromTimeSpan(holiday.Time), holiday.Name, null, null, holiday.Category)));

            if (added.Count > 0 || replaceExisting)
            {
                _store.SetHolidaySeries(series);
            }

            var description = Describe();
            return new
            {
                applied = true,
                canton = selection.Canton.ToString(),
                year,
                region = selection.Region?.Code,
                note = selection.Note,
                added = added.Select(Describe),
                alreadyPresent = selection.Holidays.Count - added.Count,
                holidayEntriesNow = series.Count,
                holidaySchedule = _store.Variables.HolidayName,
                validation = description.Validation
            };
        });

    private const string CantonDescription = "Two-letter canton code, for example ZH, BE, AG.";
    private const string YearDescription = "Calendar year, 2025 to 2030.";
    private const string RegionDescription = "Region code, only for AG, FR and SO. Call without it to get the valid codes and the localities each one covers. Never guess it; ask the user where the workplace is.";
    private const string RestDaysDescription = "False to leave out the additional cantonal rest days (category additionalCantonalRestDay), which are not statutory holidays. Default true.";

    private const int FirstYear = 2025;
    private const int LastYear = 2030;

    private static HolidaySelection Select(string canton, int year, string? region, bool includeAdditionalRestDays)
    {
        if (!Enum.TryParse<SwissCanton>(canton?.Trim(), ignoreCase: true, out var code) || !Enum.IsDefined(code) ||
            int.TryParse(canton?.Trim(), out _))
        {
            throw new ArgumentException(
                $"'{canton}' is not a Swiss canton code. Valid codes: {string.Join(", ", Enum.GetNames<SwissCanton>())}.");
        }

        if (year is < FirstYear or > LastYear)
        {
            throw new ArgumentException($"The holiday rules are verified for {FirstYear} to {LastYear}. {year} is outside that range.");
        }

        var regions = SwissHolidayRegions.For(code);
        var wanted = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
        SwissHolidayRegion? selected = null;
        string? note = null;
        if (regions.Count == 0)
        {
            if (wanted is not null)
            {
                note = $"Canton {code} has one holiday list for the whole canton; region '{wanted}' was ignored.";
            }
        }
        else if (wanted is null)
        {
            return new HolidaySelection(code, year, null, regions, null, null);
        }
        else
        {
            selected = SwissHolidayRegions.Find(code, wanted)
                ?? throw new ArgumentException(
                    $"'{wanted}' is not a region of canton {code}. Valid codes: " +
                    string.Join("; ", regions.Select(item => $"{item.Code} ({item.Covers})")));
        }

        var result = SwissHolidayCatalog.Compute(code, selected?.Code, year);
        if (result.Completeness != HolidayResultCompleteness.Complete)
        {
            throw new InvalidOperationException($"The holiday list for canton {code} is not complete.");
        }

        var holidays = result.Holidays
            .Where(holiday => includeAdditionalRestDays || holiday.Category != HolidayCategory.AdditionalCantonalRestDay)
            .ToList();
        return new HolidaySelection(code, year, selected, regions, holidays, note);
    }

    private static object Describe(HolidayDate holiday) => new
    {
        date = DateOnly.FromDateTime(holiday.Date),
        time = TimeOnly.FromTimeSpan(holiday.Time),
        name = holiday.Name,
        category = holiday.Category
    };

    /// <summary>A canton, year and region with its holidays; <see cref="Holidays"/> is null while a region is still needed.</summary>
    private sealed record HolidaySelection(
        SwissCanton Canton,
        int Year,
        SwissHolidayRegion? Region,
        IReadOnlyList<SwissHolidayRegion> Regions,
        IReadOnlyList<HolidayDate>? Holidays,
        string? Note)
    {
        public object RegionQuestion() => new
        {
            canton = Canton.ToString(),
            year = Year,
            regionRequired = true,
            applied = false,
            message = $"The holidays of canton {Canton} depend on the region. Ask the user which region the workplace is in and call again with its code as 'region'. Nothing was changed.",
            regions = Regions.Select(item => new { code = item.Code, name = item.Name, covers = item.Covers })
        };
    }

    private object WithChanges(object changes)
    {
        var description = Describe();
        return new { applied = true, changes, computedNames = description.ComputedNames, validation = description.Validation };
    }

    private ConfigurationDescription Describe()
    {
        var variables = _store.Variables;
        var validation = _validation.ValidateVariables(variables);
        return new ConfigurationDescription(
            JsonNode.Parse(_store.ToJson())!,
            new
            {
                m365Group = variables.M365Group,
                callQueueResourceAccountUpn = variables.RacqUPN,
                callQueueResourceAccountDisplayName = variables.RacqDisplayName,
                callQueue = variables.CqDisplayName,
                autoAttendantResourceAccountUpn = variables.RaaaUPN,
                autoAttendantResourceAccountDisplayName = variables.RaaaDisplayName,
                autoAttendant = variables.AaDisplayName,
                holidaySchedule = variables.HolidayName
            },
            new
            {
                valid = validation.IsValid,
                problems = validation.Issues.Select(issue => new { code = issue.Code, message = issue.Message })
            });
    }

    private sealed record ConfigurationDescription(JsonNode Document, object ComputedNames, object Validation);
}
