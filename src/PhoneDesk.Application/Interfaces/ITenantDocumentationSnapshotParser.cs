using PhoneDesk.Portability;

namespace PhoneDesk.Services.Interfaces;

/// <summary>
/// Parses the frozen DocumentationScriptBuilder marker output into the machine-readable
/// tenant-report portion of a topology snapshot.
/// </summary>
public interface ITenantDocumentationSnapshotParser
{
    TenantDocumentationSnapshot Parse(TenantDocumentationRawData rawData);
}
