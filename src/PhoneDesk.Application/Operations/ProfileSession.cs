using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Operations;

/// <summary>
/// Takes up the PowerShell session and the Graph sign-in of a profile. Each profile keeps its own
/// while the application runs, so a switch back finds its connections, and the connections of one
/// customer are never used for another. The state of the connections is read from the session
/// alone, never carried over from the profile before. Both heads switch through this method.
/// </summary>
public static class ProfileSession
{
    /// <param name="read">Runs a read-only script in the session in use and returns its output.</param>
    /// <param name="forgetTenantReads">Clears what was read from the tenant before (topology, health check).</param>
    public static async Task UseAsync(
        string profileKey,
        IPowerShellContextService? host,
        IMsalGraphAuthenticationService? graphAuthentication,
        ISessionManager session,
        TenantIdentityGuard identity,
        Func<string, Task<string?>> read,
        Action? forgetTenantReads)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(read);
        host?.UseSession(profileKey);
        graphAuthentication?.UseProfile(profileKey);

        var modulesChecked = session.ModulesChecked;
        session.ResetSession();
        session.UpdateModulesChecked(modulesChecked);
        forgetTenantReads?.Invoke();
        identity.ClearIdentity();

        TenantIdentityInfo info;
        try
        {
            info = TenantIdentity.Parse(await read(TenantIdentity.GetReadCommand()));
        }
        catch (InvalidDataException)
        {
            // The connections could not be read: both stay off, and the operator signs in.
            return;
        }

        if (info.TeamsKnown)
        {
            session.UpdateTeamsConnection(true);
            session.UpdateTenantInfo(info.TeamsTenantId, info.TeamsTenantName);
        }

        if (info.GraphKnown)
        {
            session.UpdateGraphConnection(true, info.GraphAccount);
        }

        if (info.TeamsKnown || info.GraphKnown)
        {
            identity.Update(info);
        }
    }
}
