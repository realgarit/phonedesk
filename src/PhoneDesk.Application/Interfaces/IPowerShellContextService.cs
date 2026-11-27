namespace PhoneDesk.Services.Interfaces;

/// <summary>
/// Service for managing PowerShell context and executing commands.
/// </summary>
public interface IPowerShellContextService : IDisposable
{
    Task<string> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default);
    Task<string> ExecuteCommandAsync(string command, Dictionary<string, string>? environmentVariables, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a command and returns both the flattened text output and the structured error records
    /// captured from the PowerShell error stream. The text output is byte-identical to what
    /// <see cref="ExecuteCommandAsync(string, Dictionary{string, string}?, CancellationToken)"/> returns;
    /// this overload additionally surfaces the raw <c>ErrorRecord</c> details for typed result mapping.
    ///
    /// When <paramref name="progress"/> is supplied, native PowerShell progress records (emitted by cmdlets
    /// via <c>Write-Progress</c>) are forwarded to it while the command runs. Cancelling
    /// <paramref name="cancellationToken"/> stops the running pipeline cooperatively and leaves the
    /// persistent runspace open and reusable for the next command.
    /// </summary>
    Task<PowerShellExecutionResult> ExecuteCommandWithDetailsAsync(string command, Dictionary<string, string>? environmentVariables, IProgress<PowerShellProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<bool> IsConnectedAsync(string service, CancellationToken cancellationToken = default);
    Task<string> GetConnectionStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>The key of the PowerShell session commands run in now. Empty for the session the application starts with.</summary>
    string SessionKey => string.Empty;

    /// <summary>
    /// Makes the session of a key the one commands run in, and creates it when it does not exist.
    /// Each customer profile has its own session, so its sign-ins are kept while another customer is worked on.
    /// </summary>
    void UseSession(string key)
    {
    }

    /// <summary>
    /// Gives up the active session and starts a new one in its place: the way out of a command that
    /// does not return, such as a Microsoft Teams sign-in that hangs. Both connections of the session
    /// are lost. Returns false when the host cannot do this.
    /// </summary>
    bool AbandonSession() => false;

    /// <summary>Releases the session of a key that is not the active one.</summary>
    void CloseSession(string key)
    {
    }
}
