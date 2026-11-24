using System.Text.RegularExpressions;

namespace PhoneDesk.Operations;

/// <summary>Scripts for the tenant's phone number inventory.</summary>
public static partial class PhoneNumberScripts
{
    /// <summary>True for a number in E.164 format, for example +41441234567.</summary>
    public static bool IsE164(string? number) => number is not null && E164().IsMatch(number);

    /// <summary>
    /// Removes an unassigned number from the tenant's number inventory. Teams refuses the removal
    /// when the number is assigned to a user or a resource account.
    /// </summary>
    public static string GetReleaseUnassignedPhoneNumberCommand(string number)
    {
        if (!IsE164(number))
        {
            throw new ArgumentException("The phone number must be in E.164 format, for example +41441234567.", nameof(number));
        }

        return $@"
try {{
    $result = Remove-CsOnlineTelephoneNumber -TelephoneNumber ""{number}"" -Force -ErrorAction Stop
    $notReleased = @($result.NumberIdsDeleteFailed) + @($result.NumberIdsNotOwnedByTenant) + @($result.NumberIdsManagedByServiceDesk) | Where-Object {{ $_ }}
    if ($notReleased) {{
        Write-Host ""ERROR: Phone number {number} was not released: $($notReleased -join ', ')""
    }}
    else {{
        Write-Host ""SUCCESS: Phone number {number} released""
    }}
}}
catch {{
    Write-Host ""ERROR: Failed to release phone number {number}: $_""
}}";
    }

    [GeneratedRegex(@"^\+[1-9][0-9]{5,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164();
}
