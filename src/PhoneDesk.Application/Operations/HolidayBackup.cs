using System.Text.Json;

namespace PhoneDesk.Operations;

/// <summary>
/// A backup of the holiday call handling of some auto attendants: their call flows, their links to
/// schedules, and the schedules. It is a record for a manual restore and for comparison; it is
/// written before a holiday change when the full tenant backup cannot be taken, and by the
/// desktop app before every holiday change.
/// </summary>
public sealed class HolidayBackupDocument
{
    public const string DocumentKind = "phonedesk.holiday-backup";

    public int SchemaVersion { get; init; } = 1;

    public string Kind { get; init; } = DocumentKind;

    public DateTimeOffset RetrievedAtUtc { get; init; }

    public IReadOnlyList<AutoAttendantCallHandling> AutoAttendants { get; init; } = Array.Empty<AutoAttendantCallHandling>();

    public IReadOnlyList<ScheduleInfo> Schedules { get; init; } = Array.Empty<ScheduleInfo>();
}

/// <summary>A scoped backup with what it holds and what it could not capture.</summary>
public sealed class HolidayBackupResult
{
    public HolidayBackupDocument Document { get; init; } = new();

    public IReadOnlyList<string> MissingAutoAttendants { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingSchedules { get; init; } = Array.Empty<string>();

    public int CallFlows => Document.AutoAttendants.Sum(attendant => attendant.CallFlows.Count);

    public int Links => Document.AutoAttendants.Sum(attendant => attendant.Associations.Count);

    /// <summary>True when every object the change touches is in the backup. A change must not run otherwise.</summary>
    public bool IsComplete => MissingAutoAttendants.Count == 0 && MissingSchedules.Count == 0;

    public string MissingText
        => string.Join(", ", MissingAutoAttendants.Select(id => "auto attendant " + id).Concat(MissingSchedules.Select(id => "schedule " + id)));
}

/// <summary>Writes a holiday backup to a file and returns the path.</summary>
public interface IHolidayBackupStore
{
    string Write(HolidayBackupDocument document);
}

public static class HolidayBackup
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Builds the backup of the given auto attendants and schedules from a state that was read from
    /// the tenant. The schedules every captured auto attendant uses are included. An object that
    /// is required and not in the state is reported as missing.
    /// </summary>
    public static HolidayBackupResult Create(
        HolidayCallHandlingState state,
        IEnumerable<string> requiredAutoAttendantIds,
        IEnumerable<string> requiredScheduleIds,
        DateTimeOffset retrievedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var attendantIds = (requiredAutoAttendantIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var scheduleIds = (requiredScheduleIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var attendants = attendantIds.Select(state.FindAutoAttendant).Where(attendant => attendant is not null).Select(attendant => attendant!).ToList();
        var wantedSchedules = scheduleIds
            .Concat(attendants.SelectMany(attendant => attendant.Associations.Select(link => link.ScheduleId)))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var schedules = wantedSchedules.Select(state.FindSchedule).Where(schedule => schedule is not null).Select(schedule => schedule!).ToList();

        return new HolidayBackupResult
        {
            Document = new HolidayBackupDocument
            {
                RetrievedAtUtc = retrievedAtUtc,
                AutoAttendants = attendants.OrderBy(attendant => attendant.Id, StringComparer.OrdinalIgnoreCase).ToList(),
                Schedules = schedules.OrderBy(schedule => schedule.Id, StringComparer.OrdinalIgnoreCase).ToList()
            },
            MissingAutoAttendants = attendantIds.Where(id => state.FindAutoAttendant(id) is null).ToList(),
            // Only a schedule the change names must be there; a link to a schedule that no longer exists is not a reason to stop.
            MissingSchedules = scheduleIds.Where(id => state.FindSchedule(id) is null).ToList()
        };
    }

    public static string Serialize(HolidayBackupDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static HolidayBackupDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<HolidayBackupDocument>(json, Options);
        if (document is null || !string.Equals(document.Kind, HolidayBackupDocument.DocumentKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The file is not a PhoneDesk holiday backup.");
        }

        return document;
    }
}
