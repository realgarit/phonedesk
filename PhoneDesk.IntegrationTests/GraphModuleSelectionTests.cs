using PhoneDesk.Services;
using PhoneDesk.Services.ScriptBuilders;

namespace PhoneDesk.IntegrationTests;

/// <summary>
/// Regression for the Microsoft Graph connect step on machines that also have a user-scope
/// Microsoft.Graph.* install (for example 2.41.2 in Documents\PowerShell\Modules next to the
/// bundled 2.41.1). A name-only import picked the newer user-scope Authentication module and the
/// other bundled modules then failed with "Microsoft.Graph.Authentication.Loader ... is already
/// loaded". Every Graph module must come from the bundled set.
/// </summary>
[Collection("Teams module selection")]
public sealed class GraphModuleSelectionTests : IDisposable
{
    private static readonly string[] GraphModules =
    [
        "Microsoft.Graph.Authentication",
        "Microsoft.Graph.Users",
        "Microsoft.Graph.Users.Actions",
        "Microsoft.Graph.Groups",
        "Microsoft.Graph.Identity.DirectoryManagement",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "phonedesk-graph-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _environment = new();
    private readonly CommonScriptBuilder _builder = new(new PowerShellSanitizationService());

    public GraphModuleSelectionTests()
    {
        foreach (var name in new[] { "PSModulePath", "MSAL_DISABLE_WAM", "AZURE_IDENTITY_DISABLE_WAM", "TEAMS_PM_TOKEN" })
            _environment[name] = Environment.GetEnvironmentVariable(name);
        Directory.CreateDirectory(AppDirectory);
    }

    private string AppDirectory => Path.Combine(_root, "PhoneDesk's app");
    private string BundleRoot => Path.Combine(AppDirectory, "Modules");
    private string UserRoot => Path.Combine(_root, "user-modules");

    [Fact]
    public async Task ConnectLoadsEveryGraphModuleFromTheBundleEvenWhenUserScopeHasANewerVersion()
    {
        // Newer user-scope install first on PSModulePath, like Documents\PowerShell\Modules.
        WriteGraphModule(UserRoot, "Microsoft.Graph.Authentication", "9.9.9");
        foreach (var module in GraphModules)
            WriteGraphModule(BundleRoot, module, "2.41.1");
        SetSearchPath();
        Environment.SetEnvironmentVariable("TEAMS_PM_TOKEN", "fixture-token");
        using var service = new PowerShellContextService(new TestLoggingService());

        var result = await Run(service, ForFixture(_builder.GetConnectGraphWithTokenCommand("unused")));

        Assert.False(result.HadErrors, result.Output);
        Assert.Contains("SUCCESS: Connected to Microsoft Graph", result.Output);
        Assert.Contains("STUB-MGCONNECT: 2.41.1", result.Output);
        Assert.DoesNotContain("STUB-MGCONNECT: 9.9.9", result.Output);
        foreach (var module in GraphModules)
        {
            Assert.Contains(
                $"INFO: Loaded {module} 2.41.1 from " + Path.Combine(BundleRoot, module),
                result.Output);
        }

        // Disconnect must reuse the loaded bundled module, not import the user-scope one.
        var disconnect = await Run(service, _builder.GetDisconnectGraphCommand());
        Assert.Contains("SUCCESS: Disconnected from Microsoft Graph", disconnect.Output);
        Assert.Contains("STUB-MGDISCONNECT: 2.41.1", disconnect.Output);
    }

    [Fact]
    public async Task ConnectFallsBackToSearchPathWhenNoBundleExists()
    {
        foreach (var module in GraphModules)
            WriteGraphModule(UserRoot, module, "3.0.0");
        SetSearchPath();
        Environment.SetEnvironmentVariable("TEAMS_PM_TOKEN", "fixture-token");
        using var service = new PowerShellContextService(new TestLoggingService());

        var result = await Run(service, ForFixture(_builder.GetConnectGraphWithTokenCommand("unused")));

        Assert.Contains("SUCCESS: Connected to Microsoft Graph", result.Output);
        Assert.Contains("STUB-MGCONNECT: 3.0.0", result.Output);
        Assert.DoesNotContain("INFO: Loaded Microsoft.Graph", result.Output);
    }

    private void SetSearchPath()
        => Environment.SetEnvironmentVariable("PSModulePath",
            string.Join(Path.PathSeparator, UserRoot, _environment["PSModulePath"]));

    private string ForFixture(string script)
        // Only redirect app-directory discovery; execute the production import/connect code.
        => script.Replace(
            "[System.IO.Path]::GetDirectoryName([System.Reflection.Assembly]::GetExecutingAssembly().Location)",
            "'" + AppDirectory.Replace("'", "''", StringComparison.Ordinal) + "'",
            StringComparison.Ordinal);

    private static Task<PowerShellExecutionResult> Run(PowerShellContextService service, string script)
        => service.ExecuteCommandWithDetailsAsync(script, null, null, default);

    private static void WriteGraphModule(string root, string name, string version)
    {
        var script = name == "Microsoft.Graph.Authentication"
            ? "function Connect-MgGraph {\n" +
              "    [CmdletBinding()]\n" +
              "    param([securestring] $AccessToken, [switch] $NoWelcome)\n" +
              "    Write-Host \"STUB-MGCONNECT: " + version + "\"\n" +
              "}\n" +
              "function Get-MgContext {\n" +
              "    [CmdletBinding()]\n" +
              "    param()\n" +
              "    [pscustomobject]@{ Account = 'fixture@example.test' }\n" +
              "}\n" +
              "function Disconnect-MgGraph {\n" +
              "    [CmdletBinding()]\n" +
              "    param()\n" +
              "    Write-Host \"STUB-MGDISCONNECT: " + version + "\"\n" +
              "}\n" +
              "Export-ModuleMember -Function Connect-MgGraph, Get-MgContext, Disconnect-MgGraph\n"
            : "# Discovery-only stub\n";

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
