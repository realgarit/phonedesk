using System.Text.Json;

namespace PhoneDesk.Operations;

/// <summary>An audio file Teams holds for one application.</summary>
public sealed record AudioFileRow(string Id, string FileName, string Application, bool MarkedForDeletion = false);

/// <summary>
/// Audio files in Teams. A file is uploaded for one application and can be used only there: a
/// greeting of an auto attendant needs a file of the application OrgAutoAttendant, a greeting or
/// the music on hold of a call queue a file of HuntGroup. The ID identifies a file; two uploads can
/// have the same file name.
/// </summary>
public static class AudioFiles
{
    public const string AutoAttendant = "OrgAutoAttendant";
    public const string CallQueue = "HuntGroup";
    public const string JsonMarker = "PDAUDIO_JSON: ";

    public static readonly IReadOnlyList<string> Applications = new[] { AutoAttendant, CallQueue };

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads the application from what an operator writes: the Teams name, or "autoAttendant" / "callQueue".</summary>
    public static string NormalizeApplication(string? value)
    {
        var text = (value ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (string.Equals(text, AutoAttendant, StringComparison.OrdinalIgnoreCase) || string.Equals(text, "autoAttendant", StringComparison.OrdinalIgnoreCase))
        {
            return AutoAttendant;
        }

        if (string.Equals(text, CallQueue, StringComparison.OrdinalIgnoreCase) || string.Equals(text, "callQueue", StringComparison.OrdinalIgnoreCase))
        {
            return CallQueue;
        }

        throw new ArgumentException(
            $"'{value}' is not an audio application. Use 'autoAttendant' (Teams name {AutoAttendant}) for a greeting of an auto attendant, " +
            $"or 'callQueue' (Teams name {CallQueue}) for a greeting or the music on hold of a call queue.");
    }

    /// <summary>What the application is for, in words.</summary>
    public static string Describe(string application)
        => string.Equals(application, AutoAttendant, StringComparison.Ordinal) ? "auto attendants" : "call queues";

    /// <summary>Lists the audio files of one application. The script only reads.</summary>
    public static string GetListCommand(string application)
        => ListTemplate.Replace("__APP__", NormalizeApplication(application), StringComparison.Ordinal);

    public static IReadOnlyList<AudioFileRow> Parse(string? output)
    {
        var lines = (output ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var json = lines.LastOrDefault(line => line.StartsWith(JsonMarker, StringComparison.Ordinal));
        if (json is null)
        {
            var error = lines.FirstOrDefault(line => line.StartsWith("ERROR:", StringComparison.Ordinal));
            throw new InvalidDataException(error ?? "The audio files could not be read: the tenant returned no data.");
        }

        return JsonSerializer.Deserialize<List<AudioFileRow>>(json[JsonMarker.Length..], Options) ?? new List<AudioFileRow>();
    }

    private const string ListTemplate = """
        try {
            $pdRows = @()
            foreach ($pdFile in @(Get-CsOnlineAudioFile -ApplicationId __APP__ -ErrorAction Stop)) {
                if (-not $pdFile) { continue }
                $pdRows += @([ordered]@{
                    id = [string]$pdFile.Id
                    fileName = [string]$pdFile.FileName
                    application = '__APP__'
                    markedForDeletion = [bool]$pdFile.MarkedForDeletion
                })
            }
            Write-Host ("PDAUDIO_JSON: " + (ConvertTo-Json -InputObject @($pdRows) -Depth 4 -Compress))
            Write-Host "SUCCESS: Audio files retrieved"
        }
        catch {
            Write-Host "ERROR: Failed to read the audio files: $_"
        }
        """;
}
