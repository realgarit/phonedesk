using PhoneDesk.Operations;

namespace PhoneDesk.Services;

/// <summary>
/// Keeps each run record as one JSON file. A file is written to a temporary name first and then
/// moved, so a crash while writing leaves the earlier state of the record.
/// </summary>
public sealed class FileRunJournalStore : IRunJournalStore
{
    private readonly string _directory;
    private readonly Lock _gate = new();

    public FileRunJournalStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneDesk", "runs"))
    {
    }

    public FileRunJournalStore(string directory)
    {
        _directory = directory;
    }

    public IReadOnlyList<RunRecord> List()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory))
            {
                return Array.Empty<RunRecord>();
            }

            var records = new List<RunRecord>();
            foreach (var file in Directory.EnumerateFiles(_directory, "run-*.json"))
            {
                try
                {
                    if (RunJournal.Deserialize(File.ReadAllText(file)) is { Id.Length: > 0 } record)
                    {
                        records.Add(record);
                    }
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
                {
                    // A file that cannot be read is left out; the other runs stay usable.
                }
            }

            return records.OrderByDescending(record => record.StartedUtc).ToList();
        }
    }

    public RunRecord? Find(string id)
        => List().FirstOrDefault(record => string.Equals(record.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Save(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Id.Length == 0 || record.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("The run has no usable ID.", nameof(record));
        }

        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"run-{record.Id}.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, RunJournal.Serialize(record));
            File.Move(temporary, path, overwrite: true);
        }
    }
}
