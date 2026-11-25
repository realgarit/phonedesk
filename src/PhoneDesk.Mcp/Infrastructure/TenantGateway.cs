using PhoneDesk.Operations;
using PhoneDesk.Portability;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Topology;

namespace PhoneDesk.Mcp;

internal sealed record BackupInfo(string File, DateTimeOffset RetrievedAtUtc, int AutoAttendants, int CallQueues, int ResourceAccounts, int Groups);

/// <summary>Read access to the connected tenant: single reads, the topology, and full snapshots written as backups.</summary>
internal sealed class TenantGateway
{
    private readonly ITenantCommandRunner _runner;
    private readonly TenantConnectionService _connection;
    private readonly IPowerShellCommandService _commands;
    private readonly ITenantTopologyAssembler _topologyAssembler;
    private readonly ITenantTopologyCache _topologyCache;
    private readonly ISessionManager _session;
    private readonly TenantSnapshotCollector _snapshotCollector;
    private readonly ITenantAsCodeService _tenantAsCode;
    private readonly McpEnvironment _environment;

    private string? _sessionBackupTenantId;
    private BackupInfo? _sessionBackup;
    private string? _baselineTenantId;
    private string? _latestSnapshotTenantId;
    private TopologySnapshotDocument? _latestSnapshot;
    private TopologySnapshotDocument? _baseline;
    private bool _latestSnapshotOutdated;

    public TenantGateway(
        ITenantCommandRunner runner,
        TenantConnectionService connection,
        IPowerShellCommandService commands,
        ITenantTopologyAssembler topologyAssembler,
        ITenantTopologyCache topologyCache,
        ISessionManager session,
        TenantSnapshotCollector snapshotCollector,
        ITenantAsCodeService tenantAsCode,
        McpEnvironment environment)
    {
        _runner = runner;
        _connection = connection;
        _commands = commands;
        _topologyAssembler = topologyAssembler;
        _topologyCache = topologyCache;
        _session = session;
        _snapshotCollector = snapshotCollector;
        _tenantAsCode = tenantAsCode;
        _environment = environment;
    }

    /// <summary>
    /// The most recent full snapshot of the connected tenant read in this session. Null when the
    /// snapshot belongs to another tenant or nothing is connected.
    /// </summary>
    public TopologySnapshotDocument? LatestSnapshot
        => _connection.IsReady && string.Equals(_latestSnapshotTenantId, TenantId, StringComparison.OrdinalIgnoreCase)
            ? _latestSnapshot
            : null;

    /// <summary>
    /// The first backup of the connected tenant in this session. It shows what was in the tenant
    /// before this session changed anything. Null until a backup was taken.
    /// </summary>
    public TopologySnapshotDocument? BaselineSnapshot
        => _connection.IsReady && string.Equals(_baselineTenantId, TenantId, StringComparison.OrdinalIgnoreCase) ? _baseline : null;

    public string TenantId => _session.TenantId ?? string.Empty;

    public void RequireReady()
    {
        if (!_connection.IsReady)
        {
            throw new InvalidOperationException(
                "Not connected. Run check_modules, connect_teams and connect_graph first.");
        }
    }

    public async Task<OperationResult<string>> ReadAsync(string command, string operation, CancellationToken cancellationToken)
    {
        RequireReady();
        var outcome = await _runner.RunAsync(
            new TenantCommandRequest(command, operation) { IsIdempotentRead = true }, cancellationToken);
        return outcome.Result;
    }

    public async Task<TenantTopology> GetTopologyAsync(bool refresh, CancellationToken cancellationToken)
    {
        RequireReady();
        if (!refresh && _topologyCache.Current is not null &&
            string.Equals(_topologyCache.TenantId, _session.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return _topologyCache.Current;
        }

        var result = await ReadAsync(_commands.GetRetrieveTenantTopologyCommand(), "RetrieveTenantTopology", cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidDataException(result.ErrorMessage ?? "The tenant topology could not be read.");
        }

        var topology = _topologyAssembler.Assemble(result.Value, DateTimeOffset.UtcNow);
        _topologyCache.Set(TenantId, topology);
        return topology;
    }

    /// <summary>
    /// The latest snapshot, or null when a change ran after it was read. Reads that must show the
    /// tenant as it is now use this; the backup and the baseline keep the older snapshot.
    /// </summary>
    public TopologySnapshotDocument? CurrentSnapshot => _latestSnapshotOutdated ? null : LatestSnapshot;

    /// <summary>Forgets the cached topology after a change, so the next read shows the new state.</summary>
    public void InvalidateTopology()
    {
        _topologyCache.Clear();
        _latestSnapshotOutdated = true;
    }

    /// <summary>Forgets what was read from the tenant, after a sign-out. The backup files and the baseline stay.</summary>
    public void ForgetReads()
    {
        _topologyCache.Clear();
        _latestSnapshot = null;
        _latestSnapshotTenantId = null;
        _latestSnapshotOutdated = false;
    }

    public async Task<TopologySnapshotDocument> CollectSnapshotAsync(CancellationToken cancellationToken)
    {
        RequireReady();
        var tenantId = TenantId;
        var snapshot = await _snapshotCollector.CollectAsync(
            (command, operation) => ReadAsync(command, operation, cancellationToken));
        _latestSnapshotTenantId = tenantId;
        _latestSnapshot = snapshot;
        _latestSnapshotOutdated = false;
        return snapshot;
    }

    /// <summary>Reads the full tenant snapshot and writes it to the backup folder of the tenant.</summary>
    public async Task<BackupInfo> BackupAsync(CancellationToken cancellationToken)
    {
        var snapshot = await CollectSnapshotAsync(cancellationToken);
        var directory = _environment.BackupDirectory(TenantId);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(
            directory,
            $"phonedesk-topology-{snapshot.RetrievedAtUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.json");
        await File.WriteAllTextAsync(file, _tenantAsCode.SerializeTopology(snapshot), cancellationToken);

        if (BaselineSnapshot is null)
        {
            _baselineTenantId = TenantId;
            _baseline = snapshot;
        }

        _sessionBackupTenantId = TenantId;
        _sessionBackup = new BackupInfo(
            file,
            snapshot.RetrievedAtUtc,
            snapshot.AutoAttendants.Count,
            snapshot.CallQueues.Count,
            snapshot.ResourceAccounts.Count,
            snapshot.Groups.Count);
        return _sessionBackup;
    }

    /// <summary>
    /// Makes sure a backup of the connected tenant exists from this session before a change runs.
    /// Returns null only when the operator switched the automatic backup off.
    /// </summary>
    public async Task<BackupInfo?> EnsureBackupAsync(CancellationToken cancellationToken)
    {
        if (_environment.SkipBackup)
        {
            return null;
        }

        if (_sessionBackup is not null &&
            string.Equals(_sessionBackupTenantId, TenantId, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(_sessionBackup.File))
        {
            return _sessionBackup;
        }

        return await BackupAsync(cancellationToken);
    }

    /// <summary>The newest backup file of the connected tenant, or null when there is none.</summary>
    public string? FindLatestBackupFile() => ListBackupFiles().FirstOrDefault();

    /// <summary>The backup files of the connected tenant, newest first. The file name carries the UTC time of the read.</summary>
    public IReadOnlyList<string> ListBackupFiles()
    {
        var directory = _environment.BackupDirectory(TenantId);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "phonedesk-topology-*.json")
                .OrderByDescending(file => file, StringComparer.Ordinal)
                .ToList()
            : Array.Empty<string>();
    }

    /// <summary>The backup this session wrote before its first change, when there is one.</summary>
    public string? SessionBackupFile
        => _sessionBackup is not null && string.Equals(_sessionBackupTenantId, TenantId, StringComparison.OrdinalIgnoreCase)
            ? _sessionBackup.File
            : null;
}
