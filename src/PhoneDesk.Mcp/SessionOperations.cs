using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Topology;

namespace PhoneDesk.Mcp;

/// <summary>
/// Readiness, sign-in and topology operations for the headless server. Each one runs the same
/// script and applies the same success test as the matching desktop page.
/// </summary>
internal sealed class SessionOperations
{
    private static readonly string[] RequiredGraphModules =
    {
        ConstantsService.PowerShellModules.MicrosoftGraphAuthentication,
        ConstantsService.PowerShellModules.MicrosoftGraphUsers,
        ConstantsService.PowerShellModules.MicrosoftGraphUsersActions,
        ConstantsService.PowerShellModules.MicrosoftGraphGroups,
        ConstantsService.PowerShellModules.MicrosoftGraphIdentityDirectoryManagement
    };

    private readonly IPowerShellContextService _powerShell;
    private readonly IPowerShellCommandService _commands;
    private readonly IMsalGraphAuthenticationService _graphAuthentication;
    private readonly ISessionManager _session;
    private readonly ITenantTopologyAssembler _topologyAssembler;
    private readonly ITenantTopologyCache _topologyCache;

    public SessionOperations(
        IPowerShellContextService powerShell,
        IPowerShellCommandService commands,
        IMsalGraphAuthenticationService graphAuthentication,
        ISessionManager session,
        ITenantTopologyAssembler topologyAssembler,
        ITenantTopologyCache topologyCache)
    {
        _powerShell = powerShell;
        _commands = commands;
        _graphAuthentication = graphAuthentication;
        _session = session;
        _topologyAssembler = topologyAssembler;
        _topologyCache = topologyCache;
    }

    public SessionStatus GetStatus() => new(
        _session.ModulesChecked,
        _session.TeamsConnected,
        _session.GraphConnected,
        _session.TenantName,
        _session.TenantId,
        _session.GraphAccount);

    public async Task<OperationOutcome> CheckModulesAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(_commands.GetCheckModulesCommand(), null, cancellationToken);
        var output = result.Value ?? string.Empty;

        var teamsAvailable =
            output.Contains("MicrosoftTeams module is available", StringComparison.Ordinal) ||
            output.Contains("MicrosoftTeams module installed successfully", StringComparison.Ordinal);
        var missing = RequiredGraphModules
            .Where(module => !output.Contains(module + " module is available", StringComparison.Ordinal))
            .ToList();
        if (!teamsAvailable)
        {
            missing.Insert(0, ConstantsService.PowerShellModules.MicrosoftTeams);
        }

        var ready = missing.Count == 0;
        _session.UpdateModulesChecked(ready);

        return ready
            ? OperationOutcome.Ok("All required PowerShell modules are available.", GetStatus())
            : OperationOutcome.Fail(
                "Missing modules: " + string.Join(", ", missing) + ".",
                result.ErrorMessage ?? output,
                GetStatus());
    }

    public async Task<OperationOutcome> ConnectTeamsAsync(CancellationToken cancellationToken)
    {
        if (!_session.ModulesChecked)
        {
            return OperationOutcome.Fail(
                "Check the required modules before connecting to Microsoft Teams.", null, GetStatus());
        }

        var result = await RunAsync(_commands.GetConnectTeamsCommand(), null, cancellationToken);
        var connected = result.HasSuccessMarker;
        _session.UpdateTeamsConnection(connected);

        if (!connected)
        {
            return OperationOutcome.Fail(
                "Microsoft Teams could not be connected.", result.ErrorMessage ?? result.Value, GetStatus());
        }

        foreach (var line in (result.Value ?? string.Empty).Split('\n'))
        {
            if (!line.Contains("Connected to tenant:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(':')[1].Trim().Split('(');
            if (parts.Length >= 2)
            {
                _session.UpdateTenantInfo(parts[1].Trim(')').Trim(), parts[0].Trim());
            }
        }

        return OperationOutcome.Ok("Connected to Microsoft Teams.", GetStatus());
    }

    public async Task<OperationOutcome> ConnectGraphAsync(CancellationToken cancellationToken)
    {
        if (!_session.ModulesChecked)
        {
            return OperationOutcome.Fail(
                "Check the required modules before connecting to Microsoft Graph.", null, GetStatus());
        }

        var authentication = await _graphAuthentication.AuthenticateAsync();
        if (!authentication.Success || string.IsNullOrEmpty(authentication.AccessToken))
        {
            _session.UpdateGraphConnection(false);
            return OperationOutcome.Fail(
                "Microsoft Graph sign-in did not complete.", authentication.ErrorMessage, GetStatus());
        }

        // The token reaches the script through an environment variable, never as script text.
        var environment = new Dictionary<string, string> { ["TEAMS_PM_TOKEN"] = authentication.AccessToken };
        var result = await RunAsync(
            _commands.GetConnectGraphWithTokenCommand(authentication.AccessToken), environment, cancellationToken);
        var connected = result.HasSuccessMarker;
        _session.UpdateGraphConnection(connected, authentication.Account);

        return connected
            ? OperationOutcome.Ok("Connected to Microsoft Graph.", GetStatus())
            : OperationOutcome.Fail(
                "Microsoft Graph could not be connected.", result.ErrorMessage ?? result.Value, GetStatus());
    }

    public async Task<TopologyOutcome> GetTopologyAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (!_session.TeamsConnected || !_session.GraphConnected || string.IsNullOrWhiteSpace(_session.TenantId))
        {
            return new TopologyOutcome(
                false, "Connect to Microsoft Teams and Microsoft Graph first.", null, null);
        }

        if (!refresh && _topologyCache.HasValue && _topologyCache.Current is not null &&
            string.Equals(_topologyCache.TenantId, _session.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return new TopologyOutcome(true, "Topology from this session's cache.", _topologyCache.Current, null);
        }

        var result = await RunAsync(_commands.GetRetrieveTenantTopologyCommand(), null, cancellationToken);
        if (!result.IsSuccess)
        {
            return new TopologyOutcome(
                false, "The tenant topology could not be read.", null, result.ErrorMessage ?? result.Value);
        }

        var topology = _topologyAssembler.Assemble(result.Value, DateTimeOffset.UtcNow);
        _topologyCache.Set(_session.TenantId, topology);
        return new TopologyOutcome(true, "Topology read from the tenant.", topology, null);
    }

    private async Task<OperationResult<string>> RunAsync(
        string command, Dictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        var execution = await _powerShell.ExecuteCommandWithDetailsAsync(
            command, environment, progress: null, cancellationToken);
        return PowerShellOperationResultMapper.Map(execution);
    }
}

internal sealed record SessionStatus(
    bool ModulesChecked,
    bool TeamsConnected,
    bool GraphConnected,
    string? TenantName,
    string? TenantId,
    string? GraphAccount);

internal sealed record OperationOutcome(bool Success, string Message, string? Details, SessionStatus Status)
{
    public static OperationOutcome Ok(string message, SessionStatus status) => new(true, message, null, status);

    public static OperationOutcome Fail(string message, string? details, SessionStatus status) =>
        new(false, message, details, status);
}

internal sealed record TopologyOutcome(bool Success, string Message, TenantTopology? Topology, string? Details);
