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
    private readonly IDryRunPlanBuilder _planBuilder;
    private readonly ChangeGate _changes;
    private readonly CreatedObjectGuard _guard;
    private readonly IDryRunPlanExporter _planExporter;
    private readonly TenantGateway _tenant;
    private readonly IPowerShellCommandService _commands;

    public BulkTools(
        BulkSetupScriptBuilder bulk,
        IDryRunPlanBuilder planBuilder,
        ChangeGate changes,
        CreatedObjectGuard guard,
        IDryRunPlanExporter planExporter,
        TenantGateway tenant,
        IPowerShellCommandService commands)
    {
        _commands = commands;
        _planExporter = planExporter;
        _tenant = tenant;
        _bulk = bulk;
        _planBuilder = planBuilder;
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

    [McpServerTool(Name = "bulk_setup_preview", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Returns the dry-run plan of a bulk setup: for every CSV row the objects that would be created with their computed names, the validation errors, and the preflight checks (the same group, account or number in two rows). Without checkTenant nothing is sent to the tenant. With checkTenant=true (needs both connections) it also reads the tenant for every row: which names and accounts exist already (the setup reuses them: a warning), whether enough licenses are free for all rows together, and whether each phone number is free. A check with the status Fail means that row would fail. Use it before bulk_setup. Read-only.")]
    public Task<string> BulkSetupPreview(
        [Description("CSV text with the header row from bulk_setup_template.")] string csv,
        [Description("True to compare the plan with what the tenant holds. Needs both connections.")] bool checkTenant = false,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var entries = _bulk.ParseCsv(csv ?? string.Empty).Select(ToVariables).ToList();
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("The CSV has no data rows. It needs the header row and at least one row.");
            }

            var plan = _planBuilder.BuildBulkPlan(entries);
            if (checkTenant)
            {
                var read = await _tenant.ReadAsync(_commands.GetTenantPreflightCommand(entries), "PreviewTenantCheck", cancellationToken);
                plan = TenantPreflight.Apply(plan, entries, TenantPreflight.Parse(string.IsNullOrEmpty(read.RawOutput) ? read.Value : read.RawOutput));
            }

            return (object)System.Text.Json.Nodes.JsonNode.Parse(_planExporter.ToJson(plan))!;
        });

    [McpServerTool(Name = "bulk_setup", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Sets up several complete phone systems from CSV text, one per row, in one script. Every row is validated first; one invalid row stops the whole run, unless skipInvalidRows is true: then the valid rows are planned and the result lists the rows that were left out. CHANGES THE TENANT. Call it first without planId: it returns the rows, their computed names and the exact script as a plan and changes nothing. Show the plan to the user, then call it again with the same CSV and the planId to run it.")]
    public Task<string> BulkSetup(
        [Description("CSV text with the header row from bulk_setup_template.")] string csv,
        [Description("The planId from the first call. Omit to get the plan.")] string? planId = null,
        [Description("True to set up the valid rows and leave out the invalid ones. Tell the user which rows were left out. Use the same value in both calls.")] bool skipInvalidRows = false,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var entries = _bulk.ParseCsv(csv ?? string.Empty).Select(ToVariables).ToList();
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("The CSV has no data rows. It needs the header row and at least one row.");
            }

            // The same row rules and cross-row checks as the desktop bulk page.
            var planned = _planBuilder.BuildBulkPlan(entries).Entries;
            var problems = planned
                .Where(entry => !entry.IsValid)
                .Select(entry => $"Row {entry.RowNumber}: " + string.Join(" ", entry.ValidationErrors.Concat(
                    entry.PreflightChecks
                        .Where(check => check.Status == Planning.PreflightStatus.Fail)
                        .Select(check => check.Detail))))
                .ToList();
            if (problems.Count > 0 && !skipInvalidRows)
            {
                throw new InvalidOperationException(
                    "The CSV has invalid rows. Correct them, or pass skipInvalidRows=true to set up the valid rows only. " + string.Join(" ", problems));
            }

            if (problems.Count > 0)
            {
                // Like the desktop "skip invalid rows" switch: the plan entries are in row order.
                entries = planned.Where(entry => entry.IsValid).Select(entry => entries[entry.RowNumber - 1]).ToList();
                if (entries.Count == 0)
                {
                    throw new InvalidOperationException("No row of the CSV is valid. " + string.Join(" ", problems));
                }
            }

            var summary = $"Set up {entries.Count} phone system(s): " +
                          string.Join("; ", entries.Select(entry => $"{entry.CqDisplayName} + {entry.AaDisplayName}")) +
                          (problems.Count > 0 ? $". Left out {problems.Count} invalid row(s): " + string.Join(" ", problems) : string.Empty);
            return await _changes.PlanOrApplyAsync(
                new ChangeRequest("BulkOperations", $"{entries.Count} entries", _bulk.GenerateBulkScript(entries), summary)
                {
                    // The bulk script prints progress lines, not one SUCCESS line: it failed when an error was reported.
                    IsSuccess = result => result.IsSuccess && !result.HasErrorMarker,
                    OnSuccessAsync = async (result, token) =>
                    {
                        if (!CreatedObjectGuard.ReusedExistingObject(result.RawOutput))
                        {
                            // Nothing was reused: every object of every row is new.
                            foreach (var (type, name) in entries.SelectMany(CreatedObjects))
                            {
                                await _guard.ClaimAsync(type, name, "BulkOperations", result.RawOutput, token);
                            }

                            return;
                        }

                        // A run after a failed run reuses what the failed run made. Only an object
                        // with its own "created" line in this output is new.
                        await ClaimProvenAsync(entries, result.RawOutput, token);
                    },
                    // A script that failed part of the way can still have created objects. An object
                    // with its own "created" line is recorded at once: a new resource account is not
                    // visible in the tenant for some seconds, so a read of the tenant can miss it.
                    // For the other objects the guard records only those a new read confirms.
                    OnFailureAsync = async (result, token) =>
                    {
                        var proven = await ClaimProvenAsync(entries, result.RawOutput + "\n" + result.ErrorMessage, token);
                        foreach (var item in entries.SelectMany(CreatedObjects).Where(item => !proven.Contains(item)))
                        {
                            await _guard.ClaimAfterFailureAsync(item.Type, item.Name, "BulkOperations", result, token);
                        }
                    }
                },
                planId,
                cancellationToken);
        });

    /// <summary>Records every object whose own "created" line is in the output, and returns them.</summary>
    private async Task<HashSet<(TenantObjectType Type, string Name)>> ClaimProvenAsync(
        IReadOnlyList<ConfigurationDocumentVariables> entries, string output, CancellationToken cancellationToken)
    {
        var proven = new HashSet<(TenantObjectType Type, string Name)>();
        for (var index = 0; index < entries.Count; index++)
        {
            var rowOutput = BulkOutputEvidence.RowOutput(output, index, entries.Count);
            foreach (var item in CreatedObjects(entries[index]))
            {
                if (BulkOutputEvidence.Created(output, rowOutput, item.Type, item.Name))
                {
                    await _guard.ClaimProvenAsync(item.Type, item.Name, "BulkOperations", output, cancellationToken);
                    proven.Add(item);
                }
            }
        }

        return proven;
    }

    private static IEnumerable<(TenantObjectType Type, string Name)> CreatedObjects(ConfigurationDocumentVariables entry)
    {
        yield return (TenantObjectType.M365Group, entry.M365Group);
        yield return (TenantObjectType.ResourceAccount, entry.RacqUPN);
        yield return (TenantObjectType.CallQueue, entry.CqDisplayName);
        yield return (TenantObjectType.ResourceAccount, entry.RaaaUPN);
        yield return (TenantObjectType.AutoAttendant, entry.AaDisplayName);

        // Only a Direct Routing number is an inventory entry an assignment created.
        if (!string.IsNullOrWhiteSpace(entry.RaaAnr) &&
            string.Equals(entry.PhoneNumberType.Trim(), "DirectRouting", StringComparison.OrdinalIgnoreCase))
        {
            yield return (TenantObjectType.PhoneNumber, entry.RaaAnr.Trim());
        }
    }

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
