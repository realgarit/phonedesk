namespace PhoneDesk.Services.Interfaces;

/// <summary>
/// Port describing how the running application was installed.
/// </summary>
public interface IInstallationContext
{
    /// <summary>
    /// True when the process runs with MSIX package identity (Microsoft Store install).
    /// Updates are then delivered by the Store, so the in-app GitHub updater must stay off.
    /// </summary>
    bool IsPackaged { get; }
}

/// <summary>
/// Update check for Store-managed installs: the Store delivers updates, so the in-app
/// updater never reports a release. Never performs network access.
/// </summary>
public sealed class StoreManagedUpdateCheckService : IUpdateCheckService
{
    public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<UpdateInfo?>(null);
}
