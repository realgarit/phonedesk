using PhoneDesk.Topology;

namespace PhoneDesk.Services.Interfaces
{
    /// <summary>
    /// Process-lifetime, in-memory cache of the most recently retrieved <see cref="TenantTopology"/>.
    /// Registered as a singleton so navigating away from and back to the dashboard is instant
    /// (issue #64: "results cached in memory for the session"). Read-only data only; never persisted.
    /// </summary>
    public interface ITenantTopologyCache
    {
        bool HasValue { get; }
        string? TenantId { get; }
        TenantTopology? Current { get; }
        void Set(string tenantId, TenantTopology topology);
        void Clear();
    }
}
