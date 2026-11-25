using PhoneDesk.Operations;

namespace PhoneDesk.Mcp;

/// <summary>Reads the holiday call handling from the connected tenant, always fresh: it is the check after a save.</summary>
internal sealed class HolidayCallHandlingReader
{
    private readonly TenantGateway _tenant;

    public HolidayCallHandlingReader(TenantGateway tenant)
    {
        _tenant = tenant;
    }

    public async Task<HolidayCallHandlingState> ReadAsync(
        IEnumerable<string>? autoAttendantIds,
        bool includeAutoAttendants,
        bool checkAuthorizedUsers,
        CancellationToken cancellationToken)
    {
        var result = await _tenant.ReadAsync(
            HolidayCallHandling.GetReadCommand(autoAttendantIds, includeAutoAttendants, checkAuthorizedUsers),
            "ReadHolidayCallHandling",
            cancellationToken);
        try
        {
            return HolidayCallHandling.ParseState(string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput);
        }
        catch (InvalidDataException) when (!result.IsSuccess && !string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            throw new InvalidDataException("The holiday call handling could not be read: " + result.ErrorMessage);
        }
    }

    /// <summary>
    /// Finds an auto attendant by its ID or its exact name. A name that more than one auto
    /// attendant has is refused: the caller must give the ID.
    /// </summary>
    public async Task<(string Id, string Name)> ResolveAutoAttendantAsync(string value, CancellationToken cancellationToken)
    {
        var wanted = HolidayCallHandlingText.Require(value, "auto attendant");
        foreach (var refresh in new[] { false, true })
        {
            var attendants = (await _tenant.GetTopologyAsync(refresh, cancellationToken)).AutoAttendants;
            var byId = attendants.FirstOrDefault(item => HolidayCallHandling.SameId(item.Identity, wanted));
            if (byId is not null)
            {
                return (byId.Identity, byId.Name);
            }

            var byName = attendants.Where(item => string.Equals(item.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1)
            {
                return (byName[0].Identity, byName[0].Name);
            }

            if (byName.Count > 1)
            {
                throw new InvalidOperationException(
                    $"{byName.Count} auto attendants are named '{wanted}'. Give the ID: {string.Join(", ", byName.Select(item => item.Identity))}.");
            }
        }

        throw new InvalidOperationException(
            $"No auto attendant has the ID or the name '{wanted}'. list_auto_attendants shows the auto attendants of the tenant.");
    }
}

internal static class HolidayCallHandlingText
{
    public static string Require(string? value, string what)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"The {what} is missing.")
            : value.Trim();
}
