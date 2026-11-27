using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Threading;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services
{
    public class PowerShellContextService : IPowerShellContextService
    {
        private readonly ILoggingService _loggingService;
        private readonly Lock _sessionGate = new();
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private Session _active;
        private string _activeKey = string.Empty;
        private bool _disposed = false;

        /// <summary>
        /// One PowerShell session: a runspace with its own connections to Microsoft Teams and
        /// Microsoft Graph, and the lock that lets one command run in it at a time. A command works
        /// with the session that was active when it started, also when another session becomes
        /// active while it runs.
        /// </summary>
        private sealed class Session
        {
            public Session(Runspace runspace, PowerShell powerShell)
            {
                Runspace = runspace;
                PowerShell = powerShell;
            }

            public Runspace Runspace { get; }

            public PowerShell PowerShell { get; }

            public SemaphoreSlim Semaphore { get; } = new(1, 1);
        }

        public PowerShellContextService(ILoggingService loggingService)
        {
            _loggingService = loggingService;
            _active = CreateSession();
            _sessions[_activeKey] = _active;
            _loggingService.Log("PowerShell context service initialized with persistent runspace", LogLevel.Info);
        }

        /// <summary>The key of the session commands run in now. Empty for the session the application starts with.</summary>
        public string SessionKey
        {
            get
            {
                lock (_sessionGate)
                {
                    return _activeKey;
                }
            }
        }

        /// <summary>
        /// Makes the session of a key the one commands run in, and creates it when it does not exist.
        /// A customer profile has its own session, so its sign-ins stay while the operator works for
        /// another customer and are there again after a switch back. Nothing is written to disk: a
        /// session lives as long as the application runs.
        /// </summary>
        public void UseSession(string key)
        {
            var name = (key ?? string.Empty).Trim();
            lock (_sessionGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_sessions.TryGetValue(name, out var session))
                {
                    session = CreateSession();
                    _sessions[name] = session;
                    _loggingService.Log("PowerShell session created for another profile", LogLevel.Info);
                }

                _active = session;
                _activeKey = name;
            }
        }

        /// <summary>
        /// Gives up the active session and starts a new one in its place. This is the way out of a
        /// command that does not return, such as a Microsoft Teams sign-in whose browser window was
        /// closed: stopping the pipeline does not interrupt that command. The old session is stopped
        /// and released in the background; whatever it reports later reaches nobody. Its connections
        /// are lost, so both services need a new sign-in.
        /// </summary>
        public bool AbandonSession()
        {
            Session old;
            lock (_sessionGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                old = _active;
                _active = CreateSession();
                _sessions[_activeKey] = _active;
            }

            _loggingService.Log("PowerShell session abandoned; a new session was started", LogLevel.Warning);
            ReleaseInBackground(old);
            return true;
        }

        /// <summary>Releases the session of a key that is not the active one, for example of a profile that was removed.</summary>
        public void CloseSession(string key)
        {
            var name = (key ?? string.Empty).Trim();
            Session? closed = null;
            lock (_sessionGate)
            {
                if (!string.Equals(name, _activeKey, StringComparison.OrdinalIgnoreCase) && _sessions.Remove(name, out var session))
                {
                    closed = session;
                }
            }

            if (closed is not null)
            {
                ReleaseInBackground(closed);
            }
        }

        private static void ReleaseInBackground(Session session)
            => _ = Task.Run(() =>
            {
                // Stop and Dispose can wait for a command that does not return; that must not hold up the caller.
                try { session.PowerShell.Stop(); } catch { /* the pipeline may have finished */ }
                try { session.PowerShell.Dispose(); } catch { /* best effort */ }
                try { session.Runspace.Dispose(); } catch { /* best effort */ }
            });

        private Session CreateSession()
        {
            // Use local variables during construction to ensure proper cleanup on failure
            Runspace? localRunspace = null;
            PowerShell? localPowerShell = null;

            try
            {
                // Set execution policy via InitialSessionState (configuration-level, not a runtime command).
                // This is required because Import-Module loads .psm1 files which are subject to execution policy.
                // Setting it here instead of calling Set-ExecutionPolicy at runtime avoids triggering
                // AV behavioral heuristics (Kaspersky flags runtime Set-ExecutionPolicy Bypass as MITRE T1059.001).
                var initialState = InitialSessionState.CreateDefault();
                // ExecutionPolicy is Windows-only; setting it on macOS/Linux throws
                // PlatformNotSupportedException in PowerShell SDK 7.6+.
                if (OperatingSystem.IsWindows())
                {
                    initialState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
                }
                localRunspace = RunspaceFactory.CreateRunspace(initialState);
                localRunspace.Open();

                localPowerShell = PowerShell.Create();
                localPowerShell.Runspace = localRunspace;

                var session = new Session(localRunspace, localPowerShell);
                InitializeRunspacePreferences(session);
                return session;
            }
            catch
            {
                // Clean up locally created resources if initialization fails
                localPowerShell?.Dispose();
                localRunspace?.Dispose();
                throw;
            }
        }

        private void InitializeRunspacePreferences(Session session)
        {
            try
            {
                // Note: Set-ExecutionPolicy is intentionally NOT called here.
                // When hosting PowerShell via System.Management.Automation with our own Runspace,
                // execution policy does not apply to AddScript() calls - only to .ps1 file execution.
                // Calling Set-ExecutionPolicy Bypass is unnecessary and triggers AV heuristics
                // (MITRE ATT&CK T1059.001).

                session.PowerShell.Commands.Clear();
                session.PowerShell.AddCommand("Set-Variable")
                    .AddParameter("Name", "InformationPreference")
                    .AddParameter("Value", "Continue")
                    .Invoke();

                // WAM bypass flags are already set at process level in Program.cs Main().
                // No need to set them again in the runspace.
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Error initializing PowerShell preferences: {ex.Message}", LogLevel.Warning);
            }
        }

        private Session ActiveSession()
        {
            lock (_sessionGate)
            {
                return _active;
            }
        }

        public async Task<string> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
        {
            return await ExecuteCommandAsync(command, null, cancellationToken);
        }

        public async Task<string> ExecuteCommandAsync(string command, Dictionary<string, string>? environmentVariables, CancellationToken cancellationToken = default)
        {
            // Delegate to the detailed overload and return only the text output, which is byte-identical
            // to the historic behavior. The structured error records are simply ignored here.
            var execution = await ExecuteCommandWithDetailsAsync(command, environmentVariables, null, cancellationToken);
            return execution.Output;
        }

        public async Task<PowerShellExecutionResult> ExecuteCommandWithDetailsAsync(string command, Dictionary<string, string>? environmentVariables, IProgress<PowerShellProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PowerShellContextService));

            var session = ActiveSession();
            await session.Semaphore.WaitAsync(cancellationToken);
            try
            {
                // Set environment variables for the PowerShell script to consume via $env:VAR_NAME.
                // These are set at process level because runspace session variables don't map to $env: scope.
                // Cleared in the finally block immediately after execution to minimize exposure window.
                var varsToClear = new List<string>();
                if (environmentVariables != null)
                {
                    foreach (var kvp in environmentVariables)
                    {
                        Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
                        varsToClear.Add(kvp.Key);
                    }
                }

                try
                {
                    session.PowerShell.Commands.Clear();
                    session.PowerShell.Streams.ClearStreams();

                    var fullCommand = $@"
# Ensure we're in the right context
$ErrorActionPreference = 'Continue'
{command}
";

                    session.PowerShell.AddScript(fullCommand);

                    // Forward native PowerShell progress records (Write-Progress from cmdlets) to the caller.
                    // This only OBSERVES the existing Progress stream; it never alters the script text.
                    void OnProgressAdded(object? sender, DataAddedEventArgs e)
                    {
                        try
                        {
                            var record = session.PowerShell.Streams.Progress[e.Index];
                            progress?.Report(new PowerShellProgress
                            {
                                Activity = record.Activity ?? string.Empty,
                                StatusDescription = record.StatusDescription ?? string.Empty,
                                CurrentOperation = record.CurrentOperation ?? string.Empty,
                                // A Completed record means the activity is done; treat it as 100%.
                                PercentComplete = record.RecordType == ProgressRecordType.Completed
                                    ? 100
                                    : record.PercentComplete
                            });
                        }
                        catch
                        {
                            // Progress reporting is best-effort and must never break command execution.
                        }
                    }

                    if (progress != null)
                    {
                        session.PowerShell.Streams.Progress.DataAdded += OnProgressAdded;
                    }

                    System.Collections.ObjectModel.Collection<PSObject> result;
                    try
                    {
                        result = await Task.Run(() =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            // Wire cooperative cancellation to the running pipeline. Stop() interrupts an
                            // in-flight Invoke() (which then throws PipelineStoppedException) but leaves the
                            // runspace in the Opened state, so the very next command reuses it unchanged.
                            using var cancelRegistration = cancellationToken.Register(static state =>
                            {
                                try { ((PowerShell)state!).Stop(); }
                                catch { /* pipeline may have already finished */ }
                            }, session.PowerShell);

                            try
                            {
                                var invoked = session.PowerShell.Invoke();

                                // Depending on the host, Stop() either makes Invoke() throw
                                // PipelineStoppedException or return an (empty) collection with the
                                // invocation state set to Stopped. Handle the non-throwing case here.
                                if (cancellationToken.IsCancellationRequested ||
                                    session.PowerShell.InvocationStateInfo.State == PSInvocationState.Stopped)
                                {
                                    throw new OperationCanceledException(cancellationToken);
                                }

                                return invoked;
                            }
                            catch (PipelineStoppedException)
                            {
                                // The pipeline was stopped by our cancellation registration above.
                                throw new OperationCanceledException(cancellationToken);
                            }
                        }, cancellationToken);
                    }
                    finally
                    {
                        if (progress != null)
                        {
                            session.PowerShell.Streams.Progress.DataAdded -= OnProgressAdded;
                        }
                    }

                    var output = new StringBuilder();
                    var errorInfos = new List<PowerShellErrorInfo>();

                    foreach (var item in session.PowerShell.Streams.Information)
                    {
                        output.AppendLine(item.ToString());
                    }

                    // Capture Warning stream (device code from Connect-MgGraph -UseDeviceCode is output here)
                    foreach (var item in session.PowerShell.Streams.Warning)
                    {
                        _loggingService.Log($"{item}", LogLevel.Warning);
                        output.AppendLine($"WARNING: {item}");
                    }

                    foreach (var item in result)
                    {
                        // A cmdlet can write a null value to the pipeline; it carries no text.
                        if (item is not null)
                        {
                            output.AppendLine(item.ToString());
                        }
                    }

                    var hadErrors = session.PowerShell.HadErrors;
                    if (hadErrors)
                    {
                        foreach (var error in session.PowerShell.Streams.Error)
                        {
                            _loggingService.Log($"PowerShell error: {error}", LogLevel.Error);
                            output.AppendLine($"ERROR: {error}");
                            errorInfos.Add(ToErrorInfo(error));
                        }
                    }

                    return new PowerShellExecutionResult
                    {
                        Output = output.ToString(),
                        HadErrors = hadErrors,
                        Errors = errorInfos
                    };
                }
                finally
                {
                    // SECURITY: Clear sensitive environment variables immediately after execution
                    foreach (var varName in varsToClear)
                    {
                        try
                        {
                            Environment.SetEnvironmentVariable(varName, null);
                        }
                        catch
                        {
                            // Ignore errors when clearing variables
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cooperative cancellation is not a failure: surface it to the caller (which shows a
                // "cancelled" status rather than an error dialog). Environment variables were already
                // cleared by the inner finally, and the semaphore is released by the outer finally.
                _loggingService.Log("PowerShell command cancelled by user", LogLevel.Info);
                throw;
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Error executing PowerShell command: {ex.Message}", LogLevel.Error);
                return new PowerShellExecutionResult
                {
                    Output = $"ERROR: {ex.Message}",
                    HadErrors = true,
                    Errors = new List<PowerShellErrorInfo>
                    {
                        new()
                        {
                            ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                            Message = ex.Message,
                            FailingCommand = string.Empty,
                            CategoryInfo = string.Empty,
                            RawText = ex.ToString()
                        }
                    }
                };
            }
            finally
            {
                session.Semaphore.Release();
            }
        }

        /// <summary>
        /// Projects a PowerShell <see cref="ErrorRecord"/> into a framework-free <see cref="PowerShellErrorInfo"/>,
        /// preserving the exception type, message and failing command rather than flattening to a single string.
        /// </summary>
        private static PowerShellErrorInfo ToErrorInfo(ErrorRecord error)
        {
            var exception = error.Exception;
            var invocation = error.InvocationInfo;
            var failingCommand = invocation?.MyCommand?.Name;
            if (string.IsNullOrEmpty(failingCommand))
            {
                failingCommand = invocation?.Line?.Trim();
            }

            return new PowerShellErrorInfo
            {
                ExceptionType = exception?.GetType().FullName ?? exception?.GetType().Name ?? string.Empty,
                Message = exception?.Message ?? error.ToString(),
                FailingCommand = failingCommand ?? string.Empty,
                CategoryInfo = error.CategoryInfo?.ToString() ?? string.Empty,
                RawText = error.ToString()
            };
        }

        public async Task<bool> IsConnectedAsync(string service, CancellationToken cancellationToken = default)
        {
            var session = ActiveSession();
            // Use timeout to prevent indefinite blocking
            if (!await session.Semaphore.WaitAsync(TimeSpan.FromSeconds(ConstantsService.PowerShell.ConnectionCheckTimeoutSeconds), cancellationToken))
            {
                _loggingService.Log("IsConnectedAsync timed out waiting for semaphore", LogLevel.Warning);
                return false;
            }
            
            try
            {
                session.PowerShell.Commands.Clear();
                session.PowerShell.Streams.ClearStreams();

                switch (service.ToLowerInvariant())
                {
                    case "teams":
                        // Use command-based execution instead of script for better security tool compatibility
                        session.PowerShell.AddCommand("Get-CsTenant")
                            .AddParameter("ErrorAction", "SilentlyContinue");
                        break;
                    case "graph":
                        // Use command-based execution instead of script for better security tool compatibility
                        session.PowerShell.AddCommand("Get-MgContext")
                            .AddParameter("ErrorAction", "SilentlyContinue");
                        break;
                    default:
                        return false;
                }

                var result = await Task.Run(() => session.PowerShell.Invoke(), cancellationToken);
                return result != null && result.Count > 0;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _loggingService.Log($"Error checking {service} connection: {ex.Message}", LogLevel.Warning);
                return false;
            }
            finally
            {
                session.Semaphore.Release();
            }
        }

        public async Task<string> GetConnectionStatusAsync(CancellationToken cancellationToken = default)
        {
            var session = ActiveSession();
            await session.Semaphore.WaitAsync(cancellationToken);
            try
            {
                session.PowerShell.Commands.Clear();
                session.PowerShell.Streams.ClearStreams();

                var statusCommand = @"
$status = @()
try {
    $teamsContext = Get-CsTenant -ErrorAction SilentlyContinue
    if ($teamsContext) {
        $status += ""Teams: Connected to $($teamsContext.DisplayName)""
    } else {
        $status += ""Teams: Not connected""
    }
} catch {
    $status += ""Teams: Error checking connection""
}

try {
    $graphContext = Get-MgContext -ErrorAction SilentlyContinue
    if ($graphContext) {
        $status += ""Graph: Connected as $($graphContext.Account)""
    } else {
        $status += ""Graph: Not connected""
    }
} catch {
    $status += ""Graph: Error checking connection""
}

$status -join ""`n""
";

                session.PowerShell.AddScript(statusCommand);
                var result = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return session.PowerShell.Invoke();
                }, cancellationToken);

                var output = new StringBuilder();
                foreach (var item in result)
                {
                    if (item is not null)
                    {
                        output.AppendLine(item.ToString());
                    }
                }

                return output.ToString();
            }
            catch (Exception ex)
            {
                return $"Error checking connection status: {ex.Message}";
            }
            finally
            {
                session.Semaphore.Release();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                List<Session> sessions;
                lock (_sessionGate)
                {
                    _disposed = true;
                    sessions = _sessions.Values.ToList();
                    _sessions.Clear();
                }

                foreach (var session in sessions)
                {
                    session.Semaphore.Dispose();
                    session.PowerShell.Dispose();
                    session.Runspace.Dispose();
                }

                _loggingService.Log("PowerShell context service disposed", LogLevel.Info);
            }
        }
    }
}
