using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Operations;

public sealed record ConnectionStatus(
    bool ModulesChecked,
    bool TeamsConnected,
    bool GraphConnected,
    string? TenantName,
    string? TenantId,
    string? GraphAccount);

public sealed record ConnectionOutcome(bool Success, string Message, string? Details, ConnectionStatus Status);

/// <summary>
/// Module check, sign-in and sign-out for a head without a UI. Each operation runs the same
/// script and applies the same success test as the desktop Readiness page.
/// </summary>
public sealed class TenantConnectionService
{
    private readonly ITenantCommandRunner _runner;
    private readonly IPowerShellCommandService _commands;
    private readonly IMsalGraphAuthenticationService _graphAuthentication;
    private readonly ISessionManager _session;
    private readonly ITenantTopologyCache _topologyCache;
    private readonly ITenantHealthCheckCache _healthCheckCache;

    public TenantConnectionService(
        ITenantCommandRunner runner,
        IPowerShellCommandService commands,
        IMsalGraphAuthenticationService graphAuthentication,
        ISessionManager session,
        ITenantTopologyCache topologyCache,
        ITenantHealthCheckCache healthCheckCache)
    {
        _runner = runner;
        _commands = commands;
        _graphAuthentication = graphAuthentication;
        _session = session;
        _topologyCache = topologyCache;
        _healthCheckCache = healthCheckCache;
    }

    public ConnectionStatus GetStatus() => new(
        _session.ModulesChecked,
        _session.TeamsConnected,
        _session.GraphConnected,
        _session.TenantName,
        _session.TenantId,
        _session.GraphAccount);

    /// <summary>True when both connections exist and the tenant is known.</summary>
    public bool IsReady
        => _session.TeamsConnected && _session.GraphConnected && !string.IsNullOrWhiteSpace(_session.TenantId);

    public async Task<ConnectionOutcome> CheckModulesAsync(CancellationToken cancellationToken)
    {
        var outcome = await _runner.RunAsync(
            new TenantCommandRequest(_commands.GetCheckModulesCommand(), "CheckModules"), cancellationToken);
        var output = outcome.Result.Value ?? outcome.Result.RawOutput;
        var missing = TenantOutputParser.FindMissingModules(output);
        _session.UpdateModulesChecked(missing.Count == 0);

        return missing.Count == 0
            ? Ok("All required PowerShell modules are available.")
            : Fail("Missing modules: " + string.Join(", ", missing) + ".", outcome.Result.ErrorMessage ?? output);
    }

    public async Task<ConnectionOutcome> ConnectTeamsAsync(CancellationToken cancellationToken)
    {
        if (!_session.ModulesChecked)
        {
            return Fail("Check the required modules before connecting to Microsoft Teams.", null);
        }

        var outcome = await _runner.RunAsync(
            new TenantCommandRequest(_commands.GetConnectTeamsCommand(), "ConnectTeams"), cancellationToken);
        var connected = outcome.Result.HasSuccessMarker;
        _session.UpdateTeamsConnection(connected);
        if (!connected)
        {
            return Fail("Microsoft Teams could not be connected.", outcome.Result.ErrorMessage ?? outcome.Result.RawOutput);
        }

        var tenant = TenantOutputParser.ParseConnectedTenant(outcome.Result.Value);
        if (tenant is not null)
        {
            _session.UpdateTenantInfo(tenant.Id, tenant.Name);
        }

        return Ok("Connected to Microsoft Teams.");
    }

    public async Task<ConnectionOutcome> ConnectGraphAsync(CancellationToken cancellationToken)
    {
        if (!_session.ModulesChecked)
        {
            return Fail("Check the required modules before connecting to Microsoft Graph.", null);
        }

        var authentication = await _graphAuthentication.AuthenticateAsync();
        if (!authentication.Success || string.IsNullOrEmpty(authentication.AccessToken))
        {
            _session.UpdateGraphConnection(false);
            return Fail("Microsoft Graph sign-in did not complete.", authentication.ErrorMessage);
        }

        // The token reaches the script through an environment variable, never as script text.
        var outcome = await _runner.RunAsync(
            new TenantCommandRequest(
                _commands.GetConnectGraphWithTokenCommand(authentication.AccessToken), "ConnectGraph")
            {
                EnvironmentVariables = new Dictionary<string, string> { ["TEAMS_PM_TOKEN"] = authentication.AccessToken }
            },
            cancellationToken);
        var connected = outcome.Result.HasSuccessMarker;
        _session.UpdateGraphConnection(connected, authentication.Account);

        return connected
            ? Ok("Connected to Microsoft Graph.")
            : Fail("Microsoft Graph could not be connected.", outcome.Result.ErrorMessage ?? outcome.Result.RawOutput);
    }

    /// <summary>Signs out of both services and forgets everything cached for the tenant.</summary>
    public async Task<ConnectionOutcome> DisconnectAsync(CancellationToken cancellationToken)
    {
        await _runner.RunAsync(
            new TenantCommandRequest(_commands.GetDisconnectTeamsCommand(), "DisconnectTeams"), cancellationToken);
        await _runner.RunAsync(
            new TenantCommandRequest(_commands.GetDisconnectGraphCommand(), "DisconnectGraph"), cancellationToken);
        await _graphAuthentication.SignOutAsync();

        var modulesChecked = _session.ModulesChecked;
        _session.ResetSession();
        _session.UpdateModulesChecked(modulesChecked);
        _topologyCache.Clear();
        _healthCheckCache.Clear();
        return Ok("Disconnected from Microsoft Teams and Microsoft Graph.");
    }

    private ConnectionOutcome Ok(string message) => new(true, message, null, GetStatus());

    private ConnectionOutcome Fail(string message, string? details) => new(false, message, details, GetStatus());
}
