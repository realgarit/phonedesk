using PhoneDesk.HealthChecks;
using PhoneDesk.Services;

namespace PhoneDesk.Tests;

public sealed class TenantHealthPreferencesStoreTests
{
    [Fact]
    public void PreferencesRoundTripPerTenantWithoutCrossTenantLeakage()
    {
        WithTemporaryDirectory(directory =>
        {
            var store = new TenantHealthPreferencesStore(directory);
            var preferences = new TenantHealthPreferences(
                new HashSet<string>(StringComparer.Ordinal) { "rule-b", "rule-a" },
                new HashSet<string>(StringComparer.Ordinal) { "finding-2", "finding-1" });

            Assert.True(store.Save("tenant-a", preferences));

            var loaded = store.Load("tenant-a");
            Assert.Equal(new[] { "rule-a", "rule-b" }, loaded.DisabledRuleIds.Order(StringComparer.Ordinal));
            Assert.Equal(new[] { "finding-1", "finding-2" }, loaded.SuppressedFindingIds.Order(StringComparer.Ordinal));
            Assert.Empty(store.Load("tenant-b").DisabledRuleIds);
            Assert.Empty(store.Load("tenant-b").SuppressedFindingIds);
            Assert.False(File.Exists(Path.Combine(directory, "tenant-health-preferences.json.tmp")));
        });
    }

    [Fact]
    public void CorruptExistingFileIsNotOverwritten()
    {
        WithTemporaryDirectory(directory =>
        {
            var path = Path.Combine(directory, "tenant-health-preferences.json");
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ corrupt");
            var store = new TenantHealthPreferencesStore(directory);

            var saved = store.Save("tenant-a", TenantHealthPreferences.Default);

            Assert.False(saved);
            Assert.Equal("{ corrupt", File.ReadAllText(path));
        });
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "phonedesk-health-tests", Guid.NewGuid().ToString("N"));
        try
        {
            action(directory);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
