using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneDesk.Mcp;

/// <summary>Builds the JSON text every tool returns.</summary>
internal static class McpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    public static string Error(string message, string? details = null)
        => Serialize(new { success = false, error = message, details });

    /// <summary>Runs a tool body and turns an exception into an error result the client can read.</summary>
    public static async Task<string> RunAsync(Func<Task<object>> body)
    {
        try
        {
            return Serialize(await body());
        }
        catch (OperationCanceledException)
        {
            return Error("The operation was cancelled.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException
                                       or IOException or UnauthorizedAccessException or JsonException)
        {
            return Error(ex.Message);
        }
    }

    public static string Run(Func<object> body)
    {
        try
        {
            return Serialize(body());
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException
                                       or IOException or UnauthorizedAccessException or JsonException)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>Adds one value to a result and keeps the fields of the result where they are.</summary>
    public static object With(object result, string name, object? value)
    {
        var node = JsonSerializer.SerializeToNode(result, Options)!.AsObject();
        node[name] = JsonSerializer.SerializeToNode(value, Options);
        return node;
    }

    /// <summary>Keeps long script output inside a size an AI client can read.</summary>
    public static string? Truncate(string? value, int maxLength = 6000)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength] + $"\n… ({value.Length - maxLength} more characters not shown)";
}
