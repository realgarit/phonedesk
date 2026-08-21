using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhoneDesk.Localization;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services;

public sealed class UserPreferencesStore : IUserPreferencesStore
{
    private const string SettingsFileName = "settings.json";
    private readonly string _settingsFilePath;

    public UserPreferencesStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneDesk"))
    {
    }

    public UserPreferencesStore(string settingsDirectory)
    {
        _settingsFilePath = Path.Combine(settingsDirectory, SettingsFileName);
    }

    public AppLanguage? LoadLanguage()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return null;
            }

            using var stream = File.OpenRead(_settingsFilePath);
            var settings = JsonSerializer.Deserialize<UserPreferencesDocument>(stream);
            if (settings?.Language is null)
            {
                return null;
            }

            if (!Enum.TryParse<AppLanguage>(settings.Language, ignoreCase: false, out var language) ||
                !Enum.IsDefined(language))
            {
                return null;
            }

            return language;
        }
        catch
        {
            return null;
        }
    }

    public void SaveLanguage(AppLanguage language)
    {
        var settingsDirectory = Path.GetDirectoryName(_settingsFilePath);
        if (string.IsNullOrWhiteSpace(settingsDirectory))
        {
            return;
        }

        var temporaryFilePath = _settingsFilePath + ".tmp";

        try
        {
            Directory.CreateDirectory(settingsDirectory);

            var json = JsonSerializer.Serialize(new UserPreferencesDocument
            {
                Language = language.ToString()
            });

            using (var stream = new FileStream(temporaryFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryFilePath, _settingsFilePath, overwrite: true);
        }
        catch
        {
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryFilePath))
                {
                    File.Delete(temporaryFilePath);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class UserPreferencesDocument
    {
        [JsonPropertyName("language")]
        public string? Language { get; init; }
    }
}
