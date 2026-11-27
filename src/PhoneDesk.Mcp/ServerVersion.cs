namespace PhoneDesk.Mcp;

/// <summary>
/// The release version of the app, which the MCP server ships with. The server program itself
/// carries no version of its own (its assembly reports 1.0.0), so the handshake and
/// session_status both read this one value.
/// </summary>
internal static class ServerVersion
{
    public static string Text { get; } =
        PhoneDesk.Models.AppVersion.TryParse(PhoneDesk.Services.ConstantsService.Application.Version, out var parsed)
            ? $"{parsed.Major}.{parsed.Minor}.{parsed.Patch}"
            : "0.0.0";
}
