using System.Text.Json;
using System.Xml;
using Xunit;

namespace PhoneDesk.Tests;

public sealed class ReleaseConfigurationTests
{
    [Fact]
    public void AppManifest_UsesFourPartVersion_AndIsNotRewrittenByReleasePlease()
    {
        var root = FindRepositoryRoot();
        var configPath = Path.Combine(root, "release-please-config.json");
        var manifestPath = Path.Combine(root, "app.manifest");
        var versionPath = Path.Combine(root, "version.txt");

        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        var extraFiles = config.RootElement
            .GetProperty("packages")
            .GetProperty(".")
            .GetProperty("extra-files");
        var managedPaths = extraFiles.EnumerateArray()
            .Select(entry => entry.ValueKind == JsonValueKind.String
                ? entry.GetString()
                : entry.GetProperty("path").GetString())
            .ToArray();
        Assert.DoesNotContain("app.manifest", managedPaths);

        var manifest = new XmlDocument { PreserveWhitespace = true };
        manifest.Load(manifestPath);
        var versionAttribute = manifest.SelectSingleNode(
            "//*[local-name()=\"assemblyIdentity\"]/@version") as XmlAttribute;
        Assert.NotNull(versionAttribute);

        var expectedVersion = File.ReadAllText(versionPath).Trim();
        Assert.Equal($"{expectedVersion}.0", versionAttribute!.Value);

        // Without the build.yml sync step, every release PR fails version validation.
        var buildWorkflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));
        Assert.Contains("name: Sync app.manifest on release pull request", buildWorkflow);
        Assert.True(
            buildWorkflow.IndexOf("name: Sync app.manifest on release pull request", StringComparison.Ordinal)
                < buildWorkflow.IndexOf("name: Start release pull request validation", StringComparison.Ordinal),
            "app.manifest must be synced before release pull request validation starts");
    }

    [Fact]
    public void MacOsMinimumVersion_MatchesBundleAndHomebrewCask()
    {
        // .NET 10 supports macOS 15 and later (dotnet/core release-notes/10.0/supported-os.json).
        // The app bundle and the generated Homebrew cask must declare the same floor.
        var root = FindRepositoryRoot();
        var packageScript = File.ReadAllText(Path.Combine(root, "scripts", "package-macos.sh"));
        var buildWorkflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));

        var plistMatch = System.Text.RegularExpressions.Regex.Match(
            packageScript,
            @"<key>LSMinimumSystemVersion</key>\s*<string>([0-9.]+)</string>");
        Assert.True(plistMatch.Success, "LSMinimumSystemVersion not found in package-macos.sh");
        Assert.Equal("15.0", plistMatch.Groups[1].Value);

        var caskMatch = System.Text.RegularExpressions.Regex.Match(
            buildWorkflow,
            @"depends_on macos: :([a-z_]+)");
        Assert.True(caskMatch.Success, "depends_on macos not found in build.yml");
        Assert.Equal("sequoia", caskMatch.Groups[1].Value);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PhoneDesk.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PhoneDesk repository root.");
    }
}
