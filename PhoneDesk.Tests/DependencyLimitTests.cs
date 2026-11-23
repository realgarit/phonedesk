using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PhoneDesk.Tests;

/// <summary>
/// Guards the accepted Avalonia 11 limit (docs/dependency-limits.md, issue 203) in every
/// project file. Dependabot's ignore rules alone do not hold it: its NuGet updater has
/// rewritten Avalonia to 12.0.0 and FluentIcons.Avalonia to 2.1.345 while applying an
/// Avalonia.Skia patch update, although both versions were ignored.
/// </summary>
public sealed partial class DependencyLimitTests
{
    private const string FluentIconsVersion = "[1.1.223]";

    [Fact]
    public void AvaloniaPackages_StayBelow12_AndShareOneVersion()
    {
        var references = PackageReferences()
            .Where(r => r.Name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(references);
        Assert.All(references, r => Assert.True(
            Avalonia11Range().IsMatch(r.Version),
            $"{r.Project}: {r.Name} must use an [11.x.y,12.0.0) range but has '{r.Version}'."));

        var versions = references.Select(r => r.Version).Distinct().ToArray();
        Assert.True(
            versions.Length == 1,
            "Every Avalonia package must be on the same version, found: "
            + string.Join(", ", references.Select(r => $"{r.Project}: {r.Name} {r.Version}")));
    }

    [Fact]
    public void FluentPackages_StayOnTheirAvalonia11Versions()
    {
        var references = PackageReferences().ToArray();

        var theme = references.Where(r => r.Name == "FluentAvaloniaUI").ToArray();
        Assert.NotEmpty(theme);
        Assert.All(theme, r => Assert.True(
            FluentAvalonia2Range().IsMatch(r.Version),
            $"{r.Project}: FluentAvaloniaUI must use a [2.x.y,3.0.0) range but has '{r.Version}'."));

        var icons = references.Where(r => r.Name == "FluentIcons.Avalonia").ToArray();
        Assert.NotEmpty(icons);
        Assert.All(icons, r => Assert.True(
            r.Version == FluentIconsVersion,
            $"{r.Project}: FluentIcons.Avalonia must stay on {FluentIconsVersion} but has '{r.Version}'."));
    }

    private static IEnumerable<(string Project, string Name, string Version)> PackageReferences()
    {
        var root = FindRepositoryRoot();
        var projects = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(root, path)));

        foreach (var project in projects)
        {
            var relative = Path.GetRelativePath(root, project).Replace(Path.DirectorySeparatorChar, '/');
            foreach (var reference in XDocument.Load(project).Descendants("PackageReference"))
            {
                var name = (string?)reference.Attribute("Include");
                if (name is null)
                {
                    continue;
                }

                var version = (string?)reference.Attribute("Version") ?? (string?)reference.Element("Version") ?? string.Empty;
                yield return (relative, name, version);
            }
        }
    }

    private static bool IsBuildOutput(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(part => part is "bin" or "obj" or "publish" or "artifacts" or ".git");
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

    [GeneratedRegex(@"^\[11\.\d+\.\d+,12\.0\.0\)$")]
    private static partial Regex Avalonia11Range();

    [GeneratedRegex(@"^\[2\.\d+\.\d+,3\.0\.0\)$")]
    private static partial Regex FluentAvalonia2Range();
}
