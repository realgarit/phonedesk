using System.Text.Json;

namespace PhoneDesk.Operations;

/// <summary>Who the two connections are signed in as, read from the tenant after the sign-ins.</summary>
public sealed class TenantIdentityInfo
{
    public string TeamsTenantId { get; init; } = string.Empty;

    public string TeamsTenantName { get; init; } = string.Empty;

    /// <summary>The verified domains of the tenant Microsoft Teams is connected to.</summary>
    public IReadOnlyList<string> TeamsDomains { get; init; } = Array.Empty<string>();

    public string GraphTenantId { get; init; } = string.Empty;

    public string GraphAccount { get; init; } = string.Empty;

    public bool TeamsKnown => !string.IsNullOrWhiteSpace(TeamsTenantId);

    public bool GraphKnown => !string.IsNullOrWhiteSpace(GraphTenantId);
}

/// <summary>
/// What the operator expects to be connected to: a tenant (its ID, one of its domains, or its name)
/// and, optionally, the account of the Microsoft Graph sign-in.
/// </summary>
public sealed record TenantExpectation(string? Tenant, string? Account)
{
    public static TenantExpectation None { get; } = new(null, null);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Tenant) && string.IsNullOrWhiteSpace(Account);
}

/// <summary>
/// Reads and checks the identity of the two connections. Two things can be wrong after a sign-in
/// that reported success: Microsoft Teams and Microsoft Graph are connected to different tenants
/// (the browser kept the session of another customer), or the tenant is not the one the operator
/// meant. Both are found here, before anything is changed.
/// </summary>
public static class TenantIdentity
{
    public const string JsonMarker = "PDIDENTITY_JSON: ";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads the tenant of each connection. A connection that does not exist is left empty. The script only reads.</summary>
    public static string GetReadCommand() => ReadTemplate;

    public static TenantIdentityInfo Parse(string? output)
    {
        var lines = (output ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var json = lines.LastOrDefault(line => line.StartsWith(JsonMarker, StringComparison.Ordinal));
        if (json is null)
        {
            var error = lines.FirstOrDefault(line => line.StartsWith("ERROR:", StringComparison.Ordinal));
            throw new InvalidDataException(error ?? "The identity of the connections could not be read.");
        }

        return JsonSerializer.Deserialize<TenantIdentityInfo>(json[JsonMarker.Length..], Options) ?? new TenantIdentityInfo();
    }

    /// <summary>The reasons why nothing may be changed with these connections. Empty when all is as expected.</summary>
    public static IReadOnlyList<HolidayBlocker> Check(TenantIdentityInfo identity, TenantExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(expectation);
        var problems = new List<HolidayBlocker>();
        if (identity.TeamsKnown && identity.GraphKnown &&
            !string.Equals(identity.TeamsTenantId, identity.GraphTenantId, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new("tenantsDiffer",
                $"Microsoft Teams is connected to '{identity.TeamsTenantName}' ({identity.TeamsTenantId}), but Microsoft Graph is signed in to another tenant " +
                $"({identity.GraphTenantId}) as {identity.GraphAccount}. Sign out and sign in to both with an account of the same organization."));
        }

        var tenant = expectation.Tenant?.Trim();
        if (!string.IsNullOrEmpty(tenant))
        {
            if (identity.TeamsKnown && !MatchesTenant(identity, tenant))
            {
                problems.Add(new("unexpectedTenant",
                    $"Expected the tenant '{tenant}', but Microsoft Teams is connected to '{identity.TeamsTenantName}' ({identity.TeamsTenantId}). " +
                    "Sign out and sign in with an account of the expected tenant."));
            }
            else if (!identity.TeamsKnown && identity.GraphKnown && Guid.TryParse(tenant, out _) &&
                     !string.Equals(tenant, identity.GraphTenantId, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new("unexpectedTenant",
                    $"Expected the tenant '{tenant}', but Microsoft Graph is signed in to the tenant {identity.GraphTenantId} as {identity.GraphAccount}."));
            }
        }

        var account = expectation.Account?.Trim();
        if (!string.IsNullOrEmpty(account) && identity.GraphKnown && !string.IsNullOrWhiteSpace(identity.GraphAccount) &&
            !string.Equals(account, identity.GraphAccount.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new("unexpectedAccount",
                $"Expected the account '{account}', but Microsoft Graph is signed in as {identity.GraphAccount}."));
        }

        return problems;
    }

    private static bool MatchesTenant(TenantIdentityInfo identity, string expected)
        => string.Equals(expected, identity.TeamsTenantId, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(expected, identity.TeamsTenantName.Trim(), StringComparison.OrdinalIgnoreCase) ||
           identity.TeamsDomains.Any(domain => string.Equals(expected, domain.Trim(), StringComparison.OrdinalIgnoreCase));

    private const string ReadTemplate = """
        try {
            $pdTeamsId = ''
            $pdTeamsName = ''
            $pdDomains = @()
            try {
                $pdTenant = Get-CsTenant -ErrorAction Stop
                if ($pdTenant) {
                    $pdTeamsId = [string]$pdTenant.TenantId
                    $pdTeamsName = [string]$pdTenant.DisplayName
                    $pdDomains = @(@($pdTenant.VerifiedDomains) | Where-Object { $_ } | ForEach-Object { if ($_.Name) { [string]$_.Name } else { [string]$_ } })
                }
            }
            catch {
                $pdTeamsId = ''
            }
            $pdGraphId = ''
            $pdGraphAccount = ''
            try {
                $pdContext = Get-MgContext -ErrorAction Stop
                if ($pdContext) {
                    $pdGraphId = [string]$pdContext.TenantId
                    $pdGraphAccount = [string]$pdContext.Account
                }
            }
            catch {
                $pdGraphId = ''
            }
            $pdDocument = [ordered]@{
                teamsTenantId = $pdTeamsId
                teamsTenantName = $pdTeamsName
                teamsDomains = @($pdDomains)
                graphTenantId = $pdGraphId
                graphAccount = $pdGraphAccount
            }
            Write-Host ("PDIDENTITY_JSON: " + (ConvertTo-Json -InputObject $pdDocument -Depth 4 -Compress))
            Write-Host "SUCCESS: Connection identity retrieved"
        }
        catch {
            Write-Host "ERROR: Failed to read the identity of the connections: $_"
        }
        """;
}

/// <summary>
/// Holds what the operator expects and what the connections are, for one session. Every change
/// asks it first: with a problem nothing is sent to the tenant. Both heads register one instance.
/// </summary>
public sealed class TenantIdentityGuard
{
    private readonly Lock _gate = new();
    private TenantExpectation _expectation = TenantExpectation.None;
    private TenantIdentityInfo? _identity;

    public TenantExpectation Expectation
    {
        get
        {
            lock (_gate)
            {
                return _expectation;
            }
        }
    }

    /// <summary>The identity that was read last, or null when it was not read since the last sign-in.</summary>
    public TenantIdentityInfo? Identity
    {
        get
        {
            lock (_gate)
            {
                return _identity;
            }
        }
    }

    public IReadOnlyList<HolidayBlocker> Problems
    {
        get
        {
            lock (_gate)
            {
                return _identity is null ? Array.Empty<HolidayBlocker>() : TenantIdentity.Check(_identity, _expectation);
            }
        }
    }

    public void SetExpectation(string? tenant, string? account)
    {
        lock (_gate)
        {
            _expectation = new TenantExpectation(
                string.IsNullOrWhiteSpace(tenant) ? null : tenant.Trim(),
                string.IsNullOrWhiteSpace(account) ? null : account.Trim());
        }
    }

    public void Update(TenantIdentityInfo identity)
    {
        lock (_gate)
        {
            _identity = identity;
        }
    }

    /// <summary>Forgets the identity that was read, after a sign-out. The expectation stays until it is set again.</summary>
    public void ClearIdentity()
    {
        lock (_gate)
        {
            _identity = null;
        }
    }

    /// <summary>Throws when the connections are not what they must be.</summary>
    public void RequireMatch()
    {
        var problems = Problems;
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Nothing is sent to the tenant: " + string.Join(" ", problems.Select(problem => problem.Message)));
        }
    }
}
