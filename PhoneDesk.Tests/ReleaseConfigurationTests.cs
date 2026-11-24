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
    public void ReleasePlease_UsesPatToken()
    {
        // GITHUB_TOKEN cannot create the release tag once the release range modifies workflow files.
        var root = FindRepositoryRoot();
        var buildWorkflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));
        var match = System.Text.RegularExpressions.Regex.Match(
            buildWorkflow,
            @"uses: googleapis/release-please-action@[^\r\n]*\r?\n\s*with:\r?\n(?:\s*#[^\r\n]*\r?\n)*\s*token: ([^\r\n]+)");
        Assert.True(match.Success, "release-please token not found in build.yml");
        Assert.Equal("${{ secrets.PAT_TOKEN || secrets.GITHUB_TOKEN }}", match.Groups[1].Value.Trim());
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

    [Fact]
    public void McpServer_IsPublishedIntoTheAppFolderInEveryPackagingJob()
    {
        // The installers pack the app's publish folder. The server must be added to it right after
        // the app is published, in the release job and in the pull request packaging smoke job.
        var root = FindRepositoryRoot();
        var buildWorkflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));
        var publishSteps = System.Text.RegularExpressions.Regex.Matches(
            buildWorkflow,
            @"run: dotnet publish phonedesk\.csproj [^\r\n]*\r?\n\r?\n\s*- name: Add MCP server to the publish output[^\r\n]*\r?\n(?:\s*#[^\r\n]*\r?\n)*\s*env:\r?\n\s*RUNTIME: [^\r\n]*\r?\n\s*shell: bash\r?\n\s*run: bash scripts/publish-mcp-server\.sh ""\$RUNTIME"" ""\./publish/\$RUNTIME""");
        var appPublishes = System.Text.RegularExpressions.Regex.Matches(buildWorkflow, @"run: dotnet publish phonedesk\.csproj ");

        Assert.Equal(2, appPublishes.Count);
        Assert.Equal(appPublishes.Count, publishSteps.Count);
        Assert.True(File.Exists(Path.Combine(root, "scripts", "publish-mcp-server.sh")));
    }

    [Fact]
    public void McpServer_PackageVersionsKeepSharedFilesIdenticalToTheApp()
    {
        // The server shares the app folder. Microsoft.Extensions.Hosting 11 brings its own System.Text.Json
        // and other System.* files that differ from the .NET 10 runtime files the app ships; 10.0.x does not.
        // Microsoft.Extensions.DependencyInjection is in both closures and must be the same version.
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "phonedesk.csproj"));
        var server = File.ReadAllText(Path.Combine(root, "src", "PhoneDesk.Mcp", "PhoneDesk.Mcp.csproj"));

        static string Version(string project, string package)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                project,
                $@"<PackageReference Include=""{System.Text.RegularExpressions.Regex.Escape(package)}"" Version=""([^""]+)""");
            Assert.True(match.Success, $"{package} is not referenced");
            return match.Groups[1].Value;
        }

        Assert.StartsWith("10.0.", Version(server, "Microsoft.Extensions.Hosting"));
        Assert.Equal(
            Version(app, "Microsoft.Extensions.DependencyInjection"),
            Version(server, "Microsoft.Extensions.DependencyInjection"));
        Assert.DoesNotContain("PhoneDesk.Presentation", server);
    }

    [Fact]
    public void MsixManifest_StartsTheMcpServerThroughAnExecutionAlias()
    {
        // A packaged executable cannot be started by path from outside the package.
        var root = FindRepositoryRoot();
        var manifest = new XmlDocument();
        manifest.Load(Path.Combine(root, "installer", "msix", "AppxManifest.xml"));
        var namespaces = new XmlNamespaceManager(manifest.NameTable);
        namespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
        namespaces.AddNamespace("uap5", "http://schemas.microsoft.com/appx/manifest/uap/windows10/5");
        namespaces.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10");

        var server = manifest.SelectSingleNode("//f:Application[@Executable='phonedesk-mcp.exe']", namespaces);
        Assert.NotNull(server);
        Assert.Equal("none", server!.SelectSingleNode("uap:VisualElements/@AppListEntry", namespaces)?.Value);
        Assert.Equal(
            "phonedesk-mcp.exe",
            server.SelectSingleNode(".//uap5:ExecutionAlias/@Alias", namespaces)?.Value);
        // The desktop app keeps its own application entry without the console settings.
        Assert.Null(manifest.SelectSingleNode("//f:Application[@Executable='phonedesk.exe']/f:Extensions", namespaces));
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
