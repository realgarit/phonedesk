using System.Text.RegularExpressions;

namespace PhoneDesk.Tests;

public sealed class LocalizationCoverageTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string[] TargetFiles =
    {
        "src/PhoneDesk.Presentation/Views/WelcomeView.axaml",
        "src/PhoneDesk.Presentation/Views/GetStartedView.axaml",
        "src/PhoneDesk.Presentation/Views/WizardView.axaml",
        "src/PhoneDesk.Presentation/Views/VariablesView.axaml",
        "src/PhoneDesk.Presentation/Views/DashboardView.axaml",
        "src/PhoneDesk.Presentation/Views/DocumentationView.axaml",
        "src/PhoneDesk.Presentation/Views/HistoryView.axaml",
        "src/PhoneDesk.Presentation/Views/M365GroupsView.axaml",
        "src/PhoneDesk.Presentation/Views/CallQueuesView.axaml",
        "src/PhoneDesk.Presentation/Views/AutoAttendantsView.axaml",
        "src/PhoneDesk.Presentation/Views/HolidaysView.axaml",
        "src/PhoneDesk.Presentation/Views/BulkOperationsView.axaml"
    };

    private static readonly Regex AttributeRegex = new(
        @"(?<attribute>Text|Content|Header|Title|ToolTip\.Tip|Watermark|AutomationProperties\.Name|OnContent|OffContent)\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex FormatPlaceholderRegex = new(
        @"\{\d+(?::[^{}]*)?\}",
        RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedRawValues = new(StringComparer.Ordinal)
    {
        "PhoneDesk",
        "1",
        "2",
        "3",
        "4"
    };

    [Fact]
    public void Task4Pages_DoNotContainRawVisibleLiterals()
    {
        var findings = new List<string>();

        foreach (var relativePath in TargetFiles)
        {
            var fullPath = Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            findings.AddRange(FindRawVisibleLiterals(File.ReadAllText(fullPath), relativePath));
        }

        Assert.True(findings.Count == 0, "Raw visible literals found:" + Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void Scanner_CatchesMultilineRawVisibleAttributes()
    {
        const string fixture = """
            <Grid>
                <TextBlock
                    Text=
                        "Raw text" />
                <Button
                    Content =
                        "Raw content" />
                <TextBox
                    Watermark =
                        "Raw watermark" />
            </Grid>
            """;

        var findings = FindRawVisibleLiterals(fixture, "fixture.axaml");

        Assert.Equal(3, findings.Count);
        Assert.Contains(findings, finding => finding.Contains("Text=\"Raw text\"", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("Content=\"Raw content\"", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Contains("Watermark=\"Raw watermark\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_RejectsLiteralTextHiddenInStringFormat()
    {
        const string fixture = """
            <TextBlock
                Text="{Binding Name,
                               StringFormat='Label: {0}'}" />
            """;

        var findings = FindRawVisibleLiterals(fixture, "fixture.axaml");

        Assert.Single(findings);
        Assert.Contains("StringFormat", findings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Scanner_AllowsNonLiteralBindingFormat()
    {
        const string fixture = "<TextBlock Text=\"{Binding Timestamp, StringFormat='{}{0:yyyy-MM-dd HH:mm:ss}'}\" />";

        Assert.Empty(FindRawVisibleLiterals(fixture, "fixture.axaml"));
    }

    [Fact]
    public void Scanner_AllowsNonLiteralMarkupExpressionInStringFormat()
    {
        const string fixture = """
            <TextBlock
                Text="{Binding Timestamp,
                               StringFormat={x:Static local:Formats.TimestampFormat}}" />
            """;

        Assert.Empty(FindRawVisibleLiterals(fixture, "fixture.axaml"));
    }

    private static IReadOnlyList<string> FindRawVisibleLiterals(string xaml, string relativePath)
    {
        var findings = new List<string>();

        foreach (Match match in AttributeRegex.Matches(xaml))
        {
            var value = match.Groups["value"].Value;
            if (IsAllowed(value))
            {
                continue;
            }

            findings.Add($"{relativePath}:{GetLineNumber(xaml, match.Index)} {match.Groups["attribute"].Value}=\"{value}\"");
        }

        return findings;
    }

    private static bool IsAllowed(string value)
    {
        if (AllowedRawValues.Contains(value))
        {
            return true;
        }

        if (value.StartsWith("{Binding", StringComparison.Ordinal))
        {
            return IsSafeBinding(value);
        }

        if (value.StartsWith("{loc:Translate", StringComparison.Ordinal)
            || value.StartsWith("{DynamicResource", StringComparison.Ordinal)
            || value.StartsWith("{x:Static", StringComparison.Ordinal))
        {
            return IsBalancedMarkupExpression(value);
        }

        return false;
    }

    private static bool IsSafeBinding(string value)
    {
        if (!IsBalancedMarkupExpression(value))
        {
            return false;
        }

        foreach (var parameter in EnumerateBindingParameters(value))
        {
            if (parameter.Name.Equals("StringFormat", StringComparison.Ordinal)
                && !IsSafeStringFormatValue(parameter.Value))
            {
                return false;
            }

            if ((parameter.Name.Equals("FallbackValue", StringComparison.Ordinal)
                    || parameter.Name.Equals("TargetNullValue", StringComparison.Ordinal))
                && !IsSafeNestedExpressionValue(parameter.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeStringFormatValue(string value)
    {
        var trimmedValue = value.Trim();
        if (TryUnquote(trimmedValue, out var unquotedValue))
        {
            return IsNonLiteralFormat(unquotedValue);
        }

        return IsSafeNestedExpressionValue(trimmedValue)
            || IsNonLiteralFormat(trimmedValue);
    }

    private static bool IsSafeNestedExpressionValue(string value)
    {
        if (TryUnquote(value.Trim(), out _))
        {
            return false;
        }

        return IsAllowedNestedExpression(value.Trim());
    }

    private static bool IsNonLiteralFormat(string format)
    {
        if (format.StartsWith("{}", StringComparison.Ordinal))
        {
            format = format[2..];
        }

        return FormatPlaceholderRegex.Replace(format, string.Empty).Length == 0;
    }

    private static bool TryUnquote(string value, out string unquotedValue)
    {
        if (value.Length >= 2
            && ((value[0] == '\'' && value[^1] == '\'')
                || (value[0] == '"' && value[^1] == '"')))
        {
            unquotedValue = value[1..^1];
            return true;
        }

        unquotedValue = string.Empty;
        return false;
    }

    private static bool IsAllowedNestedExpression(string value)
    {
        return (value.StartsWith("{Binding", StringComparison.Ordinal)
                || value.StartsWith("{DynamicResource", StringComparison.Ordinal)
                || value.StartsWith("{x:Static", StringComparison.Ordinal)
                || value.StartsWith("{loc:Translate", StringComparison.Ordinal))
            && IsBalancedMarkupExpression(value);
    }

    private static IEnumerable<(string Name, string Value)> EnumerateBindingParameters(string value)
    {
        var innerMarkup = value[1..^1].Trim();
        var bindingBody = innerMarkup["Binding".Length..];

        foreach (var segment in SplitTopLevelSegments(bindingBody))
        {
            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            yield return (segment[..equalsIndex].Trim(), segment[(equalsIndex + 1)..].Trim());
        }
    }

    private static IEnumerable<string> SplitTopLevelSegments(string text)
    {
        var segments = new List<string>();
        var segmentStart = 0;
        var nestedDepth = 0;
        var activeQuote = '\0';
        var escaped = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (activeQuote != '\0')
            {
                if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == activeQuote)
                {
                    activeQuote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                activeQuote = character;
                continue;
            }

            if (character == '{')
            {
                nestedDepth++;
                continue;
            }

            if (character == '}')
            {
                nestedDepth--;
                continue;
            }

            if (character == ',' && nestedDepth == 0)
            {
                segments.Add(text[segmentStart..index].Trim());
                segmentStart = index + 1;
            }
        }

        if (segmentStart < text.Length)
        {
            segments.Add(text[segmentStart..].Trim());
        }

        return segments;
    }

    private static bool IsBalancedMarkupExpression(string value)
    {
        if (value.Length < 3 || value[0] != '{' || value[^1] != '}')
        {
            return false;
        }

        var depth = 0;
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    private static int GetLineNumber(string text, int index)
    {
        var line = 1;
        for (var position = 0; position < index; position++)
        {
            if (text[position] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
