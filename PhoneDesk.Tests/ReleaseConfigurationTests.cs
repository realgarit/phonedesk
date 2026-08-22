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
