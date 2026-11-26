using PhoneDesk.Operations;

namespace PhoneDesk.Services;

/// <summary>Writes holiday backups as JSON files into one directory. A file is never overwritten.</summary>
public sealed class FileHolidayBackupStore : IHolidayBackupStore
{
    private readonly string _directory;

    /// <summary>The store of the desktop app: the backups folder in the local application data of the user.</summary>
    public FileHolidayBackupStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneDesk", "backups"))
    {
    }

    public FileHolidayBackupStore(string directory)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? throw new ArgumentException("The backup directory is missing.", nameof(directory))
            : directory;
    }

    public string Write(HolidayBackupDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Directory.CreateDirectory(_directory);
        var stamp = document.RetrievedAtUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var file = Path.Combine(_directory, $"phonedesk-holiday-backup-{stamp}Z.json");
        for (var number = 2; File.Exists(file); number++)
        {
            file = Path.Combine(_directory, $"phonedesk-holiday-backup-{stamp}Z-{number}.json");
        }

        var temporary = file + ".tmp";
        File.WriteAllText(temporary, HolidayBackup.Serialize(document));
        File.Move(temporary, file);
        return file;
    }
}
