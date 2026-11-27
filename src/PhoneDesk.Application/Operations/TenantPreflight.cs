using System.Text;
using System.Text.Json;
using PhoneDesk.Models;
using PhoneDesk.Planning;

namespace PhoneDesk.Operations;

/// <summary>What the tenant holds for one entry of a dry-run plan. A null value was not read.</summary>
public sealed class PreflightTenantRow
{
    public int Row { get; init; }

    /// <summary>The number of groups with the name of the agent group.</summary>
    public int? Groups { get; init; }

    public int? CallQueues { get; init; }

    public int? AutoAttendants { get; init; }

    public PreflightAccount? CallQueueAccount { get; init; }

    public PreflightAccount? AutoAttendantAccount { get; init; }

    public PreflightNumber? Number { get; init; }

    /// <summary>What could not be read, as the tenant reported it.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public sealed class PreflightAccount
{
    public bool Exists { get; init; }

    public string Id { get; init; } = string.Empty;

    /// <summary>True when the account already has the license of the plan.</summary>
    public bool HasLicense { get; init; }
}

public sealed class PreflightNumber
{
    public bool InInventory { get; init; }

    public string Type { get; init; } = string.Empty;

    public string AssignedToId { get; init; } = string.Empty;

    /// <summary>The sign-in name of the account that has the number, when it could be read.</summary>
    public string AssignedTo { get; init; } = string.Empty;
}

/// <summary>The licenses of one product the tenant has. Null when the tenant has no such subscription.</summary>
public sealed class PreflightLicense
{
    public string SkuId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public int Available { get; init; }
}

public sealed class PreflightTenantState
{
    public IReadOnlyList<PreflightTenantRow> Rows { get; init; } = Array.Empty<PreflightTenantRow>();

    public IReadOnlyList<PreflightLicense> Licenses { get; init; } = Array.Empty<PreflightLicense>();

    /// <summary>False when the licenses of the tenant could not be read.</summary>
    public bool LicensesRead { get; init; }

    public string LicenseError { get; init; } = string.Empty;
}

/// <summary>
/// The live part of the dry-run preview: reads what the tenant already holds for the names, sign-in
/// names, license and phone number of a plan, and turns it into preflight checks. The script only
/// reads. Both heads use it: the Guided Setup and bulk previews of the desktop app and of the MCP
/// server.
/// </summary>
public static class TenantPreflight
{
    public const string JsonMarker = "PDPREFLIGHT_JSON: ";

    /// <summary>The name of the check the plan carries until the tenant was read.</summary>
    public const string NotCheckedName = "Live tenant collision / license / number availability";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The read script for the entries of a plan. All values travel as one Base64 text, so a name
    /// can hold any character without becoming part of the script.
    /// </summary>
    /// <param name="entries">The entries of the plan, in row order.</param>
    /// <param name="effectiveName">
    /// How the provisioning scripts write a name or sign-in name. The tenant is searched for that
    /// text: a name with an apostrophe, for example, is created with two.
    /// </param>
    public static string GetReadCommand(IReadOnlyList<IPhoneManagerVariables> entries, Func<string, string>? effectiveName = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string Name(string? value) => string.IsNullOrEmpty(value) ? string.Empty : effectiveName is null ? value : effectiveName(value);
        var request = new
        {
            entries = entries.Select((entry, index) => new
            {
                row = index + 1,
                group = Name(entry.M365Group),
                callQueue = Name(entry.CqDisplayName),
                autoAttendant = Name(entry.AaDisplayName),
                callQueueAccount = Name(entry.RacqUPN),
                autoAttendantAccount = Name(entry.RaaaUPN),
                skuId = entry.SkuId ?? string.Empty,
                number = (entry.RaaAnr ?? string.Empty).Trim()
            }).ToList()
        };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
        return ReadTemplate.Replace("__REQUEST__", encoded, StringComparison.Ordinal);
    }

    public static PreflightTenantState Parse(string? output)
    {
        var lines = (output ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var json = lines.LastOrDefault(line => line.StartsWith(JsonMarker, StringComparison.Ordinal));
        if (json is null)
        {
            var error = lines.FirstOrDefault(line => line.StartsWith("ERROR:", StringComparison.Ordinal));
            throw new InvalidDataException(error ?? "The tenant could not be read for the preview.");
        }

        return JsonSerializer.Deserialize<PreflightTenantState>(json[JsonMarker.Length..], Options) ?? new PreflightTenantState();
    }

    /// <summary>
    /// Replaces the "not checked" entry of each plan entry with the checks against the tenant. The
    /// setup reuses objects that exist, so an existing object is a warning; a check fails only when
    /// the run would fail: no license left, or a number another account has.
    /// </summary>
    public static DryRunPlan Apply(DryRunPlan plan, IReadOnlyList<IPhoneManagerVariables> sources, PreflightTenantState state)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(state);
        var licensesUsed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<DryRunEntry>(plan.Entries.Count);
        foreach (var entry in plan.Entries)
        {
            var source = entry.RowNumber >= 1 && entry.RowNumber <= sources.Count ? sources[entry.RowNumber - 1] : null;
            var row = state.Rows.FirstOrDefault(candidate => candidate.Row == entry.RowNumber);
            var checks = entry.PreflightChecks.Where(check => check.Name != NotCheckedName).ToList();
            if (source is null || row is null)
            {
                checks.Add(new PreflightCheck(NotCheckedName, PreflightStatus.NotChecked, "The tenant returned nothing for this entry."));
            }
            else
            {
                checks.Add(Existing("Agent group in the tenant", "group", source.M365Group, row.Groups));
                checks.Add(Existing("Call queue in the tenant", "call queue", source.CqDisplayName, row.CallQueues));
                checks.Add(Existing("Auto attendant in the tenant", "auto attendant", source.AaDisplayName, row.AutoAttendants));
                checks.Add(Account("Call queue resource account in the tenant", source.RacqUPN, row.CallQueueAccount));
                checks.Add(Account("Auto attendant resource account in the tenant", source.RaaaUPN, row.AutoAttendantAccount));
                checks.Add(License(source, row, state, licensesUsed));
                if (!string.IsNullOrWhiteSpace(source.RaaAnr))
                {
                    checks.Add(Number(source, row));
                }

                checks.AddRange(row.Errors.Select(error => new PreflightCheck("Tenant read", PreflightStatus.NotChecked, error)));
            }

            entries.Add(entry with { PreflightChecks = checks });
        }

        return plan with { Entries = entries };
    }

    private static PreflightCheck Existing(string name, string what, string value, int? count)
        => count switch
        {
            null => new PreflightCheck(name, PreflightStatus.NotChecked, $"The tenant could not be read for the {what} '{value}'."),
            0 => new PreflightCheck(name, PreflightStatus.Pass, $"The tenant has no {what} named '{value}'. It is created."),
            1 => new PreflightCheck(name, PreflightStatus.Warning, $"The tenant already has a {what} named '{value}'. The setup uses it and does not create a second one."),
            _ => new PreflightCheck(name, PreflightStatus.Warning, $"The tenant has {count} objects of the kind {what} named '{value}'. The setup uses the first one it finds; make the name unique first.")
        };

    private static PreflightCheck Account(string name, string upn, PreflightAccount? account)
        => account switch
        {
            null => new PreflightCheck(name, PreflightStatus.NotChecked, $"The tenant could not be read for the account '{upn}'."),
            { Exists: false } => new PreflightCheck(name, PreflightStatus.Pass, $"The tenant has no account '{upn}'. It is created."),
            _ => new PreflightCheck(name, PreflightStatus.Warning, $"The account '{upn}' already exists. The setup uses it and does not create a second one.")
        };

    private static PreflightCheck License(
        IPhoneManagerVariables source, PreflightTenantRow row, PreflightTenantState state, Dictionary<string, int> used)
    {
        const string name = "License available";
        if (!state.LicensesRead)
        {
            return new PreflightCheck(name, PreflightStatus.NotChecked,
                "The licenses of the tenant could not be read" + (state.LicenseError.Length == 0 ? "." : ": " + state.LicenseError));
        }

        var sku = (source.SkuId ?? string.Empty).Trim();
        var license = state.Licenses.FirstOrDefault(candidate => string.Equals(candidate.SkuId, sku, StringComparison.OrdinalIgnoreCase));
        if (license is null)
        {
            return new PreflightCheck(name, PreflightStatus.Fail,
                $"The tenant has no subscription with the license '{sku}'. The license step would fail.");
        }

        if (row.CallQueueAccount is null || row.AutoAttendantAccount is null)
        {
            return new PreflightCheck(name, PreflightStatus.NotChecked,
                $"The tenant has {license.Available} free license(s) of {Label(license)}, but the accounts could not be read, so the number needed is not known.");
        }

        // An account that has the license already needs none.
        var needed = new[] { row.CallQueueAccount, row.AutoAttendantAccount }.Count(account => !account.HasLicense);
        used.TryGetValue(sku, out var before);
        used[sku] = before + needed;
        var left = license.Available - before;
        if (needed == 0)
        {
            return new PreflightCheck(name, PreflightStatus.Pass, $"Both resource accounts already have the license {Label(license)}.");
        }

        return left >= needed
            ? new PreflightCheck(name, PreflightStatus.Pass,
                $"This entry needs {needed} license(s) of {Label(license)}; {left} are free.")
            : new PreflightCheck(name, PreflightStatus.Fail,
                $"This entry needs {needed} license(s) of {Label(license)}, but only {Math.Max(left, 0)} are free" +
                (before > 0 ? " after the entries before it" : string.Empty) + ". The license step would fail.");
    }

    private static PreflightCheck Number(IPhoneManagerVariables source, PreflightTenantRow row)
    {
        const string name = "Phone number available";
        var wanted = source.RaaAnr.Trim();
        var number = row.Number;
        if (number is null)
        {
            return new PreflightCheck(name, PreflightStatus.NotChecked, $"The tenant could not be read for the number {wanted}.");
        }

        if (!number.InInventory)
        {
            return string.Equals(source.PhoneNumberType, "DirectRouting", StringComparison.OrdinalIgnoreCase)
                ? new PreflightCheck(name, PreflightStatus.Pass, $"The number {wanted} is not in the inventory of the tenant. A Direct Routing number can be assigned without it.")
                : new PreflightCheck(name, PreflightStatus.Fail, $"The tenant has no number {wanted}. Only a Direct Routing number can be assigned without being in the inventory.");
        }

        if (!string.IsNullOrWhiteSpace(number.Type) && !string.IsNullOrWhiteSpace(source.PhoneNumberType) &&
            !string.Equals(number.Type, source.PhoneNumberType, StringComparison.OrdinalIgnoreCase))
        {
            return new PreflightCheck(name, PreflightStatus.Fail,
                $"The number {wanted} is a {number.Type} number in the tenant, not {source.PhoneNumberType}.");
        }

        if (string.IsNullOrWhiteSpace(number.AssignedToId))
        {
            return new PreflightCheck(name, PreflightStatus.Pass, $"The number {wanted} is in the tenant and not assigned.");
        }

        var owner = string.IsNullOrWhiteSpace(number.AssignedTo) ? number.AssignedToId : number.AssignedTo;
        var own = row.AutoAttendantAccount is { Exists: true } account &&
                  (string.Equals(account.Id, number.AssignedToId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(source.RaaaUPN, number.AssignedTo, StringComparison.OrdinalIgnoreCase));
        return own
            ? new PreflightCheck(name, PreflightStatus.Pass, $"The number {wanted} is already assigned to the auto attendant resource account.")
            : new PreflightCheck(name, PreflightStatus.Fail, $"The number {wanted} is assigned to {owner}. The number step would fail.");
    }

    private static string Label(PreflightLicense license)
        => string.IsNullOrWhiteSpace(license.Name) ? license.SkuId : license.Name;

    private const string ReadTemplate = """
        try {
            $pdRequest = ConvertFrom-Json ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('__REQUEST__')))
            $pdLicenses = @()
            $pdLicensesRead = $false
            $pdLicenseError = ''
            try {
                $pdLicenses = @(Get-MgSubscribedSku -All -ErrorAction Stop | Where-Object { $_ } | ForEach-Object {
                    [ordered]@{
                        skuId = [string]$_.SkuId
                        name = [string]$_.SkuPartNumber
                        available = [int]$_.PrepaidUnits.Enabled - [int]$_.ConsumedUnits
                    }
                })
                $pdLicensesRead = $true
            }
            catch {
                $pdLicenseError = "$_"
            }
            $pdAccountOf = {
                param($pdUpn, $pdSku)
                $pdFilter = "userPrincipalName eq '" + ([string]$pdUpn).Replace("'", "''") + "'"
                $pdUser = @(Get-MgUser -Filter $pdFilter -Property 'Id,UserPrincipalName,AssignedLicenses' -ErrorAction Stop) | Where-Object { $_ } | Select-Object -First 1
                if (-not $pdUser) {
                    return [ordered]@{ exists = $false; id = ''; hasLicense = $false }
                }
                $pdHas = @(@($pdUser.AssignedLicenses) | Where-Object { $_ -and "$($_.SkuId)" -eq "$pdSku" }).Count -gt 0
                return [ordered]@{ exists = $true; id = [string]$pdUser.Id; hasLicense = [bool]$pdHas }
            }
            $pdRows = @()
            foreach ($pdEntry in @($pdRequest.entries)) {
                $pdErrors = @()
                $pdGroups = $null
                $pdQueues = $null
                $pdAttendants = $null
                $pdQueueAccount = $null
                $pdAttendantAccount = $null
                $pdNumber = $null
                try {
                    $pdFilter = "displayName eq '" + ([string]$pdEntry.group).Replace("'", "''") + "'"
                    $pdGroups = @(Get-MgGroup -Filter $pdFilter -All -ErrorAction Stop | Where-Object { $_ }).Count
                }
                catch { $pdErrors += "Agent group: $_" }
                try {
                    $pdQueues = @(Get-CsCallQueue -NameFilter ([string]$pdEntry.callQueue) -WarningAction SilentlyContinue -ErrorAction Stop | Where-Object { $_ -and $_.Name -eq [string]$pdEntry.callQueue }).Count
                }
                catch { $pdErrors += "Call queue: $_" }
                try {
                    $pdAttendants = @(Get-CsAutoAttendant -NameFilter ([string]$pdEntry.autoAttendant) -ErrorAction Stop | Where-Object { $_ -and $_.Name -eq [string]$pdEntry.autoAttendant }).Count
                }
                catch { $pdErrors += "Auto attendant: $_" }
                try { $pdQueueAccount = (& $pdAccountOf $pdEntry.callQueueAccount $pdEntry.skuId) }
                catch { $pdErrors += "Call queue resource account: $_" }
                try { $pdAttendantAccount = (& $pdAccountOf $pdEntry.autoAttendantAccount $pdEntry.skuId) }
                catch { $pdErrors += "Auto attendant resource account: $_" }
                if ([string]$pdEntry.number) {
                    try {
                        $pdItem = @(Get-CsPhoneNumberAssignment -TelephoneNumber ([string]$pdEntry.number) -ErrorAction Stop) | Where-Object { $_ -and "$($_.TelephoneNumber)" -eq [string]$pdEntry.number } | Select-Object -First 1
                        if ($pdItem) {
                            $pdOwnerId = [string]$pdItem.AssignedPstnTargetId
                            $pdOwnerName = ''
                            if ($pdOwnerId) {
                                try { $pdOwnerName = [string](Get-CsOnlineUser -Identity $pdOwnerId -ErrorAction Stop).UserPrincipalName } catch { $pdOwnerName = '' }
                            }
                            $pdNumber = [ordered]@{ inInventory = $true; type = [string]$pdItem.NumberType; assignedToId = $pdOwnerId; assignedTo = $pdOwnerName }
                        }
                        else {
                            $pdNumber = [ordered]@{ inInventory = $false; type = ''; assignedToId = ''; assignedTo = '' }
                        }
                    }
                    catch { $pdErrors += "Phone number: $_" }
                }
                $pdRows += [ordered]@{
                    row = [int]$pdEntry.row
                    groups = $pdGroups
                    callQueues = $pdQueues
                    autoAttendants = $pdAttendants
                    callQueueAccount = $pdQueueAccount
                    autoAttendantAccount = $pdAttendantAccount
                    number = $pdNumber
                    errors = @($pdErrors)
                }
            }
            $pdDocument = [ordered]@{
                rows = @($pdRows)
                licenses = @($pdLicenses)
                licensesRead = $pdLicensesRead
                licenseError = $pdLicenseError
            }
            Write-Host ("PDPREFLIGHT_JSON: " + (ConvertTo-Json -InputObject $pdDocument -Depth 6 -Compress))
            Write-Host "SUCCESS: Tenant read for the preview"
        }
        catch {
            Write-Host "ERROR: Failed to read the tenant for the preview: $_"
        }
        """;
}
