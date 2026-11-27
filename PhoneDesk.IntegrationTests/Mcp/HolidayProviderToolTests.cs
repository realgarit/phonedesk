using PhoneDesk.Mcp;
using PhoneDesk.Mcp.Tools;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>The holiday lists of other countries and the calendar file import, through the MCP tools.</summary>
public sealed class HolidayProviderToolTests
{
    [Fact]
    public void PublicHolidays_AskForTheRegionAndAddOnlyWhatIsLeftIn()
    {
        using var host = new McpTestHost().Configured();
        var tools = host.Tool<HolidayProviderTools>();

        var countries = McpTestHost.Parse(tools.PublicHolidays());
        Assert.Equal(new[] { "DE", "AT", "FR", "GB", "US" }, countries.GetProperty("countries").EnumerateArray().Select(item => item.GetProperty("code").GetString()));

        // The state decides in Germany: nothing is listed or written without it.
        var question = McpTestHost.Parse(tools.ApplyPublicHolidays("DE", 2026));
        Assert.True(question.GetProperty("regionRequired").GetBoolean());
        Assert.False(question.GetProperty("applied").GetBoolean());
        Assert.Empty(host.Get<ConfigurationStore>().Variables.HolidaySeries);

        var listed = McpTestHost.Parse(tools.PublicHolidays("Germany", 2026, "by"));
        Assert.Equal(12, listed.GetProperty("holidays").GetArrayLength());
        Assert.Equal("Public holiday", listed.GetProperty("holidays")[0].GetProperty("label").GetString());

        var applied = McpTestHost.Parse(tools.ApplyPublicHolidays("DE", 2026, "BY", new[] { "2026-01-06" }));
        Assert.Equal(11, applied.GetProperty("added").GetArrayLength());
        Assert.Equal(1, applied.GetProperty("source").GetProperty("leftOut").GetInt32());
        var again = McpTestHost.Parse(tools.ApplyPublicHolidays("DE", 2026, "BY", new[] { "2026-01-06" }));
        Assert.Equal(0, again.GetProperty("added").GetArrayLength());
        Assert.Equal(11, again.GetProperty("alreadyPresent").GetInt32());

        Assert.Contains("swiss_holidays", McpTestHost.Parse(tools.PublicHolidays("CH", 2026)).GetProperty("error").GetString());
        Assert.Contains("not a region", McpTestHost.Parse(tools.PublicHolidays("GB", 2026, "ZH")).GetProperty("error").GetString());
        Assert.Contains("outside that range", McpTestHost.Parse(tools.PublicHolidays("US", 2040)).GetProperty("error").GetString());
        Assert.Contains("yyyy-MM-dd", McpTestHost.Parse(tools.ApplyPublicHolidays("US", 2026, null, new[] { "6.1.2026" })).GetProperty("error").GetString());
    }

    [Fact]
    public void CalendarFile_IsListedFirstAndOnlyAnIcsFileIsRead()
    {
        using var host = new McpTestHost().Configured();
        var tools = host.Tool<HolidayProviderTools>();
        var file = Path.Combine(host.DataDirectory, "closures.ics");
        File.WriteAllText(file,
            "BEGIN:VCALENDAR\r\n" +
            "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20270101\r\nSUMMARY:Nieuwjaarsdag\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20271227\r\nDTEND;VALUE=DATE:20280103\r\nSUMMARY:Betriebsferien\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nDTSTART;VALUE=DATE:20270427\r\nEND:VEVENT\r\n" +
            "END:VCALENDAR\r\n");

        var listed = McpTestHost.Parse(tools.ImportHolidayCalendar(file, 2027));
        Assert.False(listed.GetProperty("applied").GetBoolean());
        Assert.Equal(3, listed.GetProperty("events").GetArrayLength());
        Assert.Empty(host.Get<ConfigurationStore>().Variables.HolidaySeries);

        // An event without a title is not written without a decision.
        Assert.Contains("no title", McpTestHost.Parse(tools.ImportHolidayCalendar(file, 2027, apply: true)).GetProperty("error").GetString());

        var applied = McpTestHost.Parse(tools.ImportHolidayCalendar(file, 2027, apply: true, excludeDates: new[] { "2027-04-27" }));
        Assert.Equal(2, applied.GetProperty("added").GetArrayLength());
        var closure = host.Get<ConfigurationStore>().Variables.HolidaySeries.Single(entry => entry.Name == "Betriebsferien");
        Assert.Equal(new DateTime(2028, 1, 3), closure.EndDateTime);

        var other = Path.Combine(host.DataDirectory, "configuration.json");
        File.WriteAllText(other, "{}");
        Assert.Contains("Only .ics files", McpTestHost.Parse(tools.ImportHolidayCalendar(other)).GetProperty("error").GetString());

        // A path on another computer is refused before anything is opened.
        foreach (var remote in new[] { @"\\server\share\closures.ics", "//server/share/closures.ics", @"\\?\UNC\server\share\closures.ics", "file://server/share/closures.ics" })
        {
            Assert.True(McpFiles.IsNetworkOrDevicePath(remote));
            Assert.Contains("not a network or device path", McpTestHost.Parse(tools.ImportHolidayCalendar(remote)).GetProperty("error").GetString());
        }
    }
}
