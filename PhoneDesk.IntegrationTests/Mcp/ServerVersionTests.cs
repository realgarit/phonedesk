using PhoneDesk.Mcp.Tools;
using PhoneDesk.Services;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// session_status reported 1.0.0, the version of the server program, which is never set. A live run
/// found it: an AI client could not tell which release it talked to.
/// </summary>
public sealed class ServerVersionTests
{
    [Fact]
    public void SessionStatus_ReportsTheReleaseVersion()
    {
        using var host = new McpTestHost();

        var status = McpTestHost.Parse(host.Tool<SessionTools>().SessionStatus());

        var expected = ConstantsService.Application.Version.Replace("Version ", string.Empty, StringComparison.Ordinal);
        Assert.Equal(expected, status.GetProperty("versions").GetProperty("server").GetString());
    }
}
