using System.Text.RegularExpressions;
using System.Xml;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using Xunit;

namespace PhoneDesk.Tests;

/// <summary>
/// Guards the Microsoft Store MSIX packaging inputs (installer/msix, scripts/package-msix.ps1)
/// and the packaged-install behaviour of the composition root.
/// </summary>
public sealed class MsixPackagingTests
{
    private static readonly string[] RequiredAssets =
    [
        "StoreLogo.scale-100.png",
        "Square44x44Logo.scale-100.png",
        "Square44x44Logo.targetsize-16_altform-unplated.png",
        "Square44x44Logo.targetsize-256_altform-unplated.png",
        "Square71x71Logo.scale-100.png",
        "Square150x150Logo.scale-100.png",
        "Square150x150Logo.scale-200.png",
        "Square310x310Logo.scale-100.png",
        "Wide310x150Logo.scale-100.png",
    ];

    [Fact]
    public void ManifestTemplate_IsWellFormed_AndDeclaresFullTrustDesktopApp()
    {
        var root = FindRepositoryRoot();
        var manifest = new XmlDocument();
        manifest.Load(Path.Combine(root, "installer", "msix", "AppxManifest.xml"));

        var ns = new XmlNamespaceManager(manifest.NameTable);
        ns.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
        ns.AddNamespace("rescap", "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities");

        var identity = manifest.SelectSingleNode("/m:Package/m:Identity", ns) as XmlElement;
        Assert.NotNull(identity);
        Assert.Equal("__IDENTITY_NAME__", identity!.GetAttribute("Name"));
        Assert.Equal("__PUBLISHER__", identity.GetAttribute("Publisher"));
        Assert.Equal("__VERSION__", identity.GetAttribute("Version"));
        Assert.Equal("x64", identity.GetAttribute("ProcessorArchitecture"));

        var family = manifest.SelectSingleNode("/m:Package/m:Dependencies/m:TargetDeviceFamily", ns) as XmlElement;
        Assert.NotNull(family);
        Assert.Equal("Windows.Desktop", family!.GetAttribute("Name"));

        var application = manifest.SelectSingleNode("/m:Package/m:Applications/m:Application", ns) as XmlElement;
        Assert.NotNull(application);
        Assert.Equal("phonedesk.exe", application!.GetAttribute("Executable"));
        Assert.Equal("Windows.FullTrustApplication", application.GetAttribute("EntryPoint"));

        var fullTrust = manifest.SelectSingleNode("/m:Package/m:Capabilities/rescap:Capability[@Name='runFullTrust']", ns);
        Assert.NotNull(fullTrust);
    }

    [Fact]
    public void ManifestTemplate_ReferencesOnlyAssetsThatExist()
    {
        var root = FindRepositoryRoot();
        var msixRoot = Path.Combine(root, "installer", "msix");
        var manifestText = File.ReadAllText(Path.Combine(msixRoot, "AppxManifest.xml"));

        foreach (Match match in Regex.Matches(manifestText, @"Assets\\([A-Za-z0-9]+)\.png"))
        {
            var baseName = match.Groups[1].Value;
            var candidates = Directory.GetFiles(Path.Combine(msixRoot, "Assets"), $"{baseName}.*.png");
            Assert.True(candidates.Length > 0, $"No scaled asset found for Assets\\{baseName}.png");
        }

        foreach (var asset in RequiredAssets)
        {
            Assert.True(File.Exists(Path.Combine(msixRoot, "Assets", asset)), $"Missing MSIX asset {asset}");
        }
    }

    [Fact]
    public void PackageScript_ReplacesEveryPlaceholder_AndIsWiredIntoCi()
    {
        var root = FindRepositoryRoot();
        var manifestText = File.ReadAllText(Path.Combine(root, "installer", "msix", "AppxManifest.xml"));
        var scriptText = File.ReadAllText(Path.Combine(root, "scripts", "package-msix.ps1"));
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));

        var placeholders = Regex.Matches(manifestText, "__[A-Z_]+__")
            .Select(m => m.Value)
            .Distinct()
            .ToArray();
        Assert.NotEmpty(placeholders);
        foreach (var placeholder in placeholders)
        {
            Assert.Contains($"Replace('{placeholder}'", scriptText);
        }

        Assert.Contains("scripts/package-msix.ps1", workflow);
        Assert.Contains("artifacts/phonedesk-win-x64.msix", workflow);
    }

    [Fact]
    public void UnpackagedProcess_IsNotTreatedAsStoreInstall()
    {
        // Test hosts never run with package identity, so this must be false on every platform.
        var context = new WindowsInstallationContext();
        Assert.False(context.IsPackaged);
    }

    [Fact]
    public async Task StoreManagedUpdateCheck_NeverReportsAnUpdate()
    {
        IUpdateCheckService service = new StoreManagedUpdateCheckService();
        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);
        Assert.Null(result);
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
