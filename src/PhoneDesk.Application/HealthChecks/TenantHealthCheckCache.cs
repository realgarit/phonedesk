using PhoneDesk.HealthChecks;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services;

public sealed class TenantHealthCheckCache : ITenantHealthCheckCache
{
    public TenantHealthCheckResult? Current { get; private set; }

    public void Set(TenantHealthCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Current = result;
    }

    public void Clear() => Current = null;
}
