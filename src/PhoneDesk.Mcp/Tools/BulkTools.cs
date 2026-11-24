using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;
using PhoneDesk.Portability;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Mcp.Tools;

[McpServerToolType]
internal sealed class BulkTools
{
    private readonly BulkSetupScriptBuilder _bulk;
    private readonly IValidationService _validation;
    private readonly ChangeGate _changes;
    private readonly CreatedObjectGuard _guard;

    public BulkTools(
        BulkSetupScriptBuilder bulk,
        IValidationService validation,
        ChangeGate changes,
        CreatedObjectGuard guard)
    {
        _bulk = bulk;
        _validation = validation;
        _changes = changes;
        _guard = guard;
    }

    [McpServerTool(Name = "bulk_setup_template", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the CSV template for bulk setup: the column headers and one example row. Each row is one complete phone system (agent group, call queue, auto attendant).")]
    public string Template() => McpJson.Run(() => new
    {
        columns = BulkSetupScriptBuilder.CsvHeaders,
        template = _bulk.GenerateCsvTemplate()
    });

    [McpServerTool(Name = "bulk_setup", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Sets up several complete phone systems from CSV text, one per row, in one script. Every row is validated first; one invalid row stops the whole run. CHANGES THE TENANT. Call it first without planId: it returns the rows, their computed names and the exact script as a plan and changes nothing. Show the plan to the user, then call it again with the same CSV and the planId to run it.")]
    public Task<string> BulkSetup(
        [Description("CSV text with the header row from bulk_setup_template.")] string csv,
        [Description("The planId from the first call. Omit to get the plan.")] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var entries = _bulk.ParseCsv(csv ?? string.Empty).Select(ToVariables).ToList();
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("The CSV has no data rows. It needs the header row and at least one row.");
            }

            var problems = entries
                .Select((entry, index) => new { row = index + 1, issues = _validation.ValidateVariables(entry).Issues })
                .Where(item => item.issues.Count > 0)
                .Select(item => $"Row {item.row}: {string.Join(" ", item.issues.Select(issue => issue.Message))}")
                .ToList();
            if (problems.Count > 0)
            {
                throw new InvalidOperationException("The CSV has invalid rows. " + string.Join(" ", problems));
            }

            var summary = $"Set up {entries.Count} phone system(s): " +
                          string.Join("; ", entries.Select(entry => $"{entry.CqDisplayName} + {entry.AaDisplayName}"));
            return await _changes.PlanOrApplyAsync(
                new ChangeRequest("BulkOperations", $"{entries.Count} entries", _bulk.GenerateBulkScript(entries), summary)
                {
                    // The bulk script prints progress lines, not one SUCCESS line: it failed when an error was reported.
                    IsSuccess = result => result.IsSuccess && !result.HasErrorMarker,
                    OnSuccess = result =>
                    {
                        // One combined output: when any row reused an object, no row can be proven new.
                        // The group is not claimed: the combined output does not tie a group ID to a row.
                        foreach (var entry in entries)
                        {
                            _guard.Claim(TenantObjectType.ResourceAccount, entry.RacqUPN, "BulkOperations", result.RawOutput);
                            _guard.Claim(TenantObjectType.CallQueue, entry.CqDisplayName, "BulkOperations", result.RawOutput);
                            _guard.Claim(TenantObjectType.ResourceAccount, entry.RaaaUPN, "BulkOperations", result.RawOutput);
                            _guard.Claim(TenantObjectType.AutoAttendant, entry.AaDisplayName, "BulkOperations", result.RawOutput);
                        }
                    }
                },
                planId,
                cancellationToken);
        });

    /// <summary>A CSV row over an otherwise empty configuration, as the desktop bulk page builds it.</summary>
    private static ConfigurationDocumentVariables ToVariables(BulkCsvRow row)
    {
        var document = ConfigurationDocumentVariables.CreateDefaultDocument(DateOnly.FromDateTime(DateTime.Today));
        var configuration = document.Configuration;
        var hours = configuration.BusinessHoursTemplate;
        return new ConfigurationDocumentVariables(document with
        {
            Configuration = configuration with
            {
                General = configuration.General with
                {
                    Customer = row.Customer,
                    CustomerGroupName = row.CustomerGroupName,
                    MsFallbackDomain = row.MsFallbackDomain,
                    RaaAnrName = row.RaaAnrName,
                    LanguageId = row.LanguageId,
                    TimeZoneId = row.TimeZoneId,
                    UsageLocation = row.UsageLocation,
                    RaaAnr = row.RaaAnr,
                    PhoneNumberType = row.PhoneNumberType
                },
                BusinessHoursTemplate = hours with
                {
                    OpeningHours1Start = ToTime(row.OpeningHours1Start, hours.OpeningHours1Start),
                    OpeningHours1End = ToTime(row.OpeningHours1End, hours.OpeningHours1End),
                    OpeningHours2Start = ToTime(row.OpeningHours2Start, hours.OpeningHours2Start),
                    OpeningHours2End = ToTime(row.OpeningHours2End, hours.OpeningHours2End)
                }
            }
        });
    }

    private static TimeOnly ToTime(TimeSpan? value, TimeOnly fallback)
        => value is { } time && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) ? TimeOnly.FromTimeSpan(time) : fallback;
}
