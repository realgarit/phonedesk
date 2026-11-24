using PhoneDesk.Portability;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Operations;

/// <summary>
/// Reads the complete live tenant snapshot: the topology plus every tenant report source. It
/// fails closed: a failed query, a warning or error line, or a missing completion marker stops the
/// read, so a partial snapshot is never returned as if it were complete.
/// </summary>
public sealed class TenantSnapshotCollector
{
    private const string TopologyCompletionMarker = "SUCCESS: Tenant topology retrieved";

    /// <summary>
    /// Warnings a Teams cmdlet prints about an object's settings. They say nothing about missing
    /// data, so they do not make a snapshot incomplete. Every other warning still stops the read:
    /// an unknown warning can mean truncated results.
    /// </summary>
    private static readonly string[] InformationalWarnings =
    {
        "ConferenceMode is turned on for this callqueue"
    };

    private readonly IDocumentationScriptBuilder _documentationScripts;
    private readonly IPowerShellCommandService _commands;
    private readonly ITenantDocumentationSnapshotParser _snapshotParser;
    private readonly ITenantTopologyAssembler _topologyAssembler;
    private readonly ITenantAsCodeService _tenantAsCode;

    public TenantSnapshotCollector(
        IDocumentationScriptBuilder documentationScripts,
        IPowerShellCommandService commands,
        ITenantDocumentationSnapshotParser snapshotParser,
        ITenantTopologyAssembler topologyAssembler,
        ITenantAsCodeService tenantAsCode)
    {
        _documentationScripts = documentationScripts;
        _commands = commands;
        _snapshotParser = snapshotParser;
        _topologyAssembler = topologyAssembler;
        _tenantAsCode = tenantAsCode;
    }

    /// <param name="read">Runs one read-only command (script, operation name) and returns its result.</param>
    /// <exception cref="InvalidDataException">The snapshot could not be read completely.</exception>
    public async Task<TopologySnapshotDocument> CollectAsync(
        Func<string, string, Task<OperationResult<string>>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var tenant = await ReadCompleteAsync(read, _documentationScripts.GetExportTenantInfoCommand(), "SnapshotTenantInfo");
        var resourceAccounts = await ReadCompleteAsync(read, _documentationScripts.GetExportResourceAccountsCommand(), "SnapshotResourceAccounts");
        var autoAttendants = await ReadCompleteAsync(read, _documentationScripts.GetExportAutoAttendantsCommand(), "SnapshotAutoAttendants");
        var callQueues = await ReadCompleteAsync(read, _documentationScripts.GetExportCallQueuesCommand(), "SnapshotCallQueues");
        var schedules = await ReadCompleteAsync(read, _documentationScripts.GetExportSchedulesCommand(), "SnapshotSchedules");
        var phoneNumbers = await ReadCompleteAsync(read, _documentationScripts.GetExportPhoneNumbersCommand(), "SnapshotPhoneNumbers");
        var voiceUsers = await ReadCompleteAsync(read, _documentationScripts.GetExportVoiceUsersCommand(), "SnapshotVoiceUsers");
        var topologyRaw = await ReadCompleteAsync(read, _commands.GetRetrieveTenantTopologyCommand(), "SnapshotTopology");
        if (!topologyRaw.Contains(TopologyCompletionMarker, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The live tenant topology query did not emit its completion marker.");
        }

        var retrievedAtUtc = DateTimeOffset.UtcNow;
        var documentation = _snapshotParser.Parse(new TenantDocumentationRawData(
            tenant,
            resourceAccounts,
            autoAttendants,
            callQueues,
            schedules,
            phoneNumbers,
            voiceUsers));
        var topology = _topologyAssembler.Assemble(topologyRaw, retrievedAtUtc);
        return _tenantAsCode.CreateTopologySnapshot(topology, documentation);
    }

    private static async Task<string> ReadCompleteAsync(
        Func<string, string, Task<OperationResult<string>>> read,
        string command,
        string operation)
    {
        var result = await read(command, operation);
        if (!result.IsSuccess)
        {
            throw new InvalidDataException(
                result.ErrorMessage ?? $"The {operation} query did not complete successfully.");
        }

        var output = result.Value ?? string.Empty;
        var diagnostic = output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line =>
                line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("TOPERR:", StringComparison.OrdinalIgnoreCase) ||
                (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) &&
                 !InformationalWarnings.Any(known => line.Contains(known, StringComparison.OrdinalIgnoreCase))));
        if (diagnostic is not null)
        {
            throw new InvalidDataException($"The live tenant snapshot is incomplete: {diagnostic}");
        }

        return output;
    }
}
