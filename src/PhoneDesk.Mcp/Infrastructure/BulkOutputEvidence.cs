namespace PhoneDesk.Mcp;

/// <summary>
/// Reads the output of the bulk script for the line that proves one object was created by this run.
/// The bulk script makes several objects and prints one combined output. A second run after a
/// failed first run reuses the objects the first run made, so "an object was reused somewhere"
/// says nothing about the other objects. Each create script prints its own line with the object's
/// name; that line is the proof.
/// </summary>
internal static class BulkOutputEvidence
{
    private const string RowMarker = "Processing: ";

    /// <summary>
    /// The part of the output that belongs to the row at <paramref name="rowIndex"/>, or null when
    /// the output does not have one part per row.
    /// </summary>
    public static string? RowOutput(string? output, int rowIndex, int rowCount)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        var parts = output.Split(RowMarker, StringSplitOptions.None);
        return parts.Length == rowCount + 1 ? parts[rowIndex + 1] : null;
    }

    /// <summary>True when the output has this object's own "created" line and no "reused" line for it.</summary>
    /// <param name="rowOutput">The output of the object's row, needed for a phone number: its line has no number in it.</param>
    public static bool Created(string? output, string? rowOutput, TenantObjectType type, string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(output) || name.Length == 0)
        {
            return false;
        }

        return type switch
        {
            TenantObjectType.M365Group => Has(output, $"{name} created successfully with ID:"),
            TenantObjectType.ResourceAccount => Has(output, $"Resource account {name} created successfully"),
            TenantObjectType.CallQueue =>
                Has(output, $"SUCCESS: Call queue {name} is ready") && !Has(output, $"Call queue {name} already existed"),
            TenantObjectType.AutoAttendant =>
                Has(output, $"SUCCESS: Auto attendant {name} is ready") && !Has(output, $"Auto attendant {name} already existed"),
            TenantObjectType.PhoneNumber => rowOutput is not null && Has(rowOutput, "SUCCESS: Phone number assigned successfully"),
            _ => false
        };
    }

    private static bool Has(string text, string line) => text.Contains(line, StringComparison.OrdinalIgnoreCase);
}
