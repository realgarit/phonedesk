using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Operations;

public sealed record ConnectionStatus(
    bool ModulesChecked,
    bool TeamsConnected,
    bool GraphConnected,
    string? TenantName,
    string? TenantId,
    string? GraphAccount);

/// <param name="WaitingForSignIn">
/// True when the browser sign-in is still open. Nothing failed: the caller asks the user to finish
/// the sign-in and then calls the same operation again, which continues with the same sign-in.
/// </param>
public sealed record ConnectionOutcome(
    bool Success,
    string Message,
    string? Details,
    ConnectionStatus Status,
    bool WaitingForSignIn = false);

/// <summary>
/// Module check, sign-in and sign-out for a head without a UI. Each operation runs the same
/// script and applies the same success test as the desktop Readiness page.
/// </summary>
/// <remarks>
/// A browser sign-in cannot be cancelled once the window is open, and it has no time limit of its
/// own. A head without a UI must still answer its caller in time, so a sign-in runs in the
/// background: a connect call waits a short time and reports "waiting for sign-in" when the user
/// has not finished. The next connect call continues with the same sign-in.
/// </remarks>
public sealed class TenantConnectionService
{
    private readonly ITenantCommandRunner _runner;
    private readonly IPowerShellCommandService _commands;
    private readonly IMsalGraphAuthenticationService _graphAuthentication;
    private readonly ISessionManager _session;
    private readonly ITenantTopologyCache _topologyCache;
    private readonly ITenantHealthCheckCache _healthCheckCache;
    private readonly TenantIdentityGuard _identity;
    private readonly IPowerShellContextService? _host;
    private readonly Lock _gate = new();

    private Task<TenantCommandOutcome>? _pendingTeamsSignIn;
    private Task<(bool Success, string? AccessToken, string? Account, string? ErrorMessage)>? _pendingGraphSignIn;

    public TenantConnectionService(
        ITenantCommandRunner runner,
        IPowerShellCommandService commands,
        IMsalGraphAuthenticationService graphAuthentication,
        ISessionManager session,
        ITenantTopologyCache topologyCache,
        ITenantHealthCheckCache healthCheckCache,
        TenantIdentityGuard? identity = null,
        IPowerShellContextService? host = null)
    {
        _identity = identity ?? new TenantIdentityGuard();
        _host = host;
        _runner = runner;
        _commands = commands;
        _graphAuthentication = graphAuthentication;
        _session = session;
        _topologyCache = topologyCache;
        _healthCheckCache = healthCheckCache;
    }

    /// <summary>How long one connect call waits for the user before it reports "waiting for sign-in".</summary>
    public TimeSpan SignInWait { get; init; } = TimeSpan.FromSeconds(45);

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

    /// <summary>
    /// True while the Teams sign-in window is open. The sign-in holds the PowerShell session, so
    /// no other command can run until the user finishes it.
    /// </summary>
    public bool TeamsSignInOpen
    {
        get
        {
            lock (_gate)
            {
                return _pendingTeamsSignIn is { IsCompleted: false };
            }
        }
    }

    /// <summary>True while the Microsoft Graph sign-in window is open.</summary>
    public bool GraphSignInOpen
    {
        get
        {
            lock (_gate)
            {
                return _pendingGraphSignIn is { IsCompleted: false };
            }
        }
    }

    public TenantIdentityGuard Identity => _identity;

    /// <summary>
    /// Gives up the Microsoft Graph sign-in that is open, so the next connect call opens a new
    /// window. The old attempt cannot be stopped, but its result is never used: a sign-in that is
    /// finished later in the old window connects nothing. The Microsoft Teams connection stays.
    /// Returns false when no Microsoft Graph sign-in was open.
    /// </summary>
    public bool AbandonGraphSignIn()
    {
        lock (_gate)
        {
            if (_pendingGraphSignIn is null)
            {
                return false;
            }

            _pendingGraphSignIn = null;
            return true;
        }
    }

    /// <summary>
    /// Gives up the Microsoft Teams sign-in that is open. The sign-in holds its PowerShell session
    /// and cannot be stopped, so the session is given up as a whole and a new one takes its place.
    /// Both connections of that session are lost: Microsoft Teams and Microsoft Graph need a new
    /// sign-in (the Graph sign-in is usually silent, because its account is still known). Whatever
    /// the old window reports later reaches nobody. Returns false when no Teams sign-in was open,
    /// or when the host cannot start a new session.
    /// </summary>
    public bool AbandonTeamsSignIn()
    {
        lock (_gate)
        {
            if (_pendingTeamsSignIn is not { IsCompleted: false } || _host is null || !_host.AbandonSession())
            {
                return false;
            }

            _pendingTeamsSignIn = null;
            _pendingGraphSignIn = null;
        }

        ForgetConnections();
        return true;
    }

    /// <summary>
    /// Takes up the PowerShell session and the Graph sign-in of a profile. Each profile keeps its
    /// own while the application runs, so after a switch back its connections are there again
    /// without a sign-in. The state of the connections is read from the session, never carried over
    /// from the profile before.
    /// </summary>
    public async Task<ConnectionOutcome> UseProfileSessionAsync(string profileKey, CancellationToken cancellationToken)
    {
        if (TeamsSignInOpen)
        {
            return Waiting(TeamsSignInOpenMessage);
        }

        lock (_gate)
        {
            _pendingGraphSignIn = null;
        }

        await ProfileSession.UseAsync(
            profileKey, _host, _graphAuthentication, _session, _identity,
            async script =>
            {
                var outcome = await _runner.RunAsync(
                    new TenantCommandRequest(script, "ReadConnectionIdentity") { IsIdempotentRead = true }, cancellationToken);
                return string.IsNullOrEmpty(outcome.Result.RawOutput) ? outcome.Result.Value : outcome.Result.RawOutput;
            },
            () =>
            {
                _topologyCache.Clear();
                _healthCheckCache.Clear();
            });
        return Ok(_session.TeamsConnected || _session.GraphConnected
            ? "The profile's connections are in use again."
            : "The profile has no connection yet. Sign in to Microsoft Teams and Microsoft Graph.");
    }

    /// <summary>Forgets the connection state and everything read from the tenant. The module check stays.</summary>
    private void ForgetConnections()
    {
        var modulesChecked = _session.ModulesChecked;
        _session.ResetSession();
        _session.UpdateModulesChecked(modulesChecked);
        _topologyCache.Clear();
        _healthCheckCache.Clear();
        _identity.ClearIdentity();
    }

    /// <summary>
    /// Reads who the connections are signed in as and keeps the result for the checks before a
    /// change. Returns null when the identity could not be read; that is not a mismatch.
    /// </summary>
    public async Task<TenantIdentityInfo?> RefreshIdentityAsync(CancellationToken cancellationToken)
    {
        if (TeamsSignInOpen || (!_session.TeamsConnected && !_session.GraphConnected))
        {
            return _identity.Identity;
        }

        var outcome = await _runner.RunAsync(
            new TenantCommandRequest(TenantIdentity.GetReadCommand(), "ReadConnectionIdentity") { IsIdempotentRead = true }, cancellationToken);
        try
        {
            var identity = TenantIdentity.Parse(string.IsNullOrEmpty(outcome.Result.RawOutput) ? outcome.Result.Value : outcome.Result.RawOutput);
            _identity.Update(identity);
            return identity;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public async Task<ConnectionOutcome> CheckModulesAsync(CancellationToken cancellationToken)
    {
        if (TeamsSignInOpen)
        {
            return Waiting(TeamsSignInOpenMessage);
        }

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

        Task<TenantCommandOutcome> signIn;
        lock (_gate)
        {
            // The sign-in is not tied to this call's cancellation: it cannot be cancelled anyway,
            // and the next call must be able to pick it up.
            signIn = _pendingTeamsSignIn ??= _runner.RunAsync(
                new TenantCommandRequest(_commands.GetConnectTeamsCommand(), "ConnectTeams"), CancellationToken.None);
        }

        if (!await FinishedInTimeAsync(signIn, cancellationToken))
        {
            return Waiting(TeamsSignInOpenMessage);
        }

        lock (_gate)
        {
            // The attempt was given up while this call waited: its result belongs to a session that
            // is gone, and a newer attempt may be open. Neither is touched.
            if (!ReferenceEquals(_pendingTeamsSignIn, signIn))
            {
                return Fail("The Microsoft Teams sign-in was given up. Call connect_teams to sign in again.", null);
            }

            _pendingTeamsSignIn = null;
        }

        var outcome = await signIn;
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

        return await WithIdentityAsync("Connected to Microsoft Teams.", cancellationToken);
    }

    public async Task<ConnectionOutcome> ConnectGraphAsync(CancellationToken cancellationToken)
    {
        if (!_session.ModulesChecked)
        {
            return Fail("Check the required modules before connecting to Microsoft Graph.", null);
        }

        if (TeamsSignInOpen)
        {
            return Waiting(TeamsSignInOpenMessage);
        }

        Task<(bool Success, string? AccessToken, string? Account, string? ErrorMessage)> signIn;
        lock (_gate)
        {
            signIn = _pendingGraphSignIn ??= _graphAuthentication.AuthenticateAsync();
        }

        if (!await FinishedInTimeAsync(signIn, cancellationToken))
        {
            return Waiting(
                "The Microsoft Graph sign-in window is open in the browser. Ask the user to complete the sign-in, " +
                "then call connect_graph again.");
        }

        lock (_gate)
        {
            // This attempt was given up while the call waited (restart_sign_in), or a newer attempt
            // took its place: its result must not connect anything, and the newer attempt stays.
            if (!ReferenceEquals(_pendingGraphSignIn, signIn))
            {
                return Fail(
                    "This Microsoft Graph sign-in was given up while the call waited. Nothing was connected by it. Call connect_graph again.",
                    null);
            }

            _pendingGraphSignIn = null;
        }

        var authentication = await signIn;
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
            ? await WithIdentityAsync("Connected to Microsoft Graph.", cancellationToken)
            : Fail("Microsoft Graph could not be connected.", outcome.Result.ErrorMessage ?? outcome.Result.RawOutput);
    }

    /// <summary>Signs out of both services and forgets everything cached for the tenant.</summary>
    public async Task<ConnectionOutcome> DisconnectAsync(CancellationToken cancellationToken)
    {
        if (TeamsSignInOpen)
        {
            return Waiting(TeamsSignInOpenMessage);
        }

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
        _identity.ClearIdentity();
        lock (_gate)
        {
            _pendingGraphSignIn = null;
        }

        return Ok("Disconnected from Microsoft Teams and Microsoft Graph.");
    }

    private const string TeamsSignInOpenMessage =
        "The Microsoft Teams sign-in window is open in the browser. Ask the user to complete the sign-in, " +
        "then call connect_teams again. No other tenant command can run until the sign-in is finished.";

    private async Task<bool> FinishedInTimeAsync(Task signIn, CancellationToken cancellationToken)
    {
        if (signIn.IsCompleted)
        {
            return true;
        }

        try
        {
            await signIn.WaitAsync(SignInWait, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up on this call; the sign-in itself stays open for the next call.
            return false;
        }
    }

    /// <summary>
    /// A sign-in that worked is still a failure for the caller when it reached another tenant or
    /// account than expected, or when the two connections are in different tenants.
    /// </summary>
    private async Task<ConnectionOutcome> WithIdentityAsync(string message, CancellationToken cancellationToken)
    {
        await RefreshIdentityAsync(cancellationToken);
        var problems = _identity.Problems;
        return problems.Count == 0
            ? Ok(message)
            : Fail(
                message + " But it is not the expected identity, so no change will be sent until this is corrected.",
                string.Join(" ", problems.Select(problem => problem.Message)));
    }

    private ConnectionOutcome Ok(string message) => new(true, message, null, GetStatus());

    private ConnectionOutcome Fail(string message, string? details) => new(false, message, details, GetStatus());

    private ConnectionOutcome Waiting(string message) => new(false, message, null, GetStatus(), WaitingForSignIn: true);
}
