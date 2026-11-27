using System.IO.Compression;
using System.Xml.Linq;
using PhoneDesk.Mcp.Tools;
using PhoneDesk.Operations;
using PhoneDesk.Portability;
using PhoneDesk.Topology;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.IntegrationTests.Mcp;

/// <summary>
/// The readable exports of a snapshot: the HTML report, the workbook and the call-flow diagrams.
/// They are built from the snapshot alone, and the same snapshot gives the same bytes.
/// </summary>
public sealed class RichReportTests
{
    /// <summary>
    /// One auto attendant with a menu: key 1 goes to the support call queue (through its resource
    /// account), key 2 to an outside number. It has an after-hours and a holiday schedule.
    /// </summary>
    private static TopologySnapshotDocument Snapshot(bool withDocumentation = true)
    {
        var documentation = new TenantDocumentationSnapshot(
            new TenantInformationSnapshot("Contoso <AG>", "tenant-1", "CH", "de-CH"),
            new[]
            {
                new ResourceAccountInventorySnapshot("RA Main", "raaa-main@contoso.com", "ra-aa", "app-aa", "+41441110000",
                    new[] { new ResourceAccountAssociationSnapshot("aa-id-1", "AutoAttendant", 0) }),
                new ResourceAccountInventorySnapshot("RA Support", "racq-support@contoso.com", "ra-cq", "app-cq", string.Empty,
                    new[] { new ResourceAccountAssociationSnapshot("cq-id-1", "CallQueue", 0) })
            },
            new[] { new AutoAttendantInventorySnapshot("Main AA", "aa-id-1", "de-DE", "W. Europe Standard Time", "Female", "Main menu") },
            new[]
            {
                new AutoAttendantMenuOptionSnapshot("Main AA", "aa-id-1", "DefaultCallFlow", "Tone1", "TransferCallToTarget", "ra-cq", 0),
                new AutoAttendantMenuOptionSnapshot("Main AA", "aa-id-1", "DefaultCallFlow", "Tone2", "TransferCallToTarget", "tel:+41449990000", 0),
                new AutoAttendantMenuOptionSnapshot("Main AA", "aa-id-1", "After hours", "Automatic", "DisconnectCall", string.Empty, 0)
            },
            new[] { new AutoAttendantCallFlowSnapshot("Main AA", "aa-id-1", "After hours", "Closed", 0) },
            new[]
            {
                new AutoAttendantScheduleAssociationSnapshot("Main AA", "aa-id-1", "AfterHours", "sched-hours", "flow-1", 0),
                new AutoAttendantScheduleAssociationSnapshot("Main AA", "aa-id-1", "Holiday", "sched-holiday", "flow-2", 0)
            },
            Array.Empty<AutoAttendantOperatorSnapshot>(),
            new[] { new CallQueueInventorySnapshot("Support CQ", "cq-id-1", "Attendant", "30", "de-DE", 1, "50", "1200", "Disconnect", "Forward") },
            new[] { new CallQueueAgentSnapshot("Support CQ", "cq-id-1", "agent-1", "True", "Anna Muster", "anna@contoso.com", 0) },
            new[] { new CallQueueDistributionListSnapshot("Support CQ", "cq-id-1", "grp-1", 0) },
            new[] { new QueueThresholdActionSnapshot("Support CQ", "cq-id-1", "Timeout", "Forward", "ra-aa", "1200", 0) },
            new[]
            {
                new ScheduleInventorySnapshot("Opening hours", "sched-hours", "WeeklyRecurrence", 0),
                new ScheduleInventorySnapshot("Feiertage 2027", "sched-holiday", "Fixed", 1)
            },
            new[] { new ScheduleDateRangeSnapshot("Feiertage 2027", "sched-holiday", "25/12/2027 00:00", "26/12/2027 00:00", 0) },
            new[]
            {
                new ScheduleWeeklyRangeSnapshot("Opening hours", "sched-hours", "Tuesday", "08:00", "17:00", 0),
                new ScheduleWeeklyRangeSnapshot("Opening hours", "sched-hours", "Monday", "08:00", "17:00", 0)
            },
            new[] { new PhoneNumberInventorySnapshot("+41441110000", "DirectRouting", "raaa-main@contoso.com", "VoiceApplicationAssigned", "Activated", "Zürich", "VoiceApplicationAssignment") },
            new[] { new VoiceUserInventorySnapshot("Anna Muster", "anna@contoso.com", "tel:+41441110001", "CH-National", "Default", "CH") });

        return new TopologySnapshotDocument(
            2,
            "phonedesk.topology",
            new DateTimeOffset(2027, 3, 1, 8, 30, 0, TimeSpan.Zero),
            new[] { new TopologyAutoAttendantSnapshot("Main AA", "aa-id-1", "de-DE", "W. Europe Standard Time", new[] { "ra-aa" }, new[] { "sched-holiday" }, new[] { "ra-cq" }) },
            new[] { new TopologyCallQueueSnapshot("Support CQ", "cq-id-1", "Attendant", 30, new[] { "agent-1" }, new[] { "grp-1" }, new[] { "ra-cq" }) },
            new[]
            {
                new TopologyResourceAccountSnapshot("RA Main", "raaa-main@contoso.com", "ra-aa", "+41441110000", ResourceAccountKind.AutoAttendant, true),
                new TopologyResourceAccountSnapshot("RA Support", "racq-support@contoso.com", "ra-cq", null, ResourceAccountKind.CallQueue, true)
            },
            new[] { new TopologyGroupSnapshot("Support Group", "grp-1", "support", "Support team") },
            withDocumentation ? documentation : null);
    }

    [Fact]
    public void Diagram_ShowsTheMenuWithTheNamesBehindTheIds()
    {
        var diagram = Assert.Single(RichReport.BuildDiagrams(Snapshot()));

        var root = diagram.Boxes[0];
        Assert.Equal("Main AA", root.Title);
        Assert.Equal("+41441110000", root.Detail);
        // Key 1 points to a resource account: the diagram names the call queue it serves.
        var targets = diagram.Boxes.Where(box => box.Kind == "target").ToList();
        Assert.Equal(3, targets.Count);
        Assert.Equal("Support CQ", targets[0].Detail);
        Assert.StartsWith("Key 1:", targets[0].Title);
        Assert.Equal("tel:+41449990000", targets[1].Detail);
        Assert.Contains(diagram.Boxes, box => box.Kind == "holiday" && box.Detail == "Feiertage 2027");
        Assert.Contains(diagram.Boxes, box => box.Kind == "afterhours" && box.Detail == "Opening hours");
        // One line from the root to each flow and schedule, one from a flow to each of its options.
        Assert.Equal(4 + 3, diagram.Lines.Count);
        Assert.All(diagram.Boxes, box => Assert.True(box.X >= 0 && box.Y >= 0 && box.X + box.Width <= diagram.Width && box.Y + box.Height <= diagram.Height));

        var svg = XDocument.Parse(RichReport.ToSvg(diagram));
        Assert.Equal("svg", svg.Root!.Name.LocalName);
        Assert.Equal(diagram.Boxes.Count + 1, svg.Root.Elements().Count(element => element.Name.LocalName == "rect"));
        Assert.Equal("call-flow-01-main-aa", RichReport.DiagramFileName(diagram, 0));
        Assert.Contains(RichReport.BuildDiagrams(Snapshot(), RichReport.German).Single().Boxes, box => box.Title == "Öffnungszeiten");
    }

    [Fact]
    public void Html_IsOneFileWithLinksBetweenTheObjectsAndNothingFromTheNetwork()
    {
        var html = RichReport.BuildHtml(Snapshot());

        // The same snapshot gives the same text.
        Assert.Equal(html, RichReport.BuildHtml(Snapshot()));
        // A name is text, never markup.
        Assert.Contains("Contoso &lt;AG&gt;", html);
        Assert.DoesNotContain("Contoso <AG>", html);
        // The auto attendant links to its resource account and its schedules; the resource account links back.
        Assert.Contains("<article id=\"aa-aa-id-1\">", html);
        Assert.Contains("<a href=\"#ra-ra-aa\">raaa-main@contoso.com</a>", html);
        Assert.Contains("<a href=\"#sch-sched-holiday\">Feiertage 2027</a>", html);
        Assert.Contains("<a href=\"#aa-aa-id-1\">Main AA</a>", html);
        Assert.Contains("<a href=\"#grp-grp-1\">Support Group</a>", html);
        // The timeout of the call queue goes to the auto attendant behind the resource account.
        Assert.Contains("&rarr; <a href=\"#aa-aa-id-1\">Main AA</a>", html);
        // The diagram is inside the file.
        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\"", html);
        // Nothing is loaded from elsewhere, and nothing runs.
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        // Weekdays are in the order of the week.
        Assert.True(html.IndexOf("<td>Monday</td>", StringComparison.Ordinal) < html.IndexOf("<td>Tuesday</td>", StringComparison.Ordinal));

        Assert.Contains("Dokumentation Teams-Telefonie", RichReport.BuildHtml(Snapshot(), RichReport.German));
        Assert.Contains("lang=\"de\"", RichReport.BuildHtml(Snapshot(), "DE"));
        var older = Assert.Throws<InvalidOperationException>(() => RichReport.BuildHtml(Snapshot(withDocumentation: false)));
        Assert.Contains("Take a new full snapshot", older.Message);
    }

    [Fact]
    public void Workbook_HasOneFilterableSheetPerKindOfObject()
    {
        var bytes = RichReport.BuildWorkbook(Snapshot());
        Assert.Equal(bytes, RichReport.BuildWorkbook(Snapshot()));

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        XDocument Part(string path)
        {
            using var stream = archive.GetEntry(path)!.Open();
            return XDocument.Load(stream);
        }

        var sheets = Part("xl/workbook.xml").Descendants().Where(element => element.Name.LocalName == "sheet").Select(element => element.Attribute("name")!.Value).ToList();
        Assert.Equal(
            new[] { "Auto attendants", "Menu options", "Call queues", "Agents", "Resource accounts", "Groups", "Schedules", "Phone numbers", "Voice users" },
            sheets);
        Assert.Equal(sheets.Count, archive.Entries.Count(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)));

        // The menu options: a header row that stays in view, a filter over all columns, and the names behind the IDs.
        var menu = Part("xl/worksheets/sheet2.xml");
        var cells = menu.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value).ToList();
        Assert.Equal(new[] { "Auto attendant", "Call flow", "Key", "Action", "Target" }, cells.Take(5));
        Assert.Contains("Support CQ", cells);
        Assert.Equal("A1:E4", menu.Descendants().Single(element => element.Name.LocalName == "autoFilter").Attribute("ref")!.Value);
        Assert.Equal("frozen", menu.Descendants().Single(element => element.Name.LocalName == "pane").Attribute("state")!.Value);
        // A phone number stays text: it is not turned into a number.
        Assert.All(Part("xl/worksheets/sheet8.xml").Descendants().Where(element => element.Name.LocalName == "c"), cell => Assert.Equal("inlineStr", cell.Attribute("t")!.Value));
    }

    [Fact]
    public void Tool_WritesTheReportFromASnapshotFileWithoutTheTenant()
    {
        using var host = new McpTestHost();
        var file = Path.Combine(host.DataDirectory, "snapshot.json");
        File.WriteAllText(file, host.Get<ITenantAsCodeService>().SerializeTopology(Snapshot()));
        var tools = host.Tool<HealthAndBackupTools>();

        var result = McpTestHost.Parse(tools.ExportTenantReport(file, "de"));

        var files = result.GetProperty("files").EnumerateArray().Select(item => item.GetString()!).ToList();
        Assert.Equal(new[] { "report.html", "report.xlsx", "call-flow-01-main-aa.svg" }, files.Select(Path.GetFileName));
        Assert.All(files, path => Assert.True(new FileInfo(path).Length > 0));
        Assert.StartsWith(Path.Combine(host.DataDirectory, "reports"), result.GetProperty("folder").GetString());
        Assert.Contains("Automatische Telefonzentralen", File.ReadAllText(files[0]));
        // Not connected, and nothing was sent.
        Assert.Empty(host.PowerShell.Executed);

        // A second export does not overwrite the first.
        var again = McpTestHost.Parse(tools.ExportTenantReport(file));
        Assert.NotEqual(result.GetProperty("folder").GetString(), again.GetProperty("folder").GetString());
        Assert.Contains("en or de", McpTestHost.Parse(tools.ExportTenantReport(file, "fr")).GetProperty("error").GetString());
        Assert.Contains("backup_tenant", McpTestHost.Parse(tools.ExportTenantReport()).GetProperty("error").GetString());
    }
}
