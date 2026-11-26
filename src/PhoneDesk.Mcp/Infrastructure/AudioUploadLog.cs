using System.Security.Cryptography;
using System.Text.Json;

namespace PhoneDesk.Mcp;

/// <summary>One upload of an audio file by this server.</summary>
internal sealed record AudioUpload(string Sha256, string FileName, string Application, string AudioFileId, string TenantId, DateTimeOffset UploadedUtc);

/// <summary>
/// Remembers which local audio content this server uploaded, for which application and tenant.
/// Teams converts an upload, so a file cannot be recognised by downloading and comparing it; the
/// hash of the local file is the only way to tell "the same recording again" from "another
/// recording with the same file name".
/// </summary>
internal sealed class AudioUploadLog
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _file;
    private readonly Lock _gate = new();

    public AudioUploadLog(McpEnvironment environment)
    {
        _file = Path.Combine(environment.DataDirectory, "audio-uploads.json");
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Earlier uploads of the same content for the same application and tenant, newest first.</summary>
    public IReadOnlyList<AudioUpload> Find(string sha256, string application, string tenantId)
        => Read()
            .Where(upload => string.Equals(upload.Sha256, sha256, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(upload.Application, application, StringComparison.Ordinal) &&
                             string.Equals(upload.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(upload => upload.UploadedUtc)
            .ToList();

    public void Add(AudioUpload upload)
    {
        lock (_gate)
        {
            var all = Read().ToList();
            all.Add(upload);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temporary = _file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Options));
            File.Move(temporary, _file, overwrite: true);
        }
    }

    private IReadOnlyList<AudioUpload> Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_file))
            {
                return Array.Empty<AudioUpload>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<AudioUpload>>(File.ReadAllText(_file), Options) ?? new List<AudioUpload>();
            }
            catch (JsonException)
            {
                // A damaged log must not stop an upload: it only means earlier uploads are not recognised.
                return Array.Empty<AudioUpload>();
            }
        }
    }
}
