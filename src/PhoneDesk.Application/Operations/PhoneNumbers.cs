using System.Text.Json;

namespace PhoneDesk.Operations;

/// <summary>A user or a resource account that can have a phone number.</summary>
public sealed class NumberOwner
{
    public string Id { get; init; } = string.Empty;

    public string UserPrincipalName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>User or ResourceAccount, as Teams reports it.</summary>
    public string AccountType { get; init; } = string.Empty;

    /// <summary>The number the account has now, in E.164 format, or empty.</summary>
    public string PhoneNumber { get; init; } = string.Empty;

    public IReadOnlyList<string> FeatureTypes { get; init; } = Array.Empty<string>();

    public bool IsResourceAccount => string.Equals(AccountType, "ResourceAccount", StringComparison.OrdinalIgnoreCase);

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? UserPrincipalName : $"{DisplayName} ({UserPrincipalName})";
}

/// <summary>A phone number of the tenant's inventory.</summary>
public sealed class PhoneNumberRow
{
    public string Number { get; init; } = string.Empty;

    /// <summary>DirectRouting, CallingPlan or OperatorConnect.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Unassigned, UserAssigned, VoiceApplicationAssigned and so on, as Teams reports it.</summary>
    public string Status { get; init; } = string.Empty;

    public string AssignedToId { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    /// <summary>The account the number is assigned to, when it was looked up.</summary>
    public NumberOwner? Owner { get; init; }

    public bool IsAssigned => !string.IsNullOrWhiteSpace(AssignedToId);
}

/// <summary>What the tenant holds about one number and one target, read for a change.</summary>
public sealed class PhoneNumberCheckState
{
    /// <summary>The number in the inventory, or null when the tenant does not have it.</summary>
    public PhoneNumberRow? Number { get; init; }

    /// <summary>The account that would get the number, or null when it was not asked for or not found.</summary>
    public NumberOwner? Target { get; init; }

    public string? TargetError { get; init; }
}

public enum PhoneNumberChangeKind
{
    Assign,
    Reassign,
    Unassign
}

/// <summary>The result of checking a number change against the tenant. Nothing is changed by the check.</summary>
public sealed class PhoneNumberAssessment
{
    public PhoneNumberChangeKind Kind { get; init; }

    public string Number { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public PhoneNumberRow? Row { get; init; }

    /// <summary>The account that has the number now, or null.</summary>
    public NumberOwner? CurrentOwner { get; init; }

    public NumberOwner? Target { get; init; }

    public IReadOnlyList<HolidayBlocker> Blockers { get; init; } = Array.Empty<HolidayBlocker>();

    /// <summary>Things the operator should know that do not stop the change.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool NoChange { get; init; }

    public bool CanSave => Blockers.Count == 0 && !NoChange;
}

/// <summary>
/// The phone number inventory and the assignment of a number to a user or a resource account:
/// assign, unassign (the number stays in the inventory) and move from one account to another.
/// Both heads use the same read scripts, checks and change scripts. A number is never released
/// from the inventory here; that is a separate, destructive operation.
/// </summary>
public static class PhoneNumbers
{
    public const string ListMarker = "PDNUMBERS_JSON: ";
    public const string CheckMarker = "PDNUMBERCHECK_JSON: ";

    public static readonly IReadOnlyList<string> Types = new[] { "DirectRouting", "CallingPlan", "OperatorConnect" };

    /// <summary>Owners are looked up for a list only up to this number: one lookup is one call to Teams.</summary>
    public const int OwnerLookupLimit = 100;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string RequireNumber(string? number)
    {
        var text = (number ?? string.Empty).Trim();
        return PhoneNumberScripts.IsE164(text)
            ? text
            : throw new ArgumentException($"'{number}' is not a phone number in E.164 format, for example +41441234567.");
    }

    public static string RequireType(string? type)
    {
        var text = (type ?? string.Empty).Trim();
        return Types.FirstOrDefault(known => string.Equals(known, text, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"'{type}' is not a phone number type. Use {string.Join(", ", Types)}.");
    }

    /// <summary>An account is named by its UPN or its object ID. Nothing else goes into a script.</summary>
    public static string RequireIdentity(string? identity)
    {
        var text = (identity ?? string.Empty).Trim();
        if (Guid.TryParse(text, out var id))
        {
            return id.ToString("D");
        }

        // A UPN: letters, digits and the few characters an address has. No quote, no space, no PowerShell character.
        var isUpn = text.Length is > 2 and <= 256 && text.Count(character => character == '@') == 1 &&
                    text.All(character => char.IsLetterOrDigit(character) || character is '@' or '.' or '-' or '_');
        return isUpn
            ? text
            : throw new ArgumentException($"'{identity}' is not a user principal name or an object ID.");
    }

    /// <summary>
    /// Lists numbers of the inventory. <paramref name="contains"/> filters on digits of the number,
    /// <paramref name="status"/> on "assigned" or "unassigned". The script only reads.
    /// </summary>
    public static string GetListCommand(string? contains = null, string? status = null, int top = 500)
    {
        var digits = new string((contains ?? string.Empty).Where(char.IsDigit).ToArray());
        var filter = status?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => string.Empty,
            "unassigned" => " -PstnAssignmentStatus Unassigned",
            "assigned" => string.Empty,
            _ => throw new ArgumentException($"'{status}' is not a status filter. Use assigned, unassigned or all.")
        };
        return ListTemplate
            .Replace("__CONTAINS__", digits.Length == 0 ? string.Empty : " -TelephoneNumberContains '" + digits + "'", StringComparison.Ordinal)
            .Replace("__STATUS__", filter, StringComparison.Ordinal)
            .Replace("__ASSIGNEDONLY__", string.Equals(status?.Trim(), "assigned", StringComparison.OrdinalIgnoreCase) ? "$true" : "$false", StringComparison.Ordinal)
            .Replace("__TOP__", Math.Clamp(top, 1, 5000).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__LOOKUPS__", OwnerLookupLimit.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    public static IReadOnlyList<PhoneNumberRow> ParseList(string? output)
        => JsonSerializer.Deserialize<List<PhoneNumberRow>>(Json(output, ListMarker, "The phone numbers"), Options) ?? new List<PhoneNumberRow>();

    /// <summary>Reads one number with its owner and, when given, the account that would get it. The script only reads.</summary>
    public static string GetCheckCommand(string number, string? targetIdentity)
        => CheckTemplate
            .Replace("__TARGET__", string.IsNullOrWhiteSpace(targetIdentity) ? string.Empty : RequireIdentity(targetIdentity), StringComparison.Ordinal)
            .Replace("__NUMBER__", RequireNumber(number), StringComparison.Ordinal);

    public static PhoneNumberCheckState ParseCheck(string? output)
        => JsonSerializer.Deserialize<PhoneNumberCheckState>(Json(output, CheckMarker, "The phone number"), Options) ?? new PhoneNumberCheckState();

    /// <summary>
    /// Checks the assignment of a number to an account. A number another account has is refused
    /// unless <paramref name="allowReassign"/> is set; then it is a move, and both accounts are shown.
    /// </summary>
    public static PhoneNumberAssessment AssessAssign(PhoneNumberCheckState state, string number, string? type, bool allowReassign)
    {
        ArgumentNullException.ThrowIfNull(state);
        var blockers = new List<HolidayBlocker>();
        var warnings = new List<string>();
        var wanted = RequireNumber(number);
        var row = state.Number;
        var target = state.Target;
        if (target is null)
        {
            blockers.Add(new("targetNotFound", "The account that should get the number was not found" +
                (string.IsNullOrWhiteSpace(state.TargetError) ? "." : ": " + state.TargetError)));
        }

        // The type comes from the inventory when the number is there; only a Direct Routing number can be new.
        var numberType = row is not null && !string.IsNullOrWhiteSpace(row.Type) ? row.Type : string.IsNullOrWhiteSpace(type) ? string.Empty : RequireType(type);
        if (row is null)
        {
            if (!string.Equals(numberType, "DirectRouting", StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add(new("numberNotInInventory",
                    $"The tenant has no number {wanted}. Only a Direct Routing number can be assigned without being in the inventory; give the type DirectRouting for that."));
            }
        }
        else if (!string.IsNullOrWhiteSpace(type) && !string.Equals(RequireType(type), row.Type, StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(new("typeMismatch", $"The number {wanted} is a {row.Type} number in the inventory, not {RequireType(type)}."));
        }

        var owner = row?.Owner;
        var alreadyThere = target is not null && row is not null && row.IsAssigned && HolidayCallHandling.SameId(row.AssignedToId, target.Id);
        var kind = row is not null && row.IsAssigned && !alreadyThere ? PhoneNumberChangeKind.Reassign : PhoneNumberChangeKind.Assign;
        if (kind == PhoneNumberChangeKind.Reassign && !allowReassign)
        {
            blockers.Add(new("assignedElsewhere",
                $"The number {wanted} is assigned to {(owner is null ? row!.AssignedToId : owner.Label)}. " +
                "To move it, confirm the move: that account loses the number."));
        }

        if (target is not null && row is not null && row.Capabilities.Count > 0)
        {
            var needed = target.IsResourceAccount ? "VoiceApplicationAssignment" : "UserAssignment";
            if (!row.Capabilities.Contains(needed, StringComparer.OrdinalIgnoreCase))
            {
                blockers.Add(new("capability",
                    $"The number {wanted} cannot be used for a {(target.IsResourceAccount ? "resource account" : "user")}: " +
                    $"it has the capabilities {string.Join(", ", row.Capabilities)}, not {needed}."));
            }
        }

        if (target is not null && !alreadyThere && !string.IsNullOrWhiteSpace(target.PhoneNumber) &&
            !string.Equals(target.PhoneNumber, wanted, StringComparison.Ordinal))
        {
            warnings.Add($"{target.Label} has the number {target.PhoneNumber} now. It is replaced by {wanted}; {target.PhoneNumber} becomes unassigned and stays in the inventory.");
        }

        if (target is not null && target.FeatureTypes.Count > 0 &&
            !target.FeatureTypes.Any(feature => feature.Contains("PhoneSystem", StringComparison.OrdinalIgnoreCase) || feature.Contains("VoiceApp", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"{target.Label} does not show a Teams Phone feature ({string.Join(", ", target.FeatureTypes)}). Teams refuses a number for an account without the license.");
        }

        return new PhoneNumberAssessment
        {
            Kind = kind,
            Number = wanted,
            Type = numberType,
            Row = row,
            CurrentOwner = owner,
            Target = target,
            Blockers = blockers,
            Warnings = warnings,
            NoChange = blockers.Count == 0 && alreadyThere
        };
    }

    /// <summary>Checks taking a number off the account that has it. The number stays in the inventory.</summary>
    public static PhoneNumberAssessment AssessUnassign(PhoneNumberCheckState state, string number)
    {
        ArgumentNullException.ThrowIfNull(state);
        var blockers = new List<HolidayBlocker>();
        var wanted = RequireNumber(number);
        var row = state.Number;
        if (row is null)
        {
            blockers.Add(new("numberNotInInventory", $"The tenant has no number {wanted}."));
        }

        return new PhoneNumberAssessment
        {
            Kind = PhoneNumberChangeKind.Unassign,
            Number = wanted,
            Type = row?.Type ?? string.Empty,
            Row = row,
            CurrentOwner = row?.Owner,
            Blockers = blockers,
            NoChange = row is not null && !row.IsAssigned
        };
    }

    /// <summary>
    /// The script of a checked change. Accounts are named by object ID. A move takes the number off
    /// the old account and gives it to the new one; when the second step fails, the script gives the
    /// number back to the old account and says which of the two states the tenant is in.
    /// </summary>
    public static string GetChangeCommand(PhoneNumberAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (!assessment.CanSave)
        {
            throw new InvalidOperationException("The change was not checked or cannot be saved.");
        }

        var number = RequireNumber(assessment.Number);
        var type = RequireType(assessment.Type);
        var template = assessment.Kind switch
        {
            PhoneNumberChangeKind.Assign => AssignTemplate,
            PhoneNumberChangeKind.Reassign => ReassignTemplate,
            _ => UnassignTemplate
        };
        return template
            .Replace("__OLD__", assessment.Row is null || !assessment.Row.IsAssigned ? string.Empty : HolidayCallHandling.RequireId(assessment.Row.AssignedToId, "the account that has the number"), StringComparison.Ordinal)
            .Replace("__NEW__", assessment.Target is null ? string.Empty : HolidayCallHandling.RequireId(assessment.Target.Id, "the account that gets the number"), StringComparison.Ordinal)
            .Replace("__NUMBER__", number, StringComparison.Ordinal)
            .Replace("__TYPE__", type, StringComparison.Ordinal);
    }

    /// <summary>What an approval is bound to: the number, who has it, and who gets it.</summary>
    public static string ApprovedState(PhoneNumberAssessment assessment)
        => JsonSerializer.Serialize(new
        {
            kind = assessment.Kind.ToString(),
            assessment.Number,
            assessment.Type,
            status = assessment.Row?.Status,
            owner = assessment.Row?.AssignedToId,
            target = assessment.Target?.Id,
            targetNumber = assessment.Target?.PhoneNumber
        });

    /// <summary>Empty when the tenant shows the change; otherwise what it shows instead.</summary>
    public static string Verify(PhoneNumberAssessment assessment, PhoneNumberCheckState after)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(after);
        var row = after.Number;
        if (assessment.Kind == PhoneNumberChangeKind.Unassign)
        {
            return row is null || !row.IsAssigned ? string.Empty : $"{assessment.Number} is still assigned to {row.Owner?.Label ?? row.AssignedToId}.";
        }

        if (row is null || !row.IsAssigned)
        {
            return $"{assessment.Number} is not assigned to any account.";
        }

        return HolidayCallHandling.SameId(row.AssignedToId, assessment.Target?.Id)
            ? string.Empty
            : $"{assessment.Number} is assigned to {row.Owner?.Label ?? row.AssignedToId}, not to {assessment.Target?.Label}.";
    }

    private static string Json(string? output, string marker, string what)
    {
        var lines = (output ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var json = lines.LastOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal));
        if (json is null)
        {
            var error = lines.FirstOrDefault(line => line.StartsWith("ERROR:", StringComparison.Ordinal));
            throw new InvalidDataException(error ?? what + " could not be read: the tenant returned no data.");
        }

        return json[marker.Length..];
    }

    // Raw string literals end without a line break, so parts of a script are joined with one.
    private const string NewLine = "\n";

    private const string OwnerFunction = """
            $pdOwnerOf = {
                param($pdIdentity)
                $pdUser = Get-CsOnlineUser -Identity $pdIdentity -ErrorAction Stop
                if (-not $pdUser) { return $null }
                [ordered]@{
                    id = [string]$pdUser.Identity
                    userPrincipalName = [string]$pdUser.UserPrincipalName
                    displayName = [string]$pdUser.DisplayName
                    accountType = [string]$pdUser.AccountType
                    phoneNumber = ([string]$pdUser.LineUri -replace '^tel:', '' -replace ';.*$', '')
                    featureTypes = @(@($pdUser.FeatureTypes) | Where-Object { $_ } | ForEach-Object { [string]$_ })
                }
            }
            $pdRowOf = {
                param($pdItem, $pdOwner)
                [ordered]@{
                    number = [string]$pdItem.TelephoneNumber
                    type = [string]$pdItem.NumberType
                    status = [string]$pdItem.PstnAssignmentStatus
                    assignedToId = [string]$pdItem.AssignedPstnTargetId
                    city = [string]$pdItem.City
                    capabilities = @(@($pdItem.Capability) | Where-Object { $_ } | ForEach-Object { [string]$_ })
                    owner = $pdOwner
                }
            }
        """;

    private const string ListTemplate = """
        try {
        """ + NewLine + OwnerFunction + NewLine + """
            $pdNumbers = @(Get-CsPhoneNumberAssignment__CONTAINS____STATUS__ -Top __TOP__ -ErrorAction Stop)
            if (__ASSIGNEDONLY__) {
                $pdNumbers = @($pdNumbers | Where-Object { $_.AssignedPstnTargetId })
            }
            $pdOwners = @{}
            $pdRows = @()
            foreach ($pdItem in $pdNumbers) {
                if (-not $pdItem) { continue }
                $pdOwner = $null
                $pdTargetId = [string]$pdItem.AssignedPstnTargetId
                if ($pdTargetId) {
                    if (-not $pdOwners.ContainsKey($pdTargetId) -and $pdOwners.Count -lt __LOOKUPS__) {
                        try { $pdOwners[$pdTargetId] = (& $pdOwnerOf $pdTargetId) } catch { $pdOwners[$pdTargetId] = $null }
                    }
                    if ($pdOwners.ContainsKey($pdTargetId)) { $pdOwner = $pdOwners[$pdTargetId] }
                }
                $pdRows += @(& $pdRowOf $pdItem $pdOwner)
            }
            Write-Host ("PDNUMBERS_JSON: " + (ConvertTo-Json -InputObject @($pdRows) -Depth 6 -Compress))
            Write-Host "SUCCESS: Phone numbers retrieved"
        }
        catch {
            Write-Host "ERROR: Failed to read the phone numbers: $_"
        }
        """;

    private const string CheckTemplate = """
        try {
        """ + NewLine + OwnerFunction + NewLine + """
            $pdNumber = $null
            $pdItem = @(Get-CsPhoneNumberAssignment -TelephoneNumber '__NUMBER__' -ErrorAction Stop) | Where-Object { $_ -and "$($_.TelephoneNumber)" -eq '__NUMBER__' } | Select-Object -First 1
            if ($pdItem) {
                $pdOwner = $null
                if ($pdItem.AssignedPstnTargetId) {
                    try { $pdOwner = (& $pdOwnerOf ([string]$pdItem.AssignedPstnTargetId)) } catch { $pdOwner = $null }
                }
                $pdNumber = (& $pdRowOf $pdItem $pdOwner)
            }
            $pdTarget = $null
            $pdTargetError = $null
            if ('__TARGET__') {
                try { $pdTarget = (& $pdOwnerOf '__TARGET__') } catch { $pdTargetError = "$_" }
            }
            $pdDocument = [ordered]@{ number = $pdNumber; target = $pdTarget; targetError = $pdTargetError }
            Write-Host ("PDNUMBERCHECK_JSON: " + (ConvertTo-Json -InputObject $pdDocument -Depth 6 -Compress))
            Write-Host "SUCCESS: Phone number retrieved"
        }
        catch {
            Write-Host "ERROR: Failed to read the phone number __NUMBER__: $_"
        }
        """;

    private const string AssignTemplate = """
        try {
            Set-CsPhoneNumberAssignment -Identity '__NEW__' -PhoneNumber '__NUMBER__' -PhoneNumberType __TYPE__ -ErrorAction Stop
            Write-Host "SUCCESS: __NUMBER__ was assigned"
        }
        catch {
            Write-Host "ERROR: Failed to assign __NUMBER__: $_"
        }
        """;

    private const string UnassignTemplate = """
        try {
            Remove-CsPhoneNumberAssignment -Identity '__OLD__' -PhoneNumber '__NUMBER__' -PhoneNumberType __TYPE__ -ErrorAction Stop
            Write-Host "SUCCESS: __NUMBER__ was taken off the account and stays in the inventory"
        }
        catch {
            Write-Host "ERROR: Failed to unassign __NUMBER__: $_"
        }
        """;

    private const string ReassignTemplate = """
        try {
            $pdNow = @(Get-CsPhoneNumberAssignment -TelephoneNumber '__NUMBER__' -ErrorAction Stop) | Where-Object { $_ -and "$($_.TelephoneNumber)" -eq '__NUMBER__' } | Select-Object -First 1
            if (-not $pdNow -or "$($pdNow.AssignedPstnTargetId)" -ne '__OLD__') {
                throw "The number is not assigned to the account the preview showed. Nothing was changed."
            }
            Remove-CsPhoneNumberAssignment -Identity '__OLD__' -PhoneNumber '__NUMBER__' -PhoneNumberType __TYPE__ -ErrorAction Stop
            try {
                Set-CsPhoneNumberAssignment -Identity '__NEW__' -PhoneNumber '__NUMBER__' -PhoneNumberType __TYPE__ -ErrorAction Stop
            }
            catch {
                $pdReason = "$_"
                # The number must not be left without an account: it goes back to the account that had it.
                $pdGivenBack = $false
                $pdBackError = ''
                try {
                    Set-CsPhoneNumberAssignment -Identity '__OLD__' -PhoneNumber '__NUMBER__' -PhoneNumberType __TYPE__ -ErrorAction Stop
                    $pdGivenBack = $true
                }
                catch {
                    $pdBackError = "$_"
                }
                if ($pdGivenBack) {
                    throw "The new account did not take the number ($pdReason). The number was given back to the account that had it."
                }
                throw "The new account did not take the number ($pdReason), and it could not be given back to the old account ($pdBackError). The number is UNASSIGNED now."
            }
            Write-Host "SUCCESS: __NUMBER__ was moved to the new account"
        }
        catch {
            Write-Host "ERROR: Failed to move __NUMBER__: $_"
        }
        """;
}
