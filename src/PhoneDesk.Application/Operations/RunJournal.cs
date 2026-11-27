using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhoneDesk.Models;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Operations;

public enum RunObjectType
{
    M365Group,
    ResourceAccount,
    CallQueue,
    AutoAttendant
}

public enum RunKind
{
    GuidedSetup,
    Bulk
}

public enum RunState
{
    /// <summary>The run was started and neither finished nor failed: the application was closed, or it stopped.</summary>
    Running,
    Failed,
    Completed,
    RolledBack
}

/// <summary>An object a run created in the tenant, with the identity it had when it was created.</summary>
public sealed class RunObject
{
    public RunObjectType Type { get; set; }

    /// <summary>What the scripts find the object by: a display name, a sign-in name, or a phone number.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The immutable ID of the object in the tenant. Empty until the tenant showed the object.</summary>
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>The step (Guided Setup) or the row (bulk) that created it.</summary>
    public int Step { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>True when a rollback removed the object, or found it gone.</summary>
    public bool Removed { get; set; }

    /// <summary>What the last rollback said about this object.</summary>
    public string RollbackNote { get; set; } = string.Empty;
}

/// <summary>
/// One Guided Setup or bulk run: what it was for, how far it got and what it created. The record
/// is written to disk after every step, so a run that was interrupted can be resumed or rolled
/// back later.
/// </summary>
public sealed class RunRecord
{
    public string Id { get; set; } = string.Empty;

    public RunKind Kind { get; set; }

    /// <summary>Which head ran it: desktop or mcp.</summary>
    public string Head { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string TenantName { get; set; } = string.Empty;

    /// <summary>The customer and group of the run, for the person who reads the list.</summary>
    public string Label { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public RunState State { get; set; }

    public int? FailedStep { get; set; }

    public string Error { get; set; } = string.Empty;

    /// <summary>The steps of a Guided Setup that completed, with the fingerprint of the script each one ran.</summary>
    public Dictionary<int, string> CompletedSteps { get; set; } = new();

    public List<RunObject> Objects { get; set; } = new();

    [JsonIgnore]
    public bool CanRollBack => State != RunState.RolledBack && Objects.Any(item => !item.Removed);

    [JsonIgnore]
    public bool IsOpen => State is RunState.Running or RunState.Failed;
}

/// <summary>Where the run records are kept. One file per run, so a crash loses at most the step in progress.</summary>
public interface IRunJournalStore
{
    IReadOnlyList<RunRecord> List();

    RunRecord? Find(string id);

    void Save(RunRecord record);
}

/// <summary>What the tenant holds now for one object of a run.</summary>
public sealed class RunObjectIdentity
{
    public int Index { get; init; }

    /// <summary>The IDs of the live objects with the name. Null when the tenant could not be read for it.</summary>
    public IReadOnlyList<string>? Ids { get; init; }

    public string Error { get; init; } = string.Empty;
}

public enum RollbackAction
{
    Delete,
    AlreadyGone,
    Blocked
}

/// <param name="Code">A fixed word for the reason, so a head can show it in its own language.</param>
public sealed record RollbackItem(RunObject Object, RollbackAction Action, string Code, string Reason);

public sealed class RollbackPlan
{
    public IReadOnlyList<RollbackItem> Items { get; init; } = Array.Empty<RollbackItem>();

    public bool HasWork => Items.Any(item => item.Action == RollbackAction.Delete);
}

public sealed record RollbackOutcome(RunObject Object, bool Removed, string Message);

/// <summary>
/// The rules of a run journal: which object a step created, the order a rollback removes objects
/// in, and whether an object in the tenant is still the one the run created. Both heads use it.
/// A rollback never removes an object the run did not create: an object counts as created only
/// with its own "created" line in the output of the script, and it is removed only while the
/// tenant shows exactly one object with the name and that object has the recorded ID.
/// A phone number is not an object of a run: that a number was assigned does not prove the run
/// added it to the inventory of the tenant. Removing the resource account takes the number off
/// it, and the number stays in the tenant.
/// </summary>
public static class RunJournal
{
    public const string IdentityMarker = "PDRUNIDENTITY_JSON: ";
    public const string StepMarker = "PDROLLBACK_ITEM: ";

    public const string ReasonOk = "ok";
    public const string ReasonGone = "gone";
    public const string ReasonUnreadable = "unreadable";
    public const string ReasonAmbiguous = "ambiguous";
    public const string ReasonNoId = "noId";
    public const string ReasonOtherId = "otherId";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(RunRecord record) => JsonSerializer.Serialize(record, Options);

    public static RunRecord? Deserialize(string json) => JsonSerializer.Deserialize<RunRecord>(json, Options);

    public static RunRecord Start(RunKind kind, string head, string? tenantId, string? tenantName, string label, DateTime utcNow)
        => new()
        {
            Id = utcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6],
            Kind = kind,
            Head = head,
            TenantId = tenantId ?? string.Empty,
            TenantName = tenantName ?? string.Empty,
            Label = label,
            StartedUtc = utcNow,
            UpdatedUtc = utcNow,
            State = RunState.Running
        };

    /// <summary>The object a Guided Setup step creates, or null for a step that creates none.</summary>
    public static (RunObjectType Type, string Name)? ObjectOfStep(GuidedSetupStep step, IPhoneManagerVariables variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return step switch
        {
            GuidedSetupStep.CreateM365Group => (RunObjectType.M365Group, variables.M365Group),
            GuidedSetupStep.CreateCallQueueResourceAccount => (RunObjectType.ResourceAccount, variables.RacqUPN),
            GuidedSetupStep.CreateCallQueue => (RunObjectType.CallQueue, variables.CqDisplayName),
            GuidedSetupStep.CreateAutoAttendantResourceAccount => (RunObjectType.ResourceAccount, variables.RaaaUPN),
            GuidedSetupStep.CreateAutoAttendant => (RunObjectType.AutoAttendant, variables.AaDisplayName),
            _ => null
        };
    }

    /// <summary>The objects one row of a bulk run can create.</summary>
    public static IReadOnlyList<(RunObjectType Type, string Name)> ObjectsOfRow(IPhoneManagerVariables variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return new List<(RunObjectType, string)>
        {
            (RunObjectType.M365Group, variables.M365Group),
            (RunObjectType.ResourceAccount, variables.RacqUPN),
            (RunObjectType.CallQueue, variables.CqDisplayName),
            (RunObjectType.ResourceAccount, variables.RaaaUPN),
            (RunObjectType.AutoAttendant, variables.AaDisplayName)
        };
    }

    /// <summary>
    /// True when the output has this object's own "created" line and no "already existed" line for
    /// it. A script that used an object that was there says so, and such an object is never the run's.
    /// </summary>
    public static bool WasCreated(string? output, RunObjectType type, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(output) || name.Length == 0)
        {
            return false;
        }

        return type switch
        {
            RunObjectType.M365Group => Has(output, $"{name} created successfully with ID:"),
            RunObjectType.ResourceAccount => Has(output, $"Resource account {name} created successfully"),
            RunObjectType.CallQueue =>
                Has(output, $"SUCCESS: Call queue {name} is ready") && !Has(output, $"Call queue {name} already existed"),
            RunObjectType.AutoAttendant =>
                Has(output, $"SUCCESS: Auto attendant {name} is ready") && !Has(output, $"Auto attendant {name} already existed"),
            _ => false
        };
    }

    /// <summary>The ID a group got, from its own "created" line.</summary>
    public static string GroupIdFrom(string? output, string name)
    {
        var marker = name.Trim() + " created successfully with ID:";
        var text = output ?? string.Empty;
        var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return string.Empty;
        }

        var rest = text[(at + marker.Length)..].TrimStart();
        var id = new string(rest.TakeWhile(character => char.IsAsciiHexDigit(character) || character == '-').ToArray());
        return Guid.TryParse(id, out _) ? id : string.Empty;
    }

    /// <summary>Adds an object to the record when the output proves the run created it. Returns the object, or null.</summary>
    public static RunObject? Record(RunRecord record, RunObjectType type, string name, int step, string? output, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!WasCreated(output, type, name))
        {
            return null;
        }

        var existing = record.Objects.FirstOrDefault(item => item.Type == type && Same(item.Name, name));
        if (existing is not null)
        {
            return existing;
        }

        var created = new RunObject
        {
            Type = type,
            Name = name.Trim(),
            Step = step,
            CreatedUtc = utcNow,
            ObjectId = type == RunObjectType.M365Group ? GroupIdFrom(output, name) : string.Empty
        };
        record.Objects.Add(created);
        record.UpdatedUtc = utcNow;
        return created;
    }

    /// <summary>
    /// The order a rollback removes objects in: what points to an object goes before the object.
    /// Auto attendants, call queues, resource accounts, then the groups.
    /// </summary>
    public static IReadOnlyList<RunObject> RollbackOrder(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Objects
            .Where(item => !item.Removed)
            .OrderBy(item => item.Type switch
            {
                RunObjectType.AutoAttendant => 0,
                RunObjectType.CallQueue => 1,
                RunObjectType.ResourceAccount => 2,
                _ => 3
            })
            .ThenByDescending(item => item.Step)
            .ToList();
    }

    /// <summary>
    /// The read script for the identity of objects. Values travel as one Base64 text. The script
    /// only reads.
    /// </summary>
    /// <param name="effectiveName">How the provisioning scripts write a name; the tenant is searched for that text.</param>
    public static string GetIdentityCommand(IReadOnlyList<RunObject> objects, Func<string, string>? effectiveName = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        string Name(RunObject item) => effectiveName is null ? item.Name : effectiveName(item.Name);
        var request = new
        {
            objects = objects.Select((item, index) => new { index, type = item.Type.ToString(), name = Name(item) }).ToList()
        };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
        return IdentityTemplate.Replace("__REQUEST__", encoded, StringComparison.Ordinal);
    }

    public static IReadOnlyList<RunObjectIdentity> ParseIdentity(string? output)
    {
        var lines = (output ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var json = lines.LastOrDefault(line => line.StartsWith(IdentityMarker, StringComparison.Ordinal));
        if (json is null)
        {
            var error = lines.FirstOrDefault(line => line.StartsWith("ERROR:", StringComparison.Ordinal));
            throw new InvalidDataException(error ?? "The tenant could not be read for the objects of the run.");
        }

        return JsonSerializer.Deserialize<List<RunObjectIdentity>>(json[IdentityMarker.Length..], Options) ?? new List<RunObjectIdentity>();
    }

    /// <summary>
    /// Stores the ID of each object that has none yet, when the tenant shows exactly one object with
    /// its name. Call it right after the step that created the object, in the same session: later
    /// the name alone no longer proves which object it is. Returns true when an ID was stored.
    /// </summary>
    public static bool StoreIds(IReadOnlyList<RunObject> objects, IReadOnlyList<RunObjectIdentity> identities)
    {
        var stored = false;
        for (var index = 0; index < objects.Count; index++)
        {
            var identity = identities.FirstOrDefault(item => item.Index == index);
            if (string.IsNullOrWhiteSpace(objects[index].ObjectId) && identity?.Ids is { Count: 1 } ids && !string.IsNullOrWhiteSpace(ids[0]))
            {
                objects[index].ObjectId = ids[0];
                stored = true;
            }
        }

        return stored;
    }

    /// <summary>Decides for each object, in rollback order, whether it is removed.</summary>
    public static RollbackPlan Assess(IReadOnlyList<RunObject> ordered, IReadOnlyList<RunObjectIdentity> identities)
    {
        var items = new List<RollbackItem>();
        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            var identity = identities.FirstOrDefault(candidate => candidate.Index == index);
            items.Add(Assess(item, identity));
        }

        return new RollbackPlan { Items = items };
    }

    private static RollbackItem Assess(RunObject item, RunObjectIdentity? identity)
    {
        if (identity?.Ids is null)
        {
            return new(item, RollbackAction.Blocked, ReasonUnreadable,
                "The tenant could not be read for this object" + (string.IsNullOrWhiteSpace(identity?.Error) ? "." : ": " + identity.Error));
        }

        if (identity.Ids.Count == 0)
        {
            return new(item, RollbackAction.AlreadyGone, ReasonGone, "The tenant no longer has it.");
        }

        if (identity.Ids.Count > 1)
        {
            return new(item, RollbackAction.Blocked, ReasonAmbiguous,
                $"The tenant has {identity.Ids.Count} objects with this name. The delete script cannot tell them apart.");
        }

        if (string.IsNullOrWhiteSpace(item.ObjectId))
        {
            return new(item, RollbackAction.Blocked, ReasonNoId,
                "The run recorded this object without its ID, so it cannot be confirmed that the object in the tenant is the one the run created. Delete it on its own page.");
        }

        return Same(item.ObjectId, identity.Ids[0])
            ? new(item, RollbackAction.Delete, ReasonOk, "It is the object the run created.")
            : new(item, RollbackAction.Blocked, ReasonOtherId, "The object with this name in the tenant has another ID: it is not the one the run created.");
    }

    /// <summary>The text an approval of a rollback is bound to: what is removed, and under which identity.</summary>
    public static string ApprovedState(RollbackPlan plan)
        => string.Join("\n", plan.Items.Select(item => $"{item.Object.Type}|{item.Object.Name}|{item.Object.ObjectId}|{item.Action}"));

    /// <summary>
    /// One script that removes the objects of a plan in order. Each delete script is the one its
    /// own page uses; a marker line in front of each lets the result be read per object.
    /// </summary>
    public static string GetRollbackCommand(RollbackPlan plan, IPowerShellCommandService commands)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(commands);
        var builder = new StringBuilder();
        for (var index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            if (item.Action != RollbackAction.Delete)
            {
                continue;
            }

            builder.Append("Write-Host '").Append(StepMarker).Append(index).Append('\'').Append('\n');
            builder.Append(item.Object.Type switch
            {
                RunObjectType.AutoAttendant => commands.GetRemoveAutoAttendantCommand(item.Object.Name),
                RunObjectType.CallQueue => commands.GetRemoveCallQueueCommand(item.Object.Name),
                RunObjectType.ResourceAccount => commands.GetRemoveResourceAccountCommand(item.Object.Name),
                _ => commands.GetRemoveM365GroupCommand(item.Object.ObjectId, item.Object.Name)
            }).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads the result of a rollback script per object and writes it to the objects. An object
    /// counts as removed only with a SUCCESS line and no ERROR line in its own part of the output.
    /// </summary>
    public static IReadOnlyList<RollbackOutcome> ApplyOutcome(RollbackPlan plan, string? output, DateTime utcNow)
    {
        var parts = new Dictionary<int, string>();
        var current = -1;
        var text = new StringBuilder();
        foreach (var line in (output ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (line.StartsWith(StepMarker, StringComparison.Ordinal) &&
                int.TryParse(line[StepMarker.Length..].Trim(), out var index))
            {
                if (current >= 0)
                {
                    parts[current] = text.ToString();
                }

                current = index;
                text.Clear();
                continue;
            }

            text.AppendLine(line);
        }

        if (current >= 0)
        {
            parts[current] = text.ToString();
        }

        var outcomes = new List<RollbackOutcome>();
        for (var index = 0; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            switch (item.Action)
            {
                case RollbackAction.AlreadyGone:
                    item.Object.Removed = true;
                    item.Object.RollbackNote = item.Reason;
                    outcomes.Add(new(item.Object, true, item.Reason));
                    break;
                case RollbackAction.Blocked:
                    item.Object.RollbackNote = item.Reason;
                    outcomes.Add(new(item.Object, false, item.Reason));
                    break;
                default:
                    parts.TryGetValue(index, out var part);
                    var lines = (part ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var error = lines.FirstOrDefault(line => line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));
                    var success = lines.LastOrDefault(line => line.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase));
                    var removed = error is null && success is not null;
                    item.Object.Removed = removed;
                    item.Object.RollbackNote = removed ? success! : error ?? "The script did not report a result for this object.";
                    outcomes.Add(new(item.Object, removed, item.Object.RollbackNote));
                    break;
            }
        }

        return outcomes;
    }

    /// <summary>Closes a record after a rollback: rolled back when nothing the run created is left.</summary>
    public static void CloseAfterRollback(RunRecord record, DateTime utcNow)
    {
        record.UpdatedUtc = utcNow;
        if (record.Objects.All(item => item.Removed))
        {
            record.State = RunState.RolledBack;
            record.CompletedSteps.Clear();
        }
    }

    private static bool Has(string text, string line) => text.Contains(line, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? left, string? right)
        => string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private const string IdentityTemplate = """
        try {
            $pdRequest = ConvertFrom-Json ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('__REQUEST__')))
            $pdRows = @()
            foreach ($pdItem in @($pdRequest.objects)) {
                $pdIds = $null
                $pdError = ''
                $pdName = [string]$pdItem.name
                try {
                    switch ([string]$pdItem.type) {
                        'M365Group' {
                            $pdFilter = "displayName eq '" + $pdName.Replace("'", "''") + "'"
                            $pdIds = @(Get-MgGroup -Filter $pdFilter -All -ErrorAction Stop | Where-Object { $_ } | ForEach-Object { [string]$_.Id })
                        }
                        'ResourceAccount' {
                            $pdFilter = "userPrincipalName eq '" + $pdName.Replace("'", "''") + "'"
                            $pdIds = @(Get-MgUser -Filter $pdFilter -Property 'Id,UserPrincipalName' -ErrorAction Stop | Where-Object { $_ } | ForEach-Object { [string]$_.Id })
                        }
                        'CallQueue' {
                            $pdIds = @(Get-CsCallQueue -NameFilter $pdName -WarningAction SilentlyContinue -ErrorAction Stop | Where-Object { $_ -and $_.Name -eq $pdName } | ForEach-Object { [string]$_.Identity })
                        }
                        'AutoAttendant' {
                            $pdIds = @(Get-CsAutoAttendant -NameFilter $pdName -ErrorAction Stop | Where-Object { $_ -and $_.Name -eq $pdName } | ForEach-Object { [string]$_.Identity })
                        }
                    }
                }
                catch {
                    $pdIds = $null
                    $pdError = "$_"
                }
                $pdRows += [ordered]@{
                    index = [int]$pdItem.index
                    ids = $pdIds
                    error = $pdError
                }
            }
            Write-Host ("PDRUNIDENTITY_JSON: " + (ConvertTo-Json -InputObject @($pdRows) -Depth 5 -Compress))
            Write-Host "SUCCESS: Objects of the run read"
        }
        catch {
            Write-Host "ERROR: Failed to read the objects of the run: $_"
        }
        """;
}
