using Guard = PhoneDesk.LocalizationGuard.LocalizationGuard;

namespace PhoneDesk.Tests;

public sealed class LocalizationGuardTests
{
    [Fact]
    public void MatchingCatalogsAndApprovedBindingsHaveNoFindings()
    {
        using var fixture = GuardFixture.Create();

        var findings = Guard.Scan(fixture.Root);

        Assert.Empty(findings);
    }

    [Fact]
    public void MissingGermanKeyIsReported()
    {
        using var fixture = GuardFixture.Create();
        fixture.WriteCatalog("Strings.de.json", "{\"WelcomeTitle\":\"Willkommen\"}");

        var findings = Guard.Scan(fixture.Root);

        Assert.Contains(findings, finding =>
            finding.Message.Contains("German catalog is missing key 'Greeting'", StringComparison.Ordinal));
    }

    [Fact]
    public void PlaceholderNamesAndMultiplicityMustMatch()
    {
        using var fixture = GuardFixture.Create();
        fixture.WriteCatalog(
            "Strings.de.json",
            "{\"WelcomeTitle\":\"Willkommen {name}\",\"Greeting\":\"Hallo {name}\"}");
        fixture.WriteCatalog(
            "Strings.en.json",
            "{\"WelcomeTitle\":\"Welcome {name}\",\"Greeting\":\"Hello {name} {name}\"}");

        var findings = Guard.Scan(fixture.Root);

        Assert.Contains(findings, finding =>
            finding.Message.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlaceholderOrderMayChangeWhenNamesAndMultiplicityMatch()
    {
        using var fixture = GuardFixture.Create();
        fixture.WriteCatalog(
            "Strings.en.json",
            "{\"WelcomeTitle\":\"Welcome\",\"Greeting\":\"Hello {first} {second}\"}");
        fixture.WriteCatalog(
            "Strings.de.json",
            "{\"WelcomeTitle\":\"Willkommen\",\"Greeting\":\"Hallo {second} {first}\"}");

        var findings = Guard.Scan(fixture.Root);

        Assert.Empty(findings);
    }

    [Fact]
    public void DuplicateJsonKeysAreReported()
    {
        using var fixture = GuardFixture.Create();
        fixture.WriteCatalog(
            "Strings.de.json",
            "{\"WelcomeTitle\":\"Willkommen\",\"WelcomeTitle\":\"Hallo\",\"Greeting\":\"Guten Tag\"}");

        var findings = Guard.Scan(fixture.Root);

        Assert.Contains(findings, finding =>
            finding.Message.Contains("duplicate JSON key 'WelcomeTitle'", StringComparison.Ordinal));
    }

    [Fact]
    public void TrailingJsonContentIsReported()
    {
        using var fixture = GuardFixture.Create();
        fixture.WriteCatalog(
            "Strings.de.json",
            "{\"WelcomeTitle\":\"Willkommen\",\"Greeting\":\"Hallo {name}\"} {} ");

        var findings = Guard.Scan(fixture.Root);

        Assert.Contains(findings, finding =>
            finding.Message.Contains("trailing JSON content", StringComparison.Ordinal));
    }

    [Fact]
    public void RawXamlTextIsRejectedButBindingsAndTechnicalValuesAreAllowed()
    {
        using var fixture = GuardFixture.Create();
        fixture.WriteView(
            "<StackPanel>\n"
            + "  <TextBlock Text=\"Manual label\" />\n"
            + "  <TextBlock Text=\"{Binding DisplayName}\" />\n"
            + "  <TextBlock Text=\"{loc:Translate Key=WelcomeTitle}\" />\n"
            + "  <TextBlock Text=\"PhoneDesk\" />\n"
            + "  <TextBlock Text=\"UPN\" />\n"
            + "</StackPanel>");

        var findings = Guard.Scan(fixture.Root);

        var finding = Assert.Single(findings, item => item.FilePath.EndsWith("Main.axaml", StringComparison.Ordinal));
        Assert.Contains("typed translation", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultilineRawXamlTextIsRejected()
    {
        using var fixture = GuardFixture.Create();
        fixture.WriteView("<TextBlock\n  Text =\n    \"Manual label\" />");

        var findings = Guard.Scan(fixture.Root);

        Assert.Contains(findings, finding => finding.FilePath.EndsWith("Main.axaml", StringComparison.Ordinal));
    }

    [Fact]
    public void SingleQuotedAttributesAndPropertyElementsAreRejected()
    {
        using var fixture = GuardFixture.Create();
        fixture.WriteView(
            "<StackPanel>\n"
            + "  <TextBlock Text='Manual label' />\n"
            + "  <Button.Content>Manual action</Button.Content>\n"
            + "</StackPanel>");

        var findings = Guard.Scan(fixture.Root);

        Assert.Equal(2, findings.Count(finding => finding.FilePath.EndsWith("Main.axaml", StringComparison.Ordinal)));
    }

    [Fact]
    public void ImplicitControlContentIsRejected()
    {
        using var fixture = GuardFixture.Create();
        fixture.WriteView("<Button>Manual action</Button>");

        var findings = Guard.Scan(fixture.Root);

        Assert.Contains(findings, finding =>
            finding.Message.Contains("Raw visible XAML Button value", StringComparison.Ordinal));
    }

    [Fact]
    public void RawStatusAssignmentIsRejectedButTypedFallbackIsAllowed()
    {
        using var fixture = GuardFixture.Create();
        fixture.WriteViewModel(
            "namespace PhoneDesk.ViewModels;\n"
            + "public sealed class SampleViewModel\n"
            + "{\n"
            + "    public string StatusMessage { get; set; } = string.Empty;\n"
            + "    public void Bad() => StatusMessage = \"Manual status\";\n"
            + "    public void Good() => StatusMessage = GetText(UiTextKey.WelcomeTitle, \"Welcome\");\n"
            + "}");

        var findings = Guard.Scan(fixture.Root);

        var finding = Assert.Single(findings, item => item.FilePath.EndsWith("SampleViewModel.cs", StringComparison.Ordinal));
        Assert.Contains("GetText", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RawCSharpUiSinksAndFilePickerLabelsAreRejected()
    {
        using var fixture = GuardFixture.Create();
        fixture.WritePresentationCode(
            "using Avalonia.Controls;\n"
            + "using Avalonia.Platform.Storage;\n"
            + "namespace PhoneDesk;\n"
            + "public sealed class SampleCode\n"
            + "{\n"
            + "    private string _manualMessage = \"Manual message\";\n"
            + "    public void Bad()\n"
            + "    {\n"
            + "        var control = new TextBlock { Text = \"Manual label\" };\n"
            + "        var type = new FilePickerFileType(\"CSV files\");\n"
            + "    }\n"
            + "}");

        var findings = Guard.Scan(fixture.Root);

        Assert.Contains(findings, finding => finding.Message.Contains("Raw C# UI sink Text", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Message.Contains("File picker type labels", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.Message.Contains("Raw user-visible C# field", StringComparison.Ordinal));
    }

    [Fact]
    public void RawStringBuilderReportTextIsRejected()
    {
        using var fixture = GuardFixture.Create();
        fixture.WritePresentationCode(
            "using System.Text;\n"
            + "namespace PhoneDesk;\n"
            + "public sealed class SampleReport\n"
            + "{\n"
            + "    public void Bad(StringBuilder doc) => doc.AppendLine(\"Manual report label\");\n"
            + "}");

        var findings = Guard.Scan(fixture.Root);

        Assert.Contains(findings, finding =>
            finding.Message.Contains("Raw report text", StringComparison.Ordinal));
    }

    private sealed class GuardFixture : IDisposable
    {
        private GuardFixture(string root)
        {
            Root = root;
        }

        public string Root { get; }

        public static GuardFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "phonedesk-localization-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "src", "PhoneDesk.Presentation", "Localization"));
            Directory.CreateDirectory(Path.Combine(root, "src", "PhoneDesk.Presentation", "Resources", "Localization"));
            Directory.CreateDirectory(Path.Combine(root, "src", "PhoneDesk.Presentation", "Views"));
            Directory.CreateDirectory(Path.Combine(root, "src", "PhoneDesk.Presentation", "ViewModels"));

            File.WriteAllText(
                Path.Combine(root, "src", "PhoneDesk.Presentation", "Localization", "UiTextKey.cs"),
                "namespace PhoneDesk.Localization;\n"
                + "public enum UiTextKey\n"
                + "{\n"
                + "    WelcomeTitle,\n"
                + "    Greeting\n"
                + "}");

            var english = "{\"WelcomeTitle\":\"Welcome\",\"Greeting\":\"Hello {name}\"}";
            var german = "{\"WelcomeTitle\":\"Willkommen\",\"Greeting\":\"Hallo {name}\"}";
            File.WriteAllText(Path.Combine(root, "src", "PhoneDesk.Presentation", "Resources", "Localization", "Strings.en.json"), english);
            File.WriteAllText(Path.Combine(root, "src", "PhoneDesk.Presentation", "Resources", "Localization", "Strings.de.json"), german);

            var fixture = new GuardFixture(root);
            fixture.WriteView("<TextBlock Text=\"{loc:Translate Key=WelcomeTitle}\" />");
            fixture.WriteViewModel(
                "using PhoneDesk.Localization;\n"
                + "namespace PhoneDesk.ViewModels;\n"
                + "public sealed class SampleViewModel\n"
                + "{\n"
                + "    public string StatusMessage { get; set; } = string.Empty;\n"
                + "    public void Good() => StatusMessage = GetText(UiTextKey.WelcomeTitle, \"Welcome\");\n"
                + "    private static string GetText(UiTextKey key, string fallback) => fallback;\n"
                + "}");
            return fixture;
        }

        public void WriteCatalog(string name, string content)
            => File.WriteAllText(
                Path.Combine(Root, "src", "PhoneDesk.Presentation", "Resources", "Localization", name),
                content);

        public void WriteView(string content)
            => File.WriteAllText(
                Path.Combine(Root, "src", "PhoneDesk.Presentation", "Views", "Main.axaml"),
                content);

        public void WriteViewModel(string content)
            => File.WriteAllText(
                Path.Combine(Root, "src", "PhoneDesk.Presentation", "ViewModels", "SampleViewModel.cs"),
                content);

        public void WritePresentationCode(string content)
            => File.WriteAllText(
                Path.Combine(Root, "src", "PhoneDesk.Presentation", "Views", "SampleCode.cs"),
                content);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
