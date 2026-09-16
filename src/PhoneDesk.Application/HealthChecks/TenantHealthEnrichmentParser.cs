using PhoneDesk.HealthChecks;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services;

public sealed class TenantHealthEnrichmentParser : ITenantHealthEnrichmentParser
{
    public TenantHealthEnrichment Parse(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            throw new InvalidDataException("Health-check output is empty.");
        }

        RequireMarker(rawOutput, "HEALTH_RA_START");
        RequireMarker(rawOutput, "HEALTH_RA_END");
        RequireMarker(rawOutput, "HEALTH_AA_START");
        RequireMarker(rawOutput, "HEALTH_AA_END");
        RequireMarker(rawOutput, "HEALTH_PHONE_START");
        RequireMarker(rawOutput, "HEALTH_PHONE_END");
        RequireMarker(rawOutput, "SUCCESS: Tenant health enrichment retrieved");

        var resourceAccounts = Rows(rawOutput, "HEALTH_RA:", 3)
            .Select(parts => new ResourceAccountHealthState(
                parts[0],
                ParseBoolean(parts[1], "resource-account license state"),
                parts[2]))
            .OrderBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToArray();
        var autoAttendants = Rows(rawOutput, "HEALTH_AA:", 2)
            .Select(parts => new AutoAttendantHealthState(
                parts[0],
                ParseBoolean(parts[1], "auto-attendant after-hours state")))
            .OrderBy(item => item.Identity, StringComparer.Ordinal)
            .ToArray();
        var serviceNumbers = Rows(rawOutput, "HEALTH_PHONE:", 2)
            .Select(parts => new UnassignedServiceNumberHealthState(parts[0], parts[1]))
            .OrderBy(item => item.TelephoneNumber, StringComparer.Ordinal)
            .ToArray();

        EnsureUnique(resourceAccounts.Select(item => item.ObjectId), "resource account");
        EnsureUnique(autoAttendants.Select(item => item.Identity), "auto attendant");
        EnsureUnique(serviceNumbers.Select(item => item.TelephoneNumber), "service phone number");

        return new TenantHealthEnrichment(resourceAccounts, autoAttendants, serviceNumbers);
    }

    private static IReadOnlyList<string[]> Rows(string rawOutput, string prefix, int fieldCount)
    {
        var rows = new List<string[]>();
        foreach (var rawLine in rawOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var parts = line[prefix.Length..].Split('|').Select(part => part.Trim()).ToArray();
            if (parts.Length != fieldCount)
            {
                throw new InvalidDataException($"Malformed {prefix.TrimEnd(':')} health-check row.");
            }
            rows.Add(parts);
        }
        return rows;
    }

    private static bool ParseBoolean(string value, string description)
        => bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Invalid {description} '{value}'.");

    private static void RequireMarker(string rawOutput, string marker)
    {
        if (!rawOutput.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Health-check output is missing the {marker} marker.");
        }
    }

    private static void EnsureUnique(IEnumerable<string> ids, string description)
    {
        var values = ids.ToArray();
        if (values.Any(string.IsNullOrWhiteSpace) ||
            values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            throw new InvalidDataException($"Health-check output contains an invalid or duplicate {description} ID.");
        }
    }
}
