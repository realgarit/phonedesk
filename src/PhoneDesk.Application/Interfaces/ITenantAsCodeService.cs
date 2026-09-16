using PhoneDesk.Portability;
using PhoneDesk.Topology;

namespace PhoneDesk.Services.Interfaces;

public interface ITenantAsCodeService
{
    string SerializeConfiguration(ConfigurationDocument document);
    ConfigurationDocument DeserializeConfiguration(string json);
    IReadOnlyList<ConfigurationChange> CompareConfigurations(
        ConfigurationDocument current,
        ConfigurationDocument imported);

    string SerializeTopology(TenantTopology topology);
    TopologySnapshotDocument CreateTopologySnapshot(
        TenantTopology topology,
        TenantDocumentationSnapshot documentation);
    string SerializeTopology(TopologySnapshotDocument document);
    TopologySnapshotDocument DeserializeTopology(string json);
    IReadOnlyList<TopologyDriftEntry> CompareTopology(
        TopologySnapshotDocument snapshot,
        TenantTopology liveTopology);
    IReadOnlyList<TopologyDriftEntry> CompareTopology(
        TopologySnapshotDocument snapshot,
        TopologySnapshotDocument liveSnapshot);
}
