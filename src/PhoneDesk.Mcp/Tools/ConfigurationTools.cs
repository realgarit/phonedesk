using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using PhoneDesk.Holidays;
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
    [Description("Lists the public holidays of a Swiss canton for a year, with their legal category (statutory, additional cantonal rest day, Sunday observance). To use them for the holiday schedule, write the wanted entries to holidayTemplate.series with configuration_update. Some cantons need a district for a complete result; 'completeness' says so.")]
    public string SwissHolidays(
        [Description("Two-letter canton code, for example ZH, BE, AG.")] string canton,
        [Description("Calendar year.")] int year,
        [Description("District or locality, where the canton's holidays depend on it.")] string? district = null)
        => McpJson.Run(() =>
        {
            if (!Enum.TryParse<SwissCanton>(canton?.Trim(), ignoreCase: true, out var code) || !Enum.IsDefined(code))
            {
                throw new ArgumentException(
                    $"'{canton}' is not a Swiss canton code. Valid codes: {string.Join(", ", Enum.GetNames<SwissCanton>())}.");
            }

            var result = SwissHolidayCatalog.Compute(code, district, year);
            return new
            {
                canton = code.ToString(),
                year,
                completeness = result.Completeness,
                holidays = result.Holidays.Select(holiday => new
                {
                    date = DateOnly.FromDateTime(holiday.Date),
                    time = TimeOnly.FromTimeSpan(holiday.Time),
                    name = holiday.Name,
                    category = holiday.Category
                })
            };
        });

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
