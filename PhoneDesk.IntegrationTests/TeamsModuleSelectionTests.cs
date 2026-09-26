using PhoneDesk.Services;
using PhoneDesk.Services.ScriptBuilders;

namespace PhoneDesk.IntegrationTests;

// PSModulePath is process-wide. These fixtures must not overlap other runspace tests.
[CollectionDefinition("Teams module selection", DisableParallelization = true)]
public sealed class TeamsModuleSelectionCollection;

[Collection("Teams module selection")]
public sealed class TeamsModuleSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "phonedesk-teams-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _environment = new();
    private readonly CommonScriptBuilder _builder = new(new PowerShellSanitizationService());

    public TeamsModuleSelectionTests()
    {
        foreach (var name in new[] { "PSModulePath", "MSAL_DISABLE_WAM", "AZURE_IDENTITY_DISABLE_WAM" })
            _environment[name] = Environment.GetEnvironmentVariable(name);
        Directory.CreateDirectory(AppDirectory);
    }

    private string AppDirectory => Path.Combine(_root, "PhoneDesk's app");
    private string BundleRoot => Path.Combine(AppDirectory, "Modules");
    private string SystemRoot => Path.Combine(_root, "system-modules");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectUsesBundleEvenWhenOlderModuleIsEarlierOrAlreadyLoaded(bool preloadOldModule)
    {
        WriteTeamsModule(SystemRoot, "7.6.0", supportsDisableWam: false);
        WriteTeamsModule(BundleRoot, "7.9.0", supportsDisableWam: true);
        SetSearchPath();
        using var service = new PowerShellContextService(new TestLoggingService());
        if (preloadOldModule)
        {
            var preload = await Run(service, "Import-Module MicrosoftTeams -Force; (Get-Module MicrosoftTeams).Version.ToString()");
            Assert.Contains("7.6.0", preload.Output);
        }

        var result = await Run(service, ForFixture(_builder.GetConnectTeamsCommand()));

        Assert.False(result.HadErrors, result.Output);
        Assert.Contains("SUCCESS: Connected to Microsoft Teams", result.Output);
        Assert.Contains("STUB-CONNECT: 7.9.0 DisableWAM=True", result.Output);
        Assert.Contains("INFO: Loaded MicrosoftTeams 7.9.0 from " + Path.Combine(BundleRoot, "MicrosoftTeams"), result.Output);
        Assert.DoesNotContain("STUB-CONNECT: 7.6.0", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingBundleFailsWithoutFallingBackToInstalledModule(bool modulesDirectoryExists)
    {
        WriteTeamsModule(SystemRoot, "7.9.0", supportsDisableWam: true);
        if (modulesDirectoryExists)
            Directory.CreateDirectory(BundleRoot);
        SetSearchPath();
        using var service = new PowerShellContextService(new TestLoggingService());

        var result = await Run(service, ForFixture(_builder.GetConnectTeamsCommand()));

        Assert.Contains("ERROR: Failed to connect to Microsoft Teams:", result.Output);
        Assert.Contains("Reinstall PhoneDesk", result.Output);
        Assert.DoesNotContain("STUB-CONNECT:", result.Output);
        Assert.DoesNotContain("SUCCESS:", result.Output);
    }

    [Fact]
    public async Task IncompatibleBundleFailsBeforeAuthenticationEvenWithCompatibleInstalledModule()
    {
        WriteTeamsModule(SystemRoot, "7.9.0", supportsDisableWam: true);
        WriteTeamsModule(BundleRoot, "7.6.0", supportsDisableWam: false);
        SetSearchPath();
        using var service = new PowerShellContextService(new TestLoggingService());

        var result = await Run(service, ForFixture(_builder.GetConnectTeamsCommand()));

        Assert.Contains("INFO: Loaded MicrosoftTeams 7.6.0", result.Output);
        Assert.Contains("does not expose Connect-MicrosoftTeams -DisableWAM", result.Output);
        Assert.Contains("Restart PhoneDesk", result.Output);
        Assert.DoesNotContain("STUB-CONNECT:", result.Output);
        Assert.DoesNotContain("SUCCESS:", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModuleCheckValidatesBundledCommandWithoutSigningIn(bool compatibleBundle)
    {
        WriteTeamsModule(SystemRoot, "7.9.0", supportsDisableWam: true);
        WriteTeamsModule(BundleRoot, compatibleBundle ? "7.9.0" : "7.6.0", compatibleBundle);
        foreach (var name in new[] { "Authentication", "Users", "Users.Actions", "Groups", "Identity.DirectoryManagement" })
            WriteModule(BundleRoot, "Microsoft.Graph." + name, "2.39.0", "# Discovery-only stub");
        SetSearchPath();
        using var service = new PowerShellContextService(new TestLoggingService());

        var result = await Run(service, ForFixture(_builder.GetCheckModulesCommand()));

        Assert.DoesNotContain("STUB-CONNECT:", result.Output);
        if (compatibleBundle)
        {
            Assert.DoesNotContain("ERROR:", result.Output);
            Assert.Contains("MicrosoftTeams module is available: 7.9.0", result.Output);
            // Checking and then connecting in the same persistent runspace must work.
            var connection = await Run(service, ForFixture(_builder.GetConnectTeamsCommand()));
            Assert.Contains("STUB-CONNECT: 7.9.0 DisableWAM=True", connection.Output);
        }
        else
        {
            Assert.Contains("ERROR: MicrosoftTeams module is not ready:", result.Output);
            Assert.DoesNotContain("MicrosoftTeams module is available:", result.Output);
        }
    }

    private void SetSearchPath()
        => Environment.SetEnvironmentVariable("PSModulePath",
            string.Join(Path.PathSeparator, SystemRoot, BundleRoot, _environment["PSModulePath"]));

    private string ForFixture(string script)
        // Only redirect app-directory discovery; execute the production selection/check/connect code.
        => script.Replace(
            "[System.IO.Path]::GetDirectoryName([System.Reflection.Assembly]::GetExecutingAssembly().Location)",
            "'" + AppDirectory.Replace("'", "''", StringComparison.Ordinal) + "'",
            StringComparison.Ordinal);

    private static Task<PowerShellExecutionResult> Run(PowerShellContextService service, string script)
        => service.ExecuteCommandWithDetailsAsync(script, null, null, default);

    private static void WriteTeamsModule(string root, string version, bool supportsDisableWam)
    {
        var parameters = supportsDisableWam ? "[switch] $DisableWAM" : "";
        WriteModule(root, "MicrosoftTeams", version, $$"""
            function Connect-MicrosoftTeams {
                [CmdletBinding()]
                param({{parameters}})
                Write-Host "STUB-CONNECT: {{version}} DisableWAM=$DisableWAM"
            }
            function Get-CsTenant {
                [CmdletBinding()]
                param()
                [pscustomobject]@{ DisplayName = 'Local fixture'; TenantId = 'stub-tenant' }
            }
            Export-ModuleMember -Function Connect-MicrosoftTeams, Get-CsTenant
            """);
    }

    private static void WriteModule(string root, string name, string version, string script)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".psm1"), script);
        File.WriteAllText(Path.Combine(directory, name + ".psd1"),
            $"@{{ RootModule = '{name}.psm1'; ModuleVersion = '{version}'; FunctionsToExport = '*'; CmdletsToExport = @() }}");
    }

    public void Dispose()
    {
        foreach (var (name, value) in _environment)
            Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(_root, recursive: true);
    }
}
