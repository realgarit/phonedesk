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
    private readonly McpEnvironment _environment;

    public ConfigurationTools(ConfigurationStore store, McpEnvironment environment)
    {
        _store = store;
        _environment = environment;
    }

    [McpServerTool(Name = "configuration_get", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the current configuration (customer, naming, language, opening hours, call queue and auto attendant settings, holidays), the object names computed from it, and any validation problems. The provisioning tools build their scripts from this configuration. It is local to this computer; reading or changing it does not touch the tenant.")]
    public string Get() => McpJson.Run(Describe);

    [McpServerTool(Name = "configuration_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Changes single configuration values. Local only; the tenant is not touched. 'changes' maps a path to its new JSON value, for example {\"general.customer\": \"contoso\", \"general.customerGroupName\": \"reception\", \"businessHoursTemplate.openingHours1Start\": \"08:00:00\"}. Use configuration_get to see every path and the current values, and configuration_options for the values that take one of a fixed set of texts (actions, greeting types) and for known languages, time zones and countries. A value outside a fixed set is refused and nothing is changed. Set an action and its target in the same call. Arrays such as businessHoursTemplate.weeklySchedule and holidayTemplate.series are replaced as a whole.")]
    public string Update(
        [Description("Map of configuration path to new JSON value.")] Dictionary<string, JsonElement> changes)
        => McpJson.Run(() => WithChanges(_store.Patch(changes)));

    [McpServerTool(Name = "configuration_options", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the configuration values that take one of a set of texts, with the valid texts in their exact spelling: greeting types, music on hold, the overflow, timeout and no-agent actions of the call queue, the actions of the auto attendant, and the known languages, time zones, usage locations and phone number types. 'closed' is true when no other text is accepted. Also gives the number limits. Read it before you set one of these values; never guess a spelling.")]
    public string Options() => McpJson.Run(() => new
    {
        options = ConfigurationChecks.Options.Select(option => new
        {
            path = option.Path,
            meaning = option.Meaning,
            closed = option.Closed,
            values = option.Values
        }),
        limits = new object[]
        {
            new { path = "callQueueTemplate.timeoutThreshold", unit = "seconds", minimum = PhoneDesk.Services.ConstantsService.CallQueue.MinTimeoutThreshold, usedWhenEmpty = PhoneDesk.Services.ConstantsService.CallQueue.TimeoutThreshold },
            new { path = "callQueueTemplate.overflowThreshold", unit = "calls", minimum = 0, usedWhenEmpty = PhoneDesk.Services.ConstantsService.CallQueue.OverflowThreshold }
        },
        rules = new[]
        {
            "An action TransferToTarget or TransferToVoicemail needs its target (the same path with 'Target' added). Without a target the call would be disconnected, so the configuration is not valid until the target is set.",
            "TransferToVoicemail takes the object ID of a Microsoft 365 group (shared voicemail). Use lookup_m365_group_id to read it.",
            "A greeting type AudioFile needs the audio file ID (import_audio_file); TextToSpeech needs the prompt text.",
            "A holiday entry has both endDate and endTime, or neither.",
            "In businessHoursTemplate.weeklySchedule the second time range of a day is used only when hasSecondRange is true."
        }
    });

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
    [Description("Lists the public holidays of a Swiss canton for a year, with their legal category (statutory, additional cantonal rest day, Sunday observance). Switzerland is the only country. In Aargau (AG), Fribourg (FR) and Solothurn (SO) the holidays depend on the region: call without 'region' to get the list of regions and the localities each one covers, ask the user which one applies, and call again with its code. Each holiday has a 'label' like the desktop app shows: an additional cantonal rest day is not a statutory holiday, although most businesses of the canton are closed, so name these days to the user and ask whether the customer is closed on them ('review' lists them). Customary days without a legal basis, such as Sechseläuten or Knabenschiessen in Zurich, are not in the list; add them with configuration_update when the customer is closed. Changes nothing; use swiss_holidays_apply to write the holidays to the configuration.")]
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
                review = Review(selection.Holidays),
                holidays = selection.Holidays.Select(Describe)
            };
        });

    [McpServerTool(Name = "swiss_holidays_apply", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Writes the public holidays of a Swiss canton and year to the holiday entries of the configuration (holidayTemplate.series), the list create_holiday_schedule works from. Local only; the tenant is not touched. It only adds: entries that are already there stay, and a date that is already there with the same time and name is not added twice. To remove entries, set holidayTemplate.series with configuration_update. For Aargau (AG), Fribourg (FR) and Solothurn (SO) a region is needed; without it nothing is written and the result lists the regions. Show the user the holidays from swiss_holidays first, and ask about the additional cantonal rest days before you include them.")]
    public string ApplySwissHolidays(
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

            // Only ever adds: manual closures and the entries of other years stay.
            var series = _store.Document.Configuration.HolidayTemplate.Series.ToList();
            var added = HolidaySeriesMerge.MissingFrom(_store.Variables.HolidaySeries, selection.Holidays);
            series.AddRange(added.Select(holiday => new PortableHolidayEntry(
                DateOnly.FromDateTime(holiday.Date), TimeOnly.FromTimeSpan(holiday.Time), holiday.Name, null, null, holiday.Category)));

            if (added.Count > 0)
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
                review = Review(added),
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
        category = holiday.Category,
        label = Label(holiday.Category)
    };

    /// <summary>The label the desktop holiday picker shows for a category (its English catalog text).</summary>
    internal static string Label(HolidayCategory category) => category switch
    {
        HolidayCategory.Statutory => "Statutory holiday (Art. 20a ArG)",
        HolidayCategory.AdditionalCantonalRestDay => "Additional cantonal rest day · confirm with customer",
        HolidayCategory.SundayObservance => "Sunday observance",
        _ => "Customer-specific / unclassified"
    };

    /// <summary>
    /// Names the days the operator must confirm with the customer, as the desktop picker does with
    /// its label and its "include additional cantonal rest days" switch. Null when there are none.
    /// </summary>
    private static object? Review(IReadOnlyList<HolidayDate> holidays)
    {
        var additional = holidays.Where(holiday => holiday.Category == HolidayCategory.AdditionalCantonalRestDay).ToList();
        return additional.Count == 0
            ? null
            : new
            {
                additionalCantonalRestDays = additional.Select(holiday => new { date = DateOnly.FromDateTime(holiday.Date), name = holiday.Name }),
                message = $"{additional.Count} of these days are additional cantonal rest days. They are not statutory holidays under Art. 20a ArG, " +
                          "but the canton treats them as rest days and most businesses are closed. Confirm with the customer whether they are closed on them. " +
                          "Use includeAdditionalRestDays=false to leave them out."
            };
    }

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
        return new { applied = true, changes, computedNames = description.ComputedNames, validation = description.Validation, warnings = description.Warnings };
    }

    private ConfigurationDescription Describe()
    {
        var variables = _store.Variables;
        var problems = _store.Problems();
        var warnings = _store.Warnings();
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
                valid = problems.Count == 0,
                problems = problems.Select(problem => new { code = JsonNamingPolicy.CamelCase.ConvertName(problem.Code), message = problem.Message })
            },
            warnings.Count == 0 ? null : warnings);
    }

    private sealed record ConfigurationDescription(JsonNode Document, object ComputedNames, object Validation, IReadOnlyList<string>? Warnings);
}
