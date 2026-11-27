using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;

namespace PhoneDesk.Mcp.Tools;

/// <summary>
/// Customers as named profiles. The desktop app has the same on its Profiles page; both use
/// <see cref="TenantProfileService"/>. Each head keeps its own profiles.
/// </summary>
[McpServerToolType]
internal sealed class ProfileTools
{
    private const string ProfileDescription = "The name of the profile, as list_profiles shows it.";

    private readonly TenantProfileService _profiles;
    private readonly ConfigurationStore _store;
    private readonly TenantConnectionService _connection;
    private readonly GuidedSetupProgress _progress;
    private readonly McpRunJournal _runs;

    public ProfileTools(
        TenantProfileService profiles, ConfigurationStore store, TenantConnectionService connection, GuidedSetupProgress progress, McpRunJournal runs)
    {
        _profiles = profiles;
        _store = store;
        _connection = connection;
        _progress = progress;
        _runs = runs;
    }

    [McpServerTool(Name = "list_profiles", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the customer profiles kept on this computer and names the one in use. A profile is one customer: the organization the sign-ins must reach (domain, name or tenant ID), the sign-in account, and the configuration of that customer. Use it at the start of a session when the user works for several customers, and ask which customer the work is for. A profile holds no password and no token. Reads this computer only.")]
    public string List() => McpJson.Run(() =>
    {
        var active = _profiles.Active;
        return new
        {
            activeProfile = active?.Name,
            profiles = _profiles.List().Select(profile => View(profile, profile.Id == active?.Id))
        };
    });

    [McpServerTool(Name = "save_profile", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Saves the current configuration as a customer profile. With a new name it creates the profile; with the name of an existing profile it updates that profile's expected organization and account, and its configuration when updateConfiguration is true. It does not switch to the profile; use switch_profile for that. Local only; the tenant is not touched. Ask the user for the name and the organization; never invent them.")]
    public string Save(
        [Description("The name of the profile, for example 'Contoso production'.")] string name,
        [Description("The organization the sign-ins must reach: a verified domain, the tenant name or the tenant ID. Empty to leave it unchecked.")] string? expectedTenant = null,
        [Description("The sign-in name the operator uses for this customer. Empty to leave it unchecked.")] string? expectedAccount = null,
        [Description("For an existing profile: true to also replace its saved configuration with the current one.")] bool updateConfiguration = false)
        => McpJson.Run(() =>
        {
            var existing = _profiles.Find(name);
            if (existing is null)
            {
                var created = _profiles.Create(name, expectedTenant, expectedAccount, _store.ToJson());
                return new { success = true, created = true, profile = View(created, false), note = "The profile is not in use yet. Use switch_profile to work with it." };
            }

            var updated = _profiles.Update(existing.Id, existing.Name, expectedTenant ?? existing.ExpectedTenant, expectedAccount ?? existing.ExpectedAccount);
            if (updateConfiguration)
            {
                _profiles.SaveConfiguration(updated.Id, _store.ToJson());
            }

            var isActive = _profiles.Active?.Id == updated.Id;
            if (isActive)
            {
                _connection.Identity.SetExpectation(updated.ExpectedTenant, updated.ExpectedAccount);
            }

            return new { success = true, created = false, configurationReplaced = updateConfiguration, profile = View(updated, isActive) };
        });

    [McpServerTool(Name = "switch_profile", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Switches to another customer profile. It saves the current configuration into the profile that was in use, loads the configuration of the named profile, sets the organization and account the next sign-ins must reach, clears the Guided Setup progress, and signs out of Microsoft Teams and Microsoft Graph. After it, sign in again with connect_teams and connect_graph: a sign-in that reaches another organization than the profile expects blocks every read and change. Tell the user that they are signed out. The tenant of either customer is not changed.")]
    public Task<string> Switch(
        [Description(ProfileDescription)] string profile,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var target = _profiles.Find(profile)
                ?? throw new ArgumentException($"There is no profile '{profile}'. Use list_profiles.");
            if (_connection.TeamsSignInOpen)
            {
                throw new InvalidOperationException("A Microsoft Teams sign-in is still open. Finish it in the browser first, then switch the profile.");
            }

            var current = _store.ToJson();
            // The configuration of the profile is put in place before the switch is written: one that
            // cannot be read stops here, and the profile in use stays as it is.
            if (!string.IsNullOrWhiteSpace(target.ConfigurationJson))
            {
                _store.Replace(target.ConfigurationJson);
            }
            else
            {
                _store.Reset();
            }

            var active = _profiles.Activate(target.Id, current);
            _connection.Identity.SetExpectation(active.ExpectedTenant, active.ExpectedAccount);
            _progress.Reset();
            _runs.Reset();
            var disconnect = await _connection.DisconnectAsync(cancellationToken);
            return new
            {
                success = true,
                activeProfile = View(active, true),
                signedOut = disconnect.Success,
                next = "Sign in again with connect_teams and connect_graph. The sign-ins are checked against the expected organization of this profile.",
                expectedTenant = active.ExpectedTenant.Length == 0 ? null : active.ExpectedTenant,
                expectedAccount = active.ExpectedAccount.Length == 0 ? null : active.ExpectedAccount
            };
        });

    [McpServerTool(Name = "remove_profile", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Removes a customer profile and its saved configuration from this computer. The tenant of the customer is not touched, and the configuration that is in place now stays. Ask the user before you call it.")]
    public string Delete([Description(ProfileDescription)] string profile) => McpJson.Run(() =>
    {
        var target = _profiles.Find(profile) ?? throw new ArgumentException($"There is no profile '{profile}'. Use list_profiles.");
        _profiles.Delete(target.Id);
        return new { success = true, deleted = target.Name, activeProfile = _profiles.Active?.Name };
    });

    private static object View(TenantProfile profile, bool active) => new
    {
        name = profile.Name,
        inUse = active,
        expectedTenant = profile.ExpectedTenant.Length == 0 ? null : profile.ExpectedTenant,
        expectedAccount = profile.ExpectedAccount.Length == 0 ? null : profile.ExpectedAccount,
        hasConfiguration = profile.ConfigurationJson.Length > 0,
        updatedUtc = profile.UpdatedUtc
    };
}
