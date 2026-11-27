using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using PhoneDesk.Portability;

namespace PhoneDesk.Operations;

/// <summary>One box of a call-flow diagram.</summary>
public sealed record CallFlowBox(double X, double Y, double Width, double Height, string Title, string Detail, string Kind);

/// <summary>A line of a call-flow diagram, from the right edge of one box to the left edge of another.</summary>
public sealed record CallFlowLine(double X1, double Y1, double X2, double Y2);

/// <summary>
/// The layout of the call-flow diagram of one auto attendant: boxes and lines with fixed
/// positions. The SVG text is written from it, and the desktop app draws the same layout as a
/// picture, so both show the same diagram.
/// </summary>
public sealed class CallFlowDiagram
{
    public string AutoAttendantName { get; init; } = string.Empty;

    public string AutoAttendantIdentity { get; init; } = string.Empty;

    public double Width { get; init; }

    public double Height { get; init; }

    public IReadOnlyList<CallFlowBox> Boxes { get; init; } = Array.Empty<CallFlowBox>();

    public IReadOnlyList<CallFlowLine> Lines { get; init; } = Array.Empty<CallFlowLine>();
}

/// <summary>One sheet of the workbook: a name, a header row and the rows.</summary>
public sealed record ReportSheet(string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// The readable exports of a tenant snapshot: an HTML report in one file, a workbook with one
/// sheet per kind of object, and a call-flow diagram per auto attendant. Everything is built
/// from a snapshot that was read before; nothing here reads the tenant. The same snapshot gives
/// the same bytes, so two exports can be compared. Both heads call this class.
/// </summary>
public static class RichReport
{
    public const string English = "en";
    public const string German = "de";

    private const double RootWidth = 230;
    private const double FlowWidth = 250;
    private const double TargetWidth = 330;
    private const double Gap = 60;
    private const double RowHeight = 44;
    private const double BoxHeight = 36;
    private const double Margin = 16;

    private static readonly DateTimeOffset FixedEntryTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The documentation part of a snapshot. A snapshot without it cannot be reported.</summary>
    public static TenantDocumentationSnapshot Require(TopologySnapshotDocument snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Documentation
            ?? throw new InvalidOperationException(
                "This snapshot has no documentation part (it is from an earlier version). Take a new full snapshot of the tenant and export that.");
    }

    /// <summary>
    /// Writes the report files into a new folder under <paramref name="parentDirectory"/>: the HTML
    /// report, the workbook, and one diagram per auto attendant as SVG, and as PNG when the head can
    /// draw pictures. Returns the folder and the files. Nothing existing is overwritten.
    /// </summary>
    /// <param name="picture">Draws a diagram as a PNG picture. Null in a head without a drawing surface.</param>
    public static (string Directory, IReadOnlyList<string> Files) WriteFiles(
        string parentDirectory, TopologySnapshotDocument snapshot, string language = English, Func<CallFlowDiagram, byte[]?>? picture = null)
    {
        Require(snapshot);
        var stamp = snapshot.RetrievedAtUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(parentDirectory, "phonedesk-report-" + stamp + "Z");
        for (var number = 2; Directory.Exists(directory); number++)
        {
            directory = Path.Combine(parentDirectory, Invariant($"phonedesk-report-{stamp}Z-{number}"));
        }

        Directory.CreateDirectory(directory);
        var files = new List<string>();
        void Text(string name, string content)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            files.Add(path);
        }

        void Bytes(string name, byte[] content)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, content);
            files.Add(path);
        }

        Text("report.html", BuildHtml(snapshot, language));
        Bytes("report.xlsx", BuildWorkbook(snapshot, language));
        var diagrams = BuildDiagrams(snapshot, language);
        for (var index = 0; index < diagrams.Count; index++)
        {
            var name = DiagramFileName(diagrams[index], index);
            Text(name + ".svg", ToSvg(diagrams[index]));
            if (picture?.Invoke(diagrams[index]) is { Length: > 0 } png)
            {
                Bytes(name + ".png", png);
            }
        }

        return (directory, files);
    }

    // ── Call-flow diagrams ────────────────────────────────────────────────

    public static IReadOnlyList<CallFlowDiagram> BuildDiagrams(TopologySnapshotDocument snapshot, string language = English)
    {
        var documentation = Require(snapshot);
        var text = new Text(language);
        var names = new Names(snapshot, documentation);
        return documentation.AutoAttendants
            .OrderBy(attendant => attendant.Name, StringComparer.Ordinal)
            .ThenBy(attendant => attendant.Identity, StringComparer.Ordinal)
            .Select(attendant => BuildDiagram(attendant, documentation, names, text))
            .ToList();
    }

    private static CallFlowDiagram BuildDiagram(AutoAttendantInventorySnapshot attendant, TenantDocumentationSnapshot documentation, Names names, Text text)
    {
        bool Mine(string name, string? identity) => identity is null
            ? string.Equals(name, attendant.Name, StringComparison.Ordinal)
            : string.Equals(identity, attendant.Identity, StringComparison.OrdinalIgnoreCase);

        var options = documentation.AutoAttendantMenuOptions.Where(option => Mine(option.AutoAttendantName, option.AutoAttendantIdentity)).ToList();
        var flows = new List<(string Title, string Detail, string Key, string Kind)>
        {
            (text["BusinessHours"], string.IsNullOrWhiteSpace(attendant.DefaultFlow) ? text["DefaultFlow"] : attendant.DefaultFlow, "DefaultCallFlow", "flow")
        };
        flows.AddRange(documentation.AutoAttendantCallFlows
            .Where(flow => Mine(flow.AutoAttendantName, flow.AutoAttendantIdentity))
            .OrderBy(flow => flow.FlowName, StringComparer.Ordinal)
            .ThenBy(flow => flow.Occurrence)
            .Select(flow => (text["CallFlow"], flow.FlowName, flow.FlowName, "flow")));

        var schedules = documentation.AutoAttendantScheduleAssociations
            .Where(link => Mine(link.AutoAttendantName, link.AutoAttendantIdentity))
            .OrderBy(link => link.Type, StringComparer.Ordinal)
            .ThenBy(link => names.Schedule(link.ScheduleId), StringComparer.Ordinal)
            .ToList();

        var boxes = new List<CallFlowBox>();
        var lines = new List<CallFlowLine>();
        var flowX = Margin + RootWidth + Gap;
        var targetX = flowX + FlowWidth + Gap;
        var y = Margin;
        var anchors = new List<double>();

        foreach (var flow in flows)
        {
            var mine = options
                .Where(option => string.Equals(option.FlowName, flow.Key, StringComparison.Ordinal))
                .OrderBy(option => option.Key, StringComparer.Ordinal)
                .ThenBy(option => option.Occurrence)
                .ToList();
            var rows = Math.Max(1, mine.Count);
            var flowY = y + ((rows - 1) * RowHeight / 2);
            boxes.Add(new CallFlowBox(flowX, flowY, FlowWidth, BoxHeight, flow.Title, flow.Detail, flow.Kind));
            anchors.Add(flowY + (BoxHeight / 2));
            for (var index = 0; index < mine.Count; index++)
            {
                var targetY = y + (index * RowHeight);
                boxes.Add(new CallFlowBox(
                    targetX, targetY, TargetWidth, BoxHeight,
                    text["Key"] + " " + KeyName(mine[index].Key, text) + ": " + mine[index].Action,
                    names.Target(mine[index].TargetId), "target"));
                lines.Add(new CallFlowLine(flowX + FlowWidth, flowY + (BoxHeight / 2), targetX, targetY + (BoxHeight / 2)));
            }

            y += rows * RowHeight;
        }

        foreach (var link in schedules)
        {
            var holiday = string.Equals(link.Type, "Holiday", StringComparison.OrdinalIgnoreCase);
            boxes.Add(new CallFlowBox(
                flowX, y, FlowWidth, BoxHeight,
                holiday ? text["Holiday"] : text["AfterHours"],
                names.Schedule(link.ScheduleId), holiday ? "holiday" : "afterhours"));
            anchors.Add(y + (BoxHeight / 2));
            y += RowHeight;
        }

        var contentHeight = Math.Max(y - (RowHeight - BoxHeight), Margin + BoxHeight);
        var rootY = Math.Max(Margin, ((contentHeight + Margin) / 2) - (BoxHeight / 2));
        boxes.Insert(0, new CallFlowBox(Margin, rootY, RootWidth, BoxHeight, attendant.Name, names.NumbersOf(attendant.Identity), "root"));
        lines.InsertRange(0, anchors.Select(anchor => new CallFlowLine(Margin + RootWidth, rootY + (BoxHeight / 2), flowX, anchor)));

        return new CallFlowDiagram
        {
            AutoAttendantName = attendant.Name,
            AutoAttendantIdentity = attendant.Identity,
            Width = targetX + TargetWidth + Margin,
            Height = contentHeight + Margin,
            Boxes = boxes,
            Lines = lines
        };
    }

    /// <summary>The diagram as SVG text. Colors are fixed, so the picture is the same in every viewer.</summary>
    public static string ToSvg(CallFlowDiagram diagram)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        var svg = new StringBuilder();
        svg.Append(Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {diagram.Width:0.#} {diagram.Height:0.#}\" width=\"{diagram.Width:0.#}\" height=\"{diagram.Height:0.#}\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" role=\"img\">\n"));
        svg.Append("<title>").Append(Escape(diagram.AutoAttendantName)).Append("</title>\n");
        svg.Append(Invariant($"<rect width=\"{diagram.Width:0.#}\" height=\"{diagram.Height:0.#}\" fill=\"#ffffff\"/>\n"));
        foreach (var line in diagram.Lines)
        {
            var middle = (line.X1 + line.X2) / 2;
            svg.Append(Invariant($"<path d=\"M{line.X1:0.#},{line.Y1:0.#} C{middle:0.#},{line.Y1:0.#} {middle:0.#},{line.Y2:0.#} {line.X2:0.#},{line.Y2:0.#}\" fill=\"none\" stroke=\"#9aa0c8\" stroke-width=\"1.5\"/>\n"));
        }

        foreach (var box in diagram.Boxes)
        {
            var (fill, stroke, ink) = Colors(box.Kind);
            svg.Append(Invariant($"<rect x=\"{box.X:0.#}\" y=\"{box.Y:0.#}\" width=\"{box.Width:0.#}\" height=\"{box.Height:0.#}\" rx=\"7\" fill=\"{fill}\" stroke=\"{stroke}\"/>\n"));
            svg.Append(Invariant($"<text x=\"{box.X + 10:0.#}\" y=\"{box.Y + 15:0.#}\" font-size=\"11\" font-weight=\"600\" fill=\"{ink}\">{Escape(Fit(box.Title, box.Width, 11))}</text>\n"));
            svg.Append(Invariant($"<text x=\"{box.X + 10:0.#}\" y=\"{box.Y + 29:0.#}\" font-size=\"11\" fill=\"{ink}\">{Escape(Fit(box.Detail, box.Width, 11))}</text>\n"));
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    /// <summary>Fill, border and text color of a box kind. The desktop picture uses the same values.</summary>
    public static (string Fill, string Stroke, string Ink) Colors(string kind) => kind switch
    {
        "root" => ("#45478f", "#45478f", "#ffffff"),
        "flow" => ("#eef0ff", "#7b80ee", "#1d1e3a"),
        "holiday" => ("#fff4e5", "#d9822b", "#3a2a12"),
        "afterhours" => ("#eef6ff", "#4a7fb5", "#12283a"),
        _ => ("#f6f6f9", "#b9bccf", "#1d1e3a")
    };

    /// <summary>A file name for the diagram of an auto attendant: letters, digits and hyphens only.</summary>
    public static string DiagramFileName(CallFlowDiagram diagram, int index)
        => Invariant($"call-flow-{index + 1:00}-{Slug(diagram.AutoAttendantName)}");

    // ── Workbook ──────────────────────────────────────────────────────────

    public static IReadOnlyList<ReportSheet> BuildSheets(TopologySnapshotDocument snapshot, string language = English)
    {
        var documentation = Require(snapshot);
        var text = new Text(language);
        var names = new Names(snapshot, documentation);
        static IReadOnlyList<string> Row(params string?[] cells) => cells.Select(cell => cell ?? string.Empty).ToList();

        return new List<ReportSheet>
        {
            new(text["AutoAttendants"], new[] { text["Name"], text["Language"], text["TimeZone"], text["Voice"], text["BusinessHoursFlow"], text["PhoneNumbers"], "ID" },
                documentation.AutoAttendants.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Identity, StringComparer.Ordinal)
                    .Select(item => Row(item.Name, item.Language, item.TimeZone, item.Voice, item.DefaultFlow, names.NumbersOf(item.Identity), item.Identity)).ToList()),
            new(text["MenuOptions"], new[] { text["AutoAttendant"], text["CallFlow"], text["Key"], text["Action"], text["Target"] },
                documentation.AutoAttendantMenuOptions.OrderBy(item => item.AutoAttendantName, StringComparer.Ordinal).ThenBy(item => item.FlowName, StringComparer.Ordinal).ThenBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Occurrence)
                    .Select(item => Row(item.AutoAttendantName, item.FlowName, item.Key, item.Action, names.Target(item.TargetId))).ToList()),
            new(text["CallQueues"], new[] { text["Name"], text["Routing"], text["AlertTime"], text["Language"], text["Agents"], text["OverflowThreshold"], text["OverflowAction"], text["TimeoutThreshold"], text["TimeoutAction"], text["PhoneNumbers"], "ID" },
                documentation.CallQueues.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Identity, StringComparer.Ordinal)
                    .Select(item => Row(item.Name, item.Routing, item.AlertTime, item.Language, item.AgentCount.ToString(CultureInfo.InvariantCulture), item.OverflowThreshold, item.OverflowAction, item.TimeoutThreshold, item.TimeoutAction, names.NumbersOf(item.Identity), item.Identity)).ToList()),
            new(text["Agents"], new[] { text["CallQueue"], text["Name"], text["SignInName"], text["OptIn"] },
                documentation.CallQueueAgents.OrderBy(item => item.CallQueueName, StringComparer.Ordinal).ThenBy(item => item.DisplayName, StringComparer.Ordinal).ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                    .Select(item => Row(item.CallQueueName, item.DisplayName, item.UserPrincipalName, item.OptIn)).ToList()),
            new(text["ResourceAccounts"], new[] { text["Name"], text["SignInName"], text["PhoneNumber"], text["Serves"], "ID" },
                documentation.ResourceAccounts.OrderBy(item => item.UserPrincipalName, StringComparer.Ordinal)
                    .Select(item => Row(item.Name, item.UserPrincipalName, item.PhoneNumber, names.ServedBy(item), item.ObjectId)).ToList()),
            new(text["Groups"], new[] { text["Name"], text["MailNickname"], text["Description"], "ID" },
                snapshot.Groups.OrderBy(item => item.DisplayName, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Select(item => Row(item.DisplayName, item.MailNickname, item.Description, item.Id)).ToList()),
            new(text["Schedules"], new[] { text["Name"], text["Type"], text["Day"], text["Start"], text["End"] },
                ScheduleRows(documentation).Select(row => Row(row.Name, row.Type, row.Day, row.Start, row.End)).ToList()),
            new(text["PhoneNumbers"], new[] { text["PhoneNumber"], text["Type"], text["AssignedTo"], text["Status"], text["City"], text["Capability"] },
                documentation.PhoneNumbers.OrderBy(item => item.Number, StringComparer.Ordinal)
                    .Select(item => Row(item.Number, item.Type, item.AssignedTo, item.Status, item.City, item.Capability)).ToList()),
            new(text["VoiceUsers"], new[] { text["Name"], text["SignInName"], text["PhoneNumber"], text["VoiceRoutingPolicy"], text["CallingPolicy"], text["DialPlan"] },
                documentation.VoiceUsers.OrderBy(item => item.UserPrincipalName, StringComparer.Ordinal)
                    .Select(item => Row(item.Name, item.UserPrincipalName, item.LineUri, item.VoiceRoutingPolicy, item.CallingPolicy, item.DialPlan)).ToList())
        };
    }

    /// <summary>
    /// The workbook as an .xlsx file: one sheet per kind of object, a bold header row that stays in
    /// view, and a filter on every column. Written without a spreadsheet library.
    /// </summary>
    public static byte[] BuildWorkbook(TopologySnapshotDocument snapshot, string language = English)
    {
        var sheets = BuildSheets(snapshot, language);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                // A fixed time, so the same snapshot gives the same file.
                entry.LastWriteTime = FixedEntryTime;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }

            var overrides = string.Concat(sheets.Select((_, index) =>
                Invariant($"<Override PartName=\"/xl/worksheets/sheet{index + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")));
            Add("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" + overrides + "</Types>");
            Add("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add("xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                string.Concat(sheets.Select((sheet, index) => Invariant($"<sheet name=\"{Escape(SheetName(sheet.Name))}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>"))) +
                "</sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(sheets.Select((_, index) => Invariant($"<Relationship Id=\"rId{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index + 1}.xml\"/>"))) +
                Invariant($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>"));
            Add("xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/><xf numFmtId=\"49\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyNumberFormat=\"1\"/></cellXfs>" +
                "</styleSheet>");
            for (var index = 0; index < sheets.Count; index++)
            {
                Add(Invariant($"xl/worksheets/sheet{index + 1}.xml"), SheetXml(sheets[index]));
            }
        }

        return stream.ToArray();
    }

    private static string SheetXml(ReportSheet sheet)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        xml.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        xml.Append("<cols>");
        for (var column = 0; column < sheet.Columns.Count; column++)
        {
            var longest = Math.Max(sheet.Columns[column].Length, sheet.Rows.Select(row => row[column].Length).DefaultIfEmpty(0).Max());
            xml.Append(Invariant($"<col min=\"{column + 1}\" max=\"{column + 1}\" width=\"{Math.Clamp(longest + 3, 10, 70)}\" customWidth=\"1\"/>"));
        }

        xml.Append("</cols><sheetData>");
        void AppendRow(int number, IReadOnlyList<string> cells, int style)
        {
            xml.Append(Invariant($"<row r=\"{number}\">"));
            for (var column = 0; column < cells.Count; column++)
            {
                xml.Append(Invariant($"<c r=\"{ColumnName(column)}{number}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">"))
                    .Append(Escape(Clean(cells[column]))).Append("</t></is></c>");
            }

            xml.Append("</row>");
        }

        AppendRow(1, sheet.Columns, 1);
        for (var row = 0; row < sheet.Rows.Count; row++)
        {
            AppendRow(row + 2, sheet.Rows[row], 0);
        }

        xml.Append("</sheetData>");
        xml.Append(Invariant($"<autoFilter ref=\"A1:{ColumnName(sheet.Columns.Count - 1)}{sheet.Rows.Count + 1}\"/>"));
        xml.Append("</worksheet>");
        return xml.ToString();
    }

    // ── HTML report ───────────────────────────────────────────────────────

    /// <summary>The report as one HTML file: styles and diagrams are inside it, and it loads nothing from the network.</summary>
    public static string BuildHtml(TopologySnapshotDocument snapshot, string language = English)
    {
        var documentation = Require(snapshot);
        var text = new Text(language);
        var names = new Names(snapshot, documentation);
        var diagrams = BuildDiagrams(snapshot, language);
        var html = new StringBuilder();
        var tenant = string.IsNullOrWhiteSpace(documentation.Tenant.Name) ? text["Tenant"] : documentation.Tenant.Name;
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(text.Language).Append("\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<title>").Append(Escape(text["ReportTitle"] + " - " + tenant)).Append("</title>\n<style>\n").Append(Style).Append("</style>\n</head>\n<body>\n");
        html.Append("<header><div class=\"brand\">").Append(BrandMark).Append("<div><div class=\"product\">PhoneDesk</div><div class=\"sub\">")
            .Append(Escape(text["ReportTitle"])).Append("</div></div></div><div class=\"meta\"><div class=\"tenant\">").Append(Escape(tenant)).Append("</div><div>")
            .Append(Escape(text["SnapshotOf"] + " " + snapshot.RetrievedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"))
            .Append("</div></div></header>\n<main>\n");

        // Overview
        html.Append("<section id=\"overview\"><h2>").Append(Escape(text["Overview"])).Append("</h2><div class=\"cards\">");
        foreach (var (label, count, anchor) in new[]
                 {
                     (text["AutoAttendants"], documentation.AutoAttendants.Count, "auto-attendants"),
                     (text["CallQueues"], documentation.CallQueues.Count, "call-queues"),
                     (text["ResourceAccounts"], documentation.ResourceAccounts.Count, "resource-accounts"),
                     (text["Groups"], snapshot.Groups.Count, "groups"),
                     (text["Schedules"], documentation.Schedules.Count, "schedules"),
                     (text["PhoneNumbers"], documentation.PhoneNumbers.Count, "phone-numbers"),
                     (text["VoiceUsers"], documentation.VoiceUsers.Count, "voice-users")
                 })
        {
            html.Append("<a class=\"card\" href=\"#").Append(anchor).Append("\"><span class=\"count\">").Append(count.ToString(CultureInfo.InvariantCulture))
                .Append("</span><span>").Append(Escape(label)).Append("</span></a>");
        }

        html.Append("</div><table class=\"facts\">");
        Fact(html, text["Tenant"], documentation.Tenant.Name);
        Fact(html, "ID", documentation.Tenant.Id);
        Fact(html, text["Country"], documentation.Tenant.Country);
        Fact(html, text["Language"], documentation.Tenant.Language);
        html.Append("</table></section>\n");

        // Auto attendants
        html.Append("<section id=\"auto-attendants\"><h2>").Append(Escape(text["AutoAttendants"])).Append("</h2>\n");
        foreach (var diagram in diagrams)
        {
            var attendant = documentation.AutoAttendants.First(item => string.Equals(item.Identity, diagram.AutoAttendantIdentity, StringComparison.Ordinal) && item.Name == diagram.AutoAttendantName);
            html.Append("<article id=\"").Append(names.Anchor("aa", attendant.Identity, attendant.Name)).Append("\"><h3>").Append(Escape(attendant.Name)).Append("</h3><table class=\"facts\">");
            Fact(html, text["PhoneNumbers"], names.NumbersOf(attendant.Identity));
            Fact(html, text["Language"], attendant.Language);
            Fact(html, text["TimeZone"], attendant.TimeZone);
            Fact(html, text["Voice"], attendant.Voice);
            FactHtml(html, text["ResourceAccounts"], names.AccountLinks(attendant.Identity));
            FactHtml(html, text["Schedules"], string.Join(", ", documentation.AutoAttendantScheduleAssociations
                .Where(link => link.AutoAttendantIdentity is null ? link.AutoAttendantName == attendant.Name : string.Equals(link.AutoAttendantIdentity, attendant.Identity, StringComparison.OrdinalIgnoreCase))
                .OrderBy(link => link.Type, StringComparer.Ordinal).ThenBy(link => names.Schedule(link.ScheduleId), StringComparer.Ordinal)
                .Select(link => Escape(link.Type) + ": " + names.ScheduleLink(link.ScheduleId))));
            html.Append("</table><div class=\"diagram\">").Append(ToSvg(diagram)).Append("</div></article>\n");
        }

        Empty(html, documentation.AutoAttendants.Count, text);
        html.Append("</section>\n");

        // Call queues
        html.Append("<section id=\"call-queues\"><h2>").Append(Escape(text["CallQueues"])).Append("</h2>\n");
        foreach (var queue in documentation.CallQueues.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Identity, StringComparer.Ordinal))
        {
            bool Mine(string name, string? identity) => identity is null ? name == queue.Name : string.Equals(identity, queue.Identity, StringComparison.OrdinalIgnoreCase);
            html.Append("<article id=\"").Append(names.Anchor("cq", queue.Identity, queue.Name)).Append("\"><h3>").Append(Escape(queue.Name)).Append("</h3><table class=\"facts\">");
            Fact(html, text["PhoneNumbers"], names.NumbersOf(queue.Identity));
            Fact(html, text["Routing"], queue.Routing);
            Fact(html, text["AlertTime"], queue.AlertTime);
            Fact(html, text["Language"], queue.Language);
            FactHtml(html, text["ResourceAccounts"], names.AccountLinks(queue.Identity));
            FactHtml(html, text["Groups"], string.Join(", ", documentation.CallQueueDistributionLists.Where(item => Mine(item.CallQueueName, item.CallQueueIdentity))
                .OrderBy(item => names.Group(item.GroupId), StringComparer.Ordinal).Select(item => names.GroupLink(item.GroupId))));
            foreach (var action in documentation.CallQueueActions.Where(item => Mine(item.CallQueueName, item.CallQueueIdentity)).OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Occurrence))
            {
                FactHtml(html, action.Kind, Escape(action.Action + " (" + action.Threshold + ")") + (string.IsNullOrWhiteSpace(action.TargetId) ? string.Empty : " &rarr; " + names.TargetLink(action.TargetId)));
            }

            html.Append("</table>");
            var agents = documentation.CallQueueAgents.Where(item => Mine(item.CallQueueName, item.CallQueueIdentity))
                .OrderBy(item => item.DisplayName, StringComparer.Ordinal).ThenBy(item => item.ObjectId, StringComparer.Ordinal).ToList();
            if (agents.Count > 0)
            {
                Table(html, new[] { text["Agents"], text["SignInName"], text["OptIn"] },
                    agents.Select(agent => new[] { Escape(agent.DisplayName), Escape(agent.UserPrincipalName), Escape(agent.OptIn) }));
            }

            html.Append("</article>\n");
        }

        Empty(html, documentation.CallQueues.Count, text);
        html.Append("</section>\n");

        // Resource accounts
        html.Append("<section id=\"resource-accounts\"><h2>").Append(Escape(text["ResourceAccounts"])).Append("</h2>\n");
        Table(html, new[] { text["Name"], text["SignInName"], text["PhoneNumber"], text["Serves"] },
            documentation.ResourceAccounts.OrderBy(item => item.UserPrincipalName, StringComparer.Ordinal).Select(account => new[]
            {
                "<span id=\"" + names.Anchor("ra", account.ObjectId, account.UserPrincipalName) + "\">" + Escape(account.Name) + "</span>",
                Escape(account.UserPrincipalName), Escape(account.PhoneNumber), names.ServedByLinks(account)
            }));
        html.Append("</section>\n");

        // Groups
        html.Append("<section id=\"groups\"><h2>").Append(Escape(text["Groups"])).Append("</h2>\n");
        Table(html, new[] { text["Name"], text["MailNickname"], text["Description"] },
            snapshot.Groups.OrderBy(item => item.DisplayName, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).Select(group => new[]
            {
                "<span id=\"" + names.Anchor("grp", group.Id, group.DisplayName) + "\">" + Escape(group.DisplayName) + "</span>",
                Escape(group.MailNickname), Escape(group.Description)
            }));
        html.Append("</section>\n");

        // Schedules
        html.Append("<section id=\"schedules\"><h2>").Append(Escape(text["Schedules"])).Append("</h2>\n");
        foreach (var schedule in documentation.Schedules.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            html.Append("<article id=\"").Append(names.Anchor("sch", schedule.Id, schedule.Name)).Append("\"><h3>").Append(Escape(schedule.Name))
                .Append(" <span class=\"tag\">").Append(Escape(schedule.Type)).Append("</span></h3>");
            var rows = ScheduleRows(documentation).Where(row => row.Id is null ? row.Name == schedule.Name : string.Equals(row.Id, schedule.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Any(row => row.Start.Length > 0))
            {
                Table(html, new[] { text["Day"], text["Start"], text["End"] }, rows.Select(row => new[] { Escape(row.Day), Escape(row.Start), Escape(row.End) }));
            }

            html.Append("</article>\n");
        }

        Empty(html, documentation.Schedules.Count, text);
        html.Append("</section>\n");

        // Phone numbers
        html.Append("<section id=\"phone-numbers\"><h2>").Append(Escape(text["PhoneNumbers"])).Append("</h2>\n");
        Table(html, new[] { text["PhoneNumber"], text["Type"], text["AssignedTo"], text["Status"], text["City"] },
            documentation.PhoneNumbers.OrderBy(item => item.Number, StringComparer.Ordinal).Select(number => new[]
            {
                Escape(number.Number), Escape(number.Type), Escape(number.AssignedTo), Escape(number.Status), Escape(number.City)
            }));
        html.Append("</section>\n");

        // Voice users
        html.Append("<section id=\"voice-users\"><h2>").Append(Escape(text["VoiceUsers"])).Append("</h2>\n");
        Table(html, new[] { text["Name"], text["SignInName"], text["PhoneNumber"], text["VoiceRoutingPolicy"], text["CallingPolicy"], text["DialPlan"] },
            documentation.VoiceUsers.OrderBy(item => item.UserPrincipalName, StringComparer.Ordinal).Select(user => new[]
            {
                Escape(user.Name), Escape(user.UserPrincipalName), Escape(user.LineUri), Escape(user.VoiceRoutingPolicy), Escape(user.CallingPolicy), Escape(user.DialPlan)
            }));
        html.Append("</section>\n</main>\n<footer>").Append(Escape(text["Footer"])).Append("</footer>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static void Fact(StringBuilder html, string label, string? value) => FactHtml(html, label, Escape(value ?? string.Empty));

    private static void FactHtml(StringBuilder html, string label, string valueHtml)
    {
        if (valueHtml.Length > 0)
        {
            html.Append("<tr><th>").Append(Escape(label)).Append("</th><td>").Append(valueHtml).Append("</td></tr>");
        }
    }

    private static void Table(StringBuilder html, IReadOnlyList<string> columns, IEnumerable<string[]> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            html.Append("<p class=\"none\">&ndash;</p>\n");
            return;
        }

        html.Append("<table class=\"list\"><thead><tr>");
        foreach (var column in columns)
        {
            html.Append("<th>").Append(Escape(column)).Append("</th>");
        }

        html.Append("</tr></thead><tbody>");
        foreach (var row in list)
        {
            html.Append("<tr>");
            foreach (var cell in row)
            {
                html.Append("<td>").Append(cell).Append("</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</tbody></table>\n");
    }

    private static void Empty(StringBuilder html, int count, Text text)
    {
        if (count == 0)
        {
            html.Append("<p class=\"none\">").Append(Escape(text["None"])).Append("</p>\n");
        }
    }

    private static IEnumerable<(string Name, string? Id, string Type, string Day, string Start, string End)> ScheduleRows(TenantDocumentationSnapshot documentation)
    {
        foreach (var schedule in documentation.Schedules.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            bool Mine(string name, string? id) => id is null ? name == schedule.Name : string.Equals(id, schedule.Id, StringComparison.OrdinalIgnoreCase);
            var any = false;
            foreach (var range in documentation.ScheduleWeeklyRanges.Where(item => Mine(item.ScheduleName, item.ScheduleId)).OrderBy(item => DayOrder(item.Day)).ThenBy(item => item.Start, StringComparer.Ordinal))
            {
                any = true;
                yield return (schedule.Name, schedule.Id, schedule.Type, range.Day, range.Start, range.End);
            }

            foreach (var range in documentation.ScheduleDateRanges.Where(item => Mine(item.ScheduleName, item.ScheduleId)).OrderBy(item => item.Occurrence))
            {
                any = true;
                yield return (schedule.Name, schedule.Id, schedule.Type, string.Empty, range.Start, range.End);
            }

            if (!any)
            {
                yield return (schedule.Name, schedule.Id, schedule.Type, string.Empty, string.Empty, string.Empty);
            }
        }
    }

    private static int DayOrder(string day)
        => Array.FindIndex(new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" }, name => string.Equals(name, day, StringComparison.OrdinalIgnoreCase));

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string KeyName(string key, Text text)
        => string.IsNullOrWhiteSpace(key) ? "-" : key.Replace("Tone", string.Empty, StringComparison.Ordinal) is { Length: > 0 } digit && digit != key ? digit : key == "Automatic" ? text["Automatic"] : key;

    /// <summary>Shortens a text to the width of its box. A character of this font is about 0.56 of its size wide.</summary>
    private static string Fit(string? value, double width, double fontSize)
    {
        var text = Clean(value ?? string.Empty);
        var max = (int)((width - 20) / (fontSize * 0.56));
        return text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";
    }

    private static string Slug(string? value)
    {
        var builder = new StringBuilder();
        foreach (var character in (value ?? string.Empty).ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        }

        var slug = string.Join("-", builder.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "x" : slug.Length > 60 ? slug[..60] : slug;
    }

    private static string SheetName(string name)
    {
        var clean = new string(name.Where(character => !"[]:*?/\\".Contains(character, StringComparison.Ordinal)).ToArray());
        return clean.Length > 31 ? clean[..31] : clean;
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var value = index + 1; value > 0; value = (value - 1) / 26)
        {
            name = (char)('A' + ((value - 1) % 26)) + name;
        }

        return name;
    }

    /// <summary>Removes characters XML does not allow.</summary>
    private static string Clean(string value)
        => new(value.Where(character => character == '\t' || character == '\n' || character == '\r' || (character >= ' ' && character != '￾' && character != '￿')).ToArray());

    private static string Escape(string? value) => WebUtility.HtmlEncode(Clean(value ?? string.Empty));

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Resolves IDs to names and builds the links between the parts of the report.</summary>
    private sealed class Names
    {
        private readonly TopologySnapshotDocument _snapshot;
        private readonly TenantDocumentationSnapshot _documentation;

        public Names(TopologySnapshotDocument snapshot, TenantDocumentationSnapshot documentation)
        {
            _snapshot = snapshot;
            _documentation = documentation;
        }

        public string Anchor(string prefix, string? id, string name) => prefix + "-" + Slug(string.IsNullOrWhiteSpace(id) ? name : id);

        private IEnumerable<ResourceAccountInventorySnapshot> AccountsOf(string configurationId)
            => _documentation.ResourceAccounts
                .Where(account => account.Associations.Any(link => string.Equals(link.ConfigurationId, configurationId, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(account => account.UserPrincipalName, StringComparer.Ordinal);

        public string NumbersOf(string configurationId)
            => string.Join(", ", AccountsOf(configurationId).Select(account => account.PhoneNumber).Where(number => !string.IsNullOrWhiteSpace(number)));

        public string AccountLinks(string configurationId)
            => string.Join(", ", AccountsOf(configurationId).Select(account =>
                "<a href=\"#" + Anchor("ra", account.ObjectId, account.UserPrincipalName) + "\">" + Escape(account.UserPrincipalName) + "</a>"));

        private (string Label, string? Anchor) Configuration(string id)
        {
            var attendant = _documentation.AutoAttendants.FirstOrDefault(item => string.Equals(item.Identity, id, StringComparison.OrdinalIgnoreCase));
            if (attendant is not null)
            {
                return (attendant.Name, Anchor("aa", attendant.Identity, attendant.Name));
            }

            var queue = _documentation.CallQueues.FirstOrDefault(item => string.Equals(item.Identity, id, StringComparison.OrdinalIgnoreCase));
            return queue is not null ? (queue.Name, Anchor("cq", queue.Identity, queue.Name)) : (id, null);
        }

        public string ServedBy(ResourceAccountInventorySnapshot account)
            => string.Join(", ", account.Associations.OrderBy(link => link.Occurrence).Select(link => Configuration(link.ConfigurationId).Label));

        public string ServedByLinks(ResourceAccountInventorySnapshot account)
            => string.Join(", ", account.Associations.OrderBy(link => link.Occurrence).Select(link => Link(Configuration(link.ConfigurationId))));

        /// <summary>What a target ID points to: the call queue or auto attendant behind a resource account, a group, or the ID itself.</summary>
        private (string Label, string? Anchor) ResolveTarget(string? targetId)
        {
            var id = (targetId ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                return (string.Empty, null);
            }

            var account = _documentation.ResourceAccounts.FirstOrDefault(item => string.Equals(item.ObjectId, id, StringComparison.OrdinalIgnoreCase));
            if (account is not null)
            {
                var first = account.Associations.OrderBy(link => link.Occurrence).FirstOrDefault();
                return first is null ? (account.UserPrincipalName, Anchor("ra", account.ObjectId, account.UserPrincipalName)) : Configuration(first.ConfigurationId);
            }

            var group = _snapshot.Groups.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (group is not null)
            {
                return (group.DisplayName, Anchor("grp", group.Id, group.DisplayName));
            }

            var agent = _documentation.CallQueueAgents.FirstOrDefault(item => string.Equals(item.ObjectId, id, StringComparison.OrdinalIgnoreCase));
            return agent is not null && !string.IsNullOrWhiteSpace(agent.DisplayName) ? (agent.DisplayName, null) : (id, null);
        }

        public string Target(string? targetId) => ResolveTarget(targetId).Label;

        public string TargetLink(string? targetId) => Link(ResolveTarget(targetId));

        public string Group(string id) => _snapshot.Groups.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id;

        public string GroupLink(string id)
        {
            var group = _snapshot.Groups.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            return group is null ? Escape(id) : Link((group.DisplayName, Anchor("grp", group.Id, group.DisplayName)));
        }

        public string Schedule(string id) => _documentation.Schedules.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;

        public string ScheduleLink(string id)
        {
            var schedule = _documentation.Schedules.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            return schedule is null ? Escape(id) : Link((schedule.Name, Anchor("sch", schedule.Id, schedule.Name)));
        }

        private static string Link((string Label, string? Anchor) target)
            => target.Anchor is null ? Escape(target.Label) : "<a href=\"#" + target.Anchor + "\">" + Escape(target.Label) + "</a>";
    }

    /// <summary>The words of the report in English and German.</summary>
    private sealed class Text
    {
        private static readonly Dictionary<string, (string En, string De)> Words = new(StringComparer.Ordinal)
        {
            ["ReportTitle"] = ("Teams Phone documentation", "Dokumentation Teams-Telefonie"),
            ["SnapshotOf"] = ("Snapshot of", "Stand vom"),
            ["Overview"] = ("Overview", "Übersicht"),
            ["Tenant"] = ("Tenant", "Mandant"),
            ["Country"] = ("Country", "Land"),
            ["Language"] = ("Language", "Sprache"),
            ["TimeZone"] = ("Time zone", "Zeitzone"),
            ["Voice"] = ("Voice", "Stimme"),
            ["AutoAttendants"] = ("Auto attendants", "Automatische Telefonzentralen"),
            ["AutoAttendant"] = ("Auto attendant", "Automatische Telefonzentrale"),
            ["CallQueues"] = ("Call queues", "Anrufwarteschleifen"),
            ["CallQueue"] = ("Call queue", "Anrufwarteschleife"),
            ["ResourceAccounts"] = ("Resource accounts", "Ressourcenkonten"),
            ["Groups"] = ("Groups", "Gruppen"),
            ["Schedules"] = ("Schedules", "Zeitpläne"),
            ["PhoneNumbers"] = ("Phone numbers", "Rufnummern"),
            ["PhoneNumber"] = ("Phone number", "Rufnummer"),
            ["VoiceUsers"] = ("Voice users", "Benutzer mit Telefonie"),
            ["MenuOptions"] = ("Menu options", "Menüoptionen"),
            ["Agents"] = ("Agents", "Agenten"),
            ["Name"] = ("Name", "Name"),
            ["SignInName"] = ("Sign-in name", "Anmeldename"),
            ["OptIn"] = ("Opted in", "Angemeldet"),
            ["Routing"] = ("Routing method", "Verteilmethode"),
            ["AlertTime"] = ("Agent alert time (s)", "Klingelzeit für Agenten (s)"),
            ["OverflowThreshold"] = ("Overflow threshold", "Überlaufschwelle"),
            ["OverflowAction"] = ("Overflow action", "Überlaufaktion"),
            ["TimeoutThreshold"] = ("Timeout (s)", "Zeitlimit (s)"),
            ["TimeoutAction"] = ("Timeout action", "Aktion bei Zeitlimit"),
            ["Serves"] = ("Serves", "Zugeordnet zu"),
            ["MailNickname"] = ("Mail nickname", "E-Mail-Alias"),
            ["Description"] = ("Description", "Beschreibung"),
            ["Type"] = ("Type", "Typ"),
            ["Day"] = ("Day", "Tag"),
            ["Start"] = ("Start", "Beginn"),
            ["End"] = ("End", "Ende"),
            ["AssignedTo"] = ("Assigned to", "Zugewiesen an"),
            ["Status"] = ("Status", "Status"),
            ["City"] = ("City", "Ort"),
            ["Capability"] = ("Capability", "Verwendung"),
            ["VoiceRoutingPolicy"] = ("Voice routing policy", "Voice-Routing-Richtlinie"),
            ["CallingPolicy"] = ("Calling policy", "Anrufrichtlinie"),
            ["DialPlan"] = ("Dial plan", "Wählplan"),
            ["Key"] = ("Key", "Taste"),
            ["Action"] = ("Action", "Aktion"),
            ["Target"] = ("Target", "Ziel"),
            ["CallFlow"] = ("Call flow", "Anrufablauf"),
            ["BusinessHours"] = ("Opening hours", "Öffnungszeiten"),
            ["BusinessHoursFlow"] = ("Call flow in opening hours", "Anrufablauf in den Öffnungszeiten"),
            ["DefaultFlow"] = ("Default call flow", "Standard-Anrufablauf"),
            ["AfterHours"] = ("After hours", "Ausserhalb der Öffnungszeiten"),
            ["Holiday"] = ("Holiday", "Feiertag"),
            ["Automatic"] = ("automatic", "automatisch"),
            ["None"] = ("None in this snapshot.", "Keine in diesem Stand."),
            ["Footer"] = ("Made by PhoneDesk from a snapshot of the tenant. The report shows the tenant at the time of the snapshot.",
                "Von PhoneDesk aus einem Stand des Mandanten erstellt. Der Bericht zeigt den Mandanten zum Zeitpunkt des Stands.")
        };

        public Text(string? language)
        {
            Language = string.Equals(language, German, StringComparison.OrdinalIgnoreCase) ? German : English;
        }

        public string Language { get; }

        public string this[string key] => Language == German ? Words[key].De : Words[key].En;
    }

    private const string BrandMark =
        "<svg width=\"40\" height=\"40\" viewBox=\"0 0 40 40\" aria-hidden=\"true\"><defs><linearGradient id=\"pd\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">" +
        "<stop offset=\"0\" stop-color=\"#7B80EE\"/><stop offset=\"1\" stop-color=\"#45478F\"/></linearGradient></defs>" +
        "<rect width=\"40\" height=\"40\" rx=\"9\" fill=\"url(#pd)\"/>" +
        "<g fill=\"#fff\"><circle cx=\"12\" cy=\"11\" r=\"2.6\"/><circle cx=\"20\" cy=\"11\" r=\"2.6\"/><circle cx=\"28\" cy=\"11\" r=\"2.6\"/>" +
        "<circle cx=\"20\" cy=\"19\" r=\"2.6\"/><circle cx=\"20\" cy=\"27\" r=\"2.6\"/>" +
        "<circle cx=\"12\" cy=\"19\" r=\"2.6\" opacity=\".38\"/><circle cx=\"28\" cy=\"19\" r=\"2.6\" opacity=\".38\"/>" +
        "<circle cx=\"12\" cy=\"27\" r=\"2.6\" opacity=\".38\"/><circle cx=\"28\" cy=\"27\" r=\"2.6\" opacity=\".38\"/></g></svg>";

    private const string Style = """
        :root { --ink: #1d1e3a; --soft: #5b5e80; --line: #e2e3ee; --brand: #45478f; --brand2: #7b80ee; }
        * { box-sizing: border-box; }
        body { margin: 0; font-family: "Segoe UI", Helvetica, Arial, sans-serif; color: var(--ink); background: #f6f6fa; font-size: 14px; line-height: 1.45; }
        header { display: flex; justify-content: space-between; align-items: center; gap: 24px; padding: 20px 32px; color: #fff; background: linear-gradient(135deg, var(--brand2), var(--brand)); }
        .brand { display: flex; align-items: center; gap: 14px; }
        .product { font-size: 20px; font-weight: 700; }
        .sub { opacity: .85; }
        .meta { text-align: right; }
        .tenant { font-size: 16px; font-weight: 600; }
        main { max-width: 1180px; margin: 0 auto; padding: 24px 32px; }
        section { background: #fff; border: 1px solid var(--line); border-radius: 12px; padding: 20px 24px; margin-bottom: 20px; }
        h2 { margin: 0 0 14px; font-size: 19px; color: var(--brand); }
        h3 { margin: 18px 0 8px; font-size: 15px; }
        article { border-top: 1px solid var(--line); padding-top: 4px; margin-top: 14px; }
        article:first-of-type { border-top: 0; margin-top: 0; }
        a { color: var(--brand); }
        .cards { display: flex; flex-wrap: wrap; gap: 10px; margin-bottom: 14px; }
        .card { display: flex; flex-direction: column; min-width: 130px; padding: 10px 14px; border: 1px solid var(--line); border-radius: 10px; text-decoration: none; color: var(--soft); }
        .count { font-size: 22px; font-weight: 700; color: var(--ink); }
        table { border-collapse: collapse; }
        table.facts th { text-align: left; font-weight: 600; color: var(--soft); padding: 3px 18px 3px 0; vertical-align: top; white-space: nowrap; }
        table.facts td { padding: 3px 0; }
        table.list { width: 100%; margin-top: 8px; }
        table.list th { text-align: left; font-size: 12px; color: var(--soft); border-bottom: 1px solid var(--line); padding: 6px 10px 6px 0; }
        table.list td { border-bottom: 1px solid var(--line); padding: 6px 10px 6px 0; vertical-align: top; word-break: break-word; }
        .diagram { overflow-x: auto; margin-top: 10px; border: 1px solid var(--line); border-radius: 10px; padding: 6px; }
        .diagram svg { display: block; max-width: 100%; height: auto; }
        .tag { font-size: 11px; font-weight: 600; color: var(--soft); border: 1px solid var(--line); border-radius: 6px; padding: 1px 6px; }
        .none { color: var(--soft); }
        footer { max-width: 1180px; margin: 0 auto; padding: 0 32px 28px; color: var(--soft); font-size: 12px; }
        @media print { body { background: #fff; } section { break-inside: avoid; } }
        """;
}
