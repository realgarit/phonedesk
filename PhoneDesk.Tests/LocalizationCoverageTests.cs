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
        "src/PhoneDesk.Presentation/Views/HistoryView.axaml"
    };

    private static readonly Regex AttributeRegex = new(
        "(?<attribute>Text|Content|Header|Title|ToolTip\\.Tip|Watermark|AutomationProperties\\.Name|OnContent|OffContent)=\"(?<value>[^\"]+)\"",
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
            var lines = File.ReadAllLines(fullPath);

            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in AttributeRegex.Matches(lines[index]))
                {
                    var value = match.Groups["value"].Value;
                    if (IsAllowed(value))
                    {
                        continue;
                    }

                    findings.Add($"{relativePath}:{index + 1} {match.Groups["attribute"].Value}=\"{value}\"");
                }
            }
        }

        Assert.True(findings.Count == 0, "Raw visible literals found:" + Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    private static bool IsAllowed(string value)
    {
        if (AllowedRawValues.Contains(value))
        {
            return true;
        }

        if (value.StartsWith("{Binding", StringComparison.Ordinal)
            || value.StartsWith("{loc:Translate", StringComparison.Ordinal)
            || value.StartsWith("{DynamicResource", StringComparison.Ordinal)
            || value.StartsWith("{x:Static", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Contains("StringFormat=", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
