using System.Text.Json;
using PhoneDesk.Mcp;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Operations;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// A customer's holiday list against the tenant, and the table of changes for the customer
/// record. The lists are of the kind customers send: mixed formats, half days, no year.
/// </summary>
public sealed class HolidayListComparisonTests
{
    private const string Main = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Branch = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string Holidays = "11111111-0000-0000-0000-000000000001";
    private const string Closure = "22222222-0000-0000-0000-000000000002";
    private const string BranchHolidays = "33333333-0000-0000-0000-000000000003";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("25.12.2027 Weihnachten", "2027-12-25T00:00:00", "2027-12-26T00:00:00", "", "Weihnachten", false)]
    [InlineData("2027-12-25", "2027-12-25T00:00:00", "2027-12-26T00:00:00", "", "", false)]
    [InlineData("24.12.2027 12:00-24:00 Heiligabend Nachmittag", "2027-12-24T12:00:00", "2027-12-25T00:00:00", "H", "Heiligabend Nachmittag", false)]
    [InlineData("31.12.2027 12:00 - 00:00 Silvester", "2027-12-31T12:00:00", "2028-01-01T00:00:00", "H", "Silvester", false)]
    [InlineData("2027-12-27 to 2027-12-31 Betriebsferien", "2027-12-27T00:00:00", "2028-01-01T00:00:00", "B", "Betriebsferien", false)]
    [InlineData("27.12. bis 02.01. Betriebsferien", "2027-12-27T00:00:00", "2028-01-03T00:00:00", "B", "Betriebsferien", true)]
    [InlineData("G 01.08. Bundesfeier", "2027-08-01T00:00:00", "2027-08-02T00:00:00", "G", "Bundesfeier", true)]
    [InlineData("r 02.01.2028 Berchtoldstag", "2028-01-02T00:00:00", "2028-01-03T00:00:00", "R", "Berchtoldstag", false)]
    [InlineData("25.12.2027 - Weihnachten", "2027-12-25T00:00:00", "2027-12-26T00:00:00", "", "Weihnachten", false)]
    public void List_IsReadInTheFormsCustomersWrite(string line, string start, string end, string marker, string wording, bool yearAssumed)
    {
        var list = HolidayListComparer.Parse(line, 2027);

        Assert.Empty(list.Unreadable);
        var entry = Assert.Single(list.Entries);
        Assert.Equal(DateTime.Parse(start, System.Globalization.CultureInfo.InvariantCulture), entry.Window.Start);
        Assert.Equal(DateTime.Parse(end, System.Globalization.CultureInfo.InvariantCulture), entry.Window.End);
        Assert.Equal(marker, entry.Marker);
        Assert.Equal(wording, entry.Wording);
        Assert.Equal(yearAssumed, entry.YearWasAssumed);
        Assert.Equal(line, entry.Original);
    }

    [Fact]
    public void List_ReportsLinesItCannotReadAndTheYearsItSpans()
    {
        var list = HolidayListComparer.Parse(
            "# Feiertage\n25.12.2027 Weihnachten\n\nOstern (Datum folgt)\n31.02.2027 gibt es nicht\n26.12.2027 08:00-07:00 falsch\n01.01.2028 Neujahr",
            2027);

        Assert.Equal(2, list.Entries.Count);
        Assert.Equal(new[] { 2027, 2028 }, list.Years);
        Assert.Equal(new[] { 4, 5, 6 }, list.Unreadable.Select(line => line.LineNumber));
    }

    [Fact]
    public void Comparison_FindsMatchCoverSameStartOverlapAndMissingWhateverTheSchedulesAreCalled()
    {
        var list = HolidayListComparer.Parse(
            "25.12.2027 Weihnachten\n" +
            "01.01.2028 Neujahr\n" +
            "24.12.2027 12:00-24:00 Heiligabend\n" +
            "26.12.2027 Stephanstag\n" +
            "01.08.2027 Bundesfeier",
            2027);

        var comparison = HolidayListComparer.Compare(HolidayCallHandling.ParseState(State(afterWork: false)), list, new[] { Main });
        var status = comparison.Rows.ToDictionary(row => row.Entry.Wording, row => row.Status);

        Assert.Equal(HolidayListStatus.Match, status["Weihnachten"]);
        // The company holidays over the turn of the year contain New Year.
        Assert.Equal(HolidayListStatus.Covered, status["Neujahr"]);
        // A schedule closes the whole of 24 December: the same start, another end.
        Assert.Equal(HolidayListStatus.SameStart, status["Stephanstag"]);
        Assert.Equal(HolidayListStatus.PartialOverlap, status["Heiligabend"]);
        Assert.Equal(HolidayListStatus.Missing, status["Bundesfeier"]);
        Assert.Contains("Betriebsferien", comparison.Rows.Single(row => row.Entry.Wording == "Neujahr").Schedule!.Name);

        // What the tenant has in the period and the customer did not name.
        var extra = Assert.Single(comparison.OnlyInTenant);
        Assert.Equal(new DateTime(2027, 11, 1), extra.Window.Start);
    }

    [Fact]
    public void Comparison_ShowsDifferentCallHandlingOfTheSameHolidayPerAutoAttendant()
    {
        var list = HolidayListComparer.Parse("25.12.2027 Weihnachten", 2027);

        var rows = HolidayListComparer.Compare(HolidayCallHandling.ParseState(State(afterWork: false)), list).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Contains("Wir sind geschlossen", rows.Single(row => row.AutoAttendantName == "Main AA").CallHandling);
        Assert.Contains("Notfallnummer", rows.Single(row => row.AutoAttendantName == "Branch AA").CallHandling);
    }

    [Fact]
    public void ChangeTable_CountsOnlyWhatTheTenantShowsNowAndDidNotShowBefore()
    {
        var list = HolidayListComparer.Parse(
            "25.12.2027 Weihnachten\n01.01.2028 Neujahr\nG 01.08.2027 Bundesfeier\n15.08.2027 Mariä Himmelfahrt",
            2027);
        var before = HolidayCallHandling.ParseState(State(afterWork: false));
        var now = HolidayCallHandling.ParseState(State(afterWork: true));

        var rows = HolidayListComparer.Changes(before, now, list, new[] { Main });
        var result = rows.ToDictionary(row => row.Entry.Wording, row => row);

        Assert.Equal(HolidayChangeResult.NoChange, result["Weihnachten"].Result);
        Assert.Equal(HolidayChangeResult.NoChange, result["Neujahr"].Result);
        // New Year lies inside the company holidays that exist: W.
        Assert.Equal("W", result["Neujahr"].Markers);
        Assert.Equal(HolidayChangeResult.Done, result["Bundesfeier"].Result);
        Assert.Equal("G", result["Bundesfeier"].Markers);
        Assert.Equal(HolidayChangeResult.Pending, result["Mariä Himmelfahrt"].Result);

        var table = HolidayListComparer.ToTable(rows).Split('\n');
        Assert.StartsWith("Result\tMarkers\tFrom\tTo\tCustomer wording", table[0]);
        Assert.Contains(table, line => line.StartsWith("done\tG\t2027-08-01 00:00\t2027-08-02 00:00\tG 01.08.2027 Bundesfeier\tMain AA", StringComparison.Ordinal));
        Assert.Contains(table, line => line.StartsWith("pending\t\t2027-08-15 00:00", StringComparison.Ordinal));
        Assert.Contains(table, line => line.StartsWith("no change\tW\t2028-01-01 00:00", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tools_CompareTheListAndWriteTheChangeTableFromABackup()
    {
        using var host = McpHolidayCallHandlingTests.HostWith(State(afterWork: false));
        Respond(host, State(afterWork: false));
        var tools = host.Tool<HolidayListTools>();
        const string list = "25.12.2027 Weihnachten\n01.08. Bundesfeier\nirgendwann im Herbst";

        var compared = McpTestHost.Parse(await tools.CompareHolidayList(list, 2027, new[] { Main }, Cancellation));
        Assert.Equal(2, compared.GetProperty("entries").GetInt32());
        Assert.Equal("01.08. Bundesfeier", compared.GetProperty("yearAssumedFor")[0].GetString());
        Assert.Equal(3, compared.GetProperty("unreadableLines")[0].GetProperty("lineNumber").GetInt32());
        Assert.Equal(1, compared.GetProperty("summary").GetProperty("Match").GetInt32());
        Assert.Equal(1, compared.GetProperty("summary").GetProperty("Missing").GetInt32());

        // No backup from before the work: the table cannot be written.
        var noBackup = McpTestHost.Parse(await tools.HolidayChangeTable(list, 2027, cancellationToken: Cancellation));
        Assert.Contains("No holiday backup from before the work was found", noBackup.GetProperty("error").GetString());

        // A file that is not a holiday backup of this tenant is not read, wherever it is.
        var other = Path.Combine(host.DataDirectory, "configuration.json");
        await File.WriteAllTextAsync(other, "{}", Cancellation);
        foreach (var path in new[] { other, Path.Combine(host.DataDirectory, "backups", "..", "configuration.json"), @"C:\Windows\win.ini" })
        {
            var refused = McpTestHost.Parse(await tools.HolidayChangeTable(list, 2027, backupFile: path, cancellationToken: Cancellation));
            Assert.Contains("is not a holiday backup of the connected tenant", refused.GetProperty("error").GetString());
        }

        // A backup before the work, then the work, then the table.
        var backup = McpTestHost.Parse(await host.Tool<HolidayChangeTools>().BackupHolidayCallHandling(new[] { Main }, Cancellation));
        Respond(host, State(afterWork: true));
        var table = McpTestHost.Parse(await tools.HolidayChangeTable(list, 2027, cancellationToken: Cancellation));
        var named = McpTestHost.Parse(await tools.HolidayChangeTable(
            list, 2027, backupFile: backup.GetProperty("backup").GetProperty("file").GetString(), cancellationToken: Cancellation));
        Assert.Equal(1, named.GetProperty("done").GetInt32());

        Assert.Equal(backup.GetProperty("backup").GetProperty("file").GetString(), table.GetProperty("before").GetProperty("file").GetString());
        Assert.Equal(1, table.GetProperty("done").GetInt32());
        Assert.Equal(1, table.GetProperty("noChange").GetInt32());
        Assert.Equal(0, table.GetProperty("pending").GetInt32());
        Assert.Contains("done\t\t2027-08-01 00:00\t2027-08-02 00:00\t01.08. Bundesfeier\tMain AA", table.GetProperty("table").GetString());
    }

    private static void Respond(McpTestHost host, string state)
    {
        foreach (var ids in new string[]?[] { null, new[] { Main }, new[] { Main, Branch } })
        {
            foreach (var include in new[] { true, false })
            {
                foreach (var check in new[] { true, false })
                {
                    host.PowerShell.Respond(HolidayCallHandling.GetReadCommand(ids, include, check), state);
                }
            }
        }
    }

    /// <summary>
    /// Two auto attendants. The main one has Christmas, company holidays over the turn of the year,
    /// a closure of the whole of 24 December that starts when Stephanstag does not, and a day in
    /// November. After the work it also has 1 August.
    /// </summary>
    private static string State(bool afterWork)
    {
        static object Flow(string id, string greeting) => new
        {
            id,
            name = "flow",
            greetings = new[] { new { type = "TextToSpeech", text = greeting, audioFile = (object?)null } },
            menuName = "menu",
            menuPrompts = Array.Empty<object>(),
            options = Array.Empty<object>(),
            forceListenMenu = false,
            dialByName = false
        };
        static object Link(string schedule, string flow) => new { type = "Holiday", scheduleId = schedule, callFlowId = flow, enabled = true, priority = 0 };
        static object Window(string start, string end) => new { start, end };

        var mainWindows = new List<object>
        {
            Window("2027-12-25T00:00:00", "2027-12-26T00:00:00"),
            Window("2027-12-26T00:00:00", "2027-12-26T12:00:00"),
            Window("2027-12-24T00:00:00", "2027-12-24T18:00:00"),
            Window("2027-11-01T00:00:00", "2027-11-02T00:00:00")
        };
        if (afterWork)
        {
            mainWindows.Add(Window("2027-08-01T00:00:00", "2027-08-02T00:00:00"));
        }

        return HolidayCallHandling.JsonMarker + JsonSerializer.Serialize(new
        {
            autoAttendants = new object[]
            {
                new
                {
                    id = Main,
                    name = "Main AA",
                    authorizedUsers = Array.Empty<string>(),
                    callFlows = new[] { Flow("f1", "Wir sind geschlossen."), Flow("f2", "Betriebsferien.") },
                    associations = new[] { Link(Holidays, "f1"), Link(Closure, "f2") }
                },
                new
                {
                    id = Branch,
                    name = "Branch AA",
                    authorizedUsers = Array.Empty<string>(),
                    callFlows = new[] { Flow("f3", "Bitte wählen Sie die Notfallnummer.") },
                    associations = new[] { Link(BranchHolidays, "f3") }
                }
            },
            schedules = new object[]
            {
                new { id = Holidays, name = "hd-2027", type = "Fixed", windows = mainWindows, usedBy = new[] { Main } },
                new { id = Closure, name = "Betriebsferien Winter", type = "Fixed", windows = new[] { Window("2027-12-27T00:00:00", "2028-01-03T00:00:00") }, usedBy = new[] { Main } },
                new { id = BranchHolidays, name = "Filiale", type = "Fixed", windows = new[] { Window("2027-12-25T00:00:00", "2027-12-26T00:00:00") }, usedBy = new[] { Branch } }
            },
            missingAuthorizedUsers = Array.Empty<object>(),
            uncheckedAuthorizedUsers = Array.Empty<object>(),
            disabledAuthorizedUsers = Array.Empty<object>()
        }) + "\nSUCCESS: Holiday call handling retrieved";
    }
}
