using PhoneDesk.Services.Interfaces;
using PhoneDesk.Topology;

namespace PhoneDesk.Services
{
    /// <summary>
    /// Trivial in-memory holder for the last retrieved topology. Registered as a singleton so the
    /// snapshot survives navigation between pages for the lifetime of the process. Assignment of a
    /// reference is atomic; the dashboard only ever reads/writes it from the UI thread.
    /// </summary>
    public sealed class TenantTopologyCache : ITenantTopologyCache
    {
        private TenantTopology? _current;
        private string? _tenantId;

        public bool HasValue => _current is not null;

        public TenantTopology? Current => _current;
        public string? TenantId => _tenantId;

        public void Set(string tenantId, TenantTopology topology)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentNullException.ThrowIfNull(topology);
            _tenantId = tenantId;
            _current = topology;
        }

        public void Clear()
        {
            _tenantId = null;
            _current = null;
        }
    }
}
