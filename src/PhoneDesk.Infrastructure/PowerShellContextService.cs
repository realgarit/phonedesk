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
        private readonly Runspace _runspace;
        private readonly PowerShell _powerShell;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private bool _disposed = false;

        public PowerShellContextService(ILoggingService loggingService)
        {
            _loggingService = loggingService;

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

                // Assign to fields only after successful initialization
                _runspace = localRunspace;
                _powerShell = localPowerShell;

                InitializeRunspacePreferences();
                _loggingService.Log("PowerShell context service initialized with persistent runspace", LogLevel.Info);
            }
            catch
            {
                // Clean up locally created resources if initialization fails
                localPowerShell?.Dispose();
                localRunspace?.Dispose();
                throw;
            }
        }

        private void InitializeRunspacePreferences()
        {
            try
            {
                // Note: Set-ExecutionPolicy is intentionally NOT called here.
                // When hosting PowerShell via System.Management.Automation with our own Runspace,
                // execution policy does not apply to AddScript() calls - only to .ps1 file execution.
                // Calling Set-ExecutionPolicy Bypass is unnecessary and triggers AV heuristics
                // (MITRE ATT&CK T1059.001).

                _powerShell.Commands.Clear();
                _powerShell.AddCommand("Set-Variable")
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

            await _semaphore.WaitAsync(cancellationToken);
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
                    _powerShell.Commands.Clear();
                    _powerShell.Streams.ClearStreams();

                    var fullCommand = $@"
# Ensure we're in the right context
$ErrorActionPreference = 'Continue'
{command}
";

                    _powerShell.AddScript(fullCommand);

                    // Forward native PowerShell progress records (Write-Progress from cmdlets) to the caller.
                    // This only OBSERVES the existing Progress stream; it never alters the script text.
                    void OnProgressAdded(object? sender, DataAddedEventArgs e)
                    {
                        try
                        {
                            var record = _powerShell.Streams.Progress[e.Index];
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
                        _powerShell.Streams.Progress.DataAdded += OnProgressAdded;
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
                            }, _powerShell);

                            try
                            {
                                var invoked = _powerShell.Invoke();

                                // Depending on the host, Stop() either makes Invoke() throw
                                // PipelineStoppedException or return an (empty) collection with the
                                // invocation state set to Stopped. Handle the non-throwing case here.
                                if (cancellationToken.IsCancellationRequested ||
                                    _powerShell.InvocationStateInfo.State == PSInvocationState.Stopped)
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
                            _powerShell.Streams.Progress.DataAdded -= OnProgressAdded;
                        }
                    }

                    var output = new StringBuilder();
                    var errorInfos = new List<PowerShellErrorInfo>();

                    foreach (var item in _powerShell.Streams.Information)
                    {
                        output.AppendLine(item.ToString());
                    }

                    // Capture Warning stream (device code from Connect-MgGraph -UseDeviceCode is output here)
                    foreach (var item in _powerShell.Streams.Warning)
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

                    var hadErrors = _powerShell.HadErrors;
                    if (hadErrors)
                    {
                        foreach (var error in _powerShell.Streams.Error)
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
                _semaphore.Release();
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
            // Use timeout to prevent indefinite blocking
            if (!await _semaphore.WaitAsync(TimeSpan.FromSeconds(ConstantsService.PowerShell.ConnectionCheckTimeoutSeconds), cancellationToken))
            {
                _loggingService.Log("IsConnectedAsync timed out waiting for semaphore", LogLevel.Warning);
                return false;
            }
            
            try
            {
                _powerShell.Commands.Clear();
                _powerShell.Streams.ClearStreams();

                switch (service.ToLowerInvariant())
                {
                    case "teams":
                        // Use command-based execution instead of script for better security tool compatibility
                        _powerShell.AddCommand("Get-CsTenant")
                            .AddParameter("ErrorAction", "SilentlyContinue");
                        break;
                    case "graph":
                        // Use command-based execution instead of script for better security tool compatibility
                        _powerShell.AddCommand("Get-MgContext")
                            .AddParameter("ErrorAction", "SilentlyContinue");
                        break;
                    default:
                        return false;
                }

                var result = await Task.Run(() => _powerShell.Invoke(), cancellationToken);
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
                _semaphore.Release();
            }
        }

        public async Task<string> GetConnectionStatusAsync(CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                _powerShell.Commands.Clear();
                _powerShell.Streams.ClearStreams();

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

                _powerShell.AddScript(statusCommand);
                var result = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return _powerShell.Invoke();
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
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _semaphore.Dispose();
                _powerShell?.Dispose();
                _runspace?.Dispose();
                _disposed = true;
                _loggingService.Log("PowerShell context service disposed", LogLevel.Info);
            }
        }
    }
}
