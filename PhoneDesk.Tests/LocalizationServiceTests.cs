using System.ComponentModel;
using System.Text.Json;
using PhoneDesk.Localization;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Tests;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void EmptyPreferenceStartsInEnglish()
    {
        var service = CreateService(preference: null);

        Assert.Equal(AppLanguage.English, service.CurrentLanguage);
        Assert.Equal("Settings", service.Text[UiTextKey.SettingsTitle]);
    }

    [Fact]
    public void InvalidStoredLanguageFallsBackToEnglish()
    {
        var service = CreateService(preference: (AppLanguage)999);

        Assert.Equal(AppLanguage.English, service.CurrentLanguage);
        Assert.Equal("Update {version} is available.", service.Text[UiTextKey.UpdateAvailable]);
    }

    [Fact]
    public void ChangingLanguageUpdatesFormattedTextNotifiesBindingsAndPersistsChoice()
    {
        var store = new InMemoryUserPreferencesStore();
        var service = CreateService(store);
        var serviceNotifications = new List<string?>();
        var catalogNotifications = new List<string?>();

        service.PropertyChanged += (_, e) => serviceNotifications.Add(e.PropertyName);
        service.Text.PropertyChanged += (_, e) => catalogNotifications.Add(e.PropertyName);

        service.CurrentLanguage = AppLanguage.German;

        Assert.Equal(AppLanguage.German, service.CurrentLanguage);
        Assert.Equal(AppLanguage.German, store.SavedLanguage);
        Assert.Equal("Einstellungen", service.Text[UiTextKey.SettingsTitle]);
        Assert.Contains(nameof(ITranslationService.CurrentLanguage), serviceNotifications);
        Assert.Contains(nameof(ITranslationService.Text), serviceNotifications);
        Assert.Contains("Item[]", catalogNotifications);
        Assert.Contains(string.Empty, catalogNotifications);
        Assert.DoesNotContain(
            catalogNotifications,
            notification => notification is not "Item[]"
                && notification?.StartsWith("Item[", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void NamedPlaceholdersAreReplacedWithoutChangingTechnicalValues()
    {
        var service = CreateService();

        var message = service.Get(
            UiTextKey.UpdateAvailable,
            new Dictionary<string, object?> { ["version"] = "3.26.0" });

        Assert.Equal("Update 3.26.0 is available.", message);
    }

    [Fact]
    public void PlaceholderValuesAreNotReprocessed()
    {
        var catalog = new TranslationCatalog(new Dictionary<UiTextKey, string>
        {
            [UiTextKey.UpdateAvailable] = "{version} / {status}"
        });

        var message = catalog.Get(
            UiTextKey.UpdateAvailable,
            new Dictionary<string, object?>
            {
                ["version"] = "{status}",
                ["status"] = "Ready"
            });

        Assert.Equal("{status} / Ready", message);
    }

    [Fact]
    public void ShellCatalogContainsRequiredSemanticEnglishAndGermanEntries()
    {
        var repositoryRoot = FindRepositoryRoot();
        var englishCatalog = LoadCatalog(Path.Combine(
            repositoryRoot,
            "src",
            "PhoneDesk.Presentation",
            "Resources",
            "Localization",
            "Strings.en.json"));
        var germanCatalog = LoadCatalog(Path.Combine(
            repositoryRoot,
            "src",
            "PhoneDesk.Presentation",
            "Resources",
            "Localization",
            "Strings.de.json"));

        Assert.Equal("Readiness Check", englishCatalog[UiTextKey.MainNavigationReadinessCheck]);
        Assert.Equal("Configuration", englishCatalog[UiTextKey.MainNavigationConfiguration]);
        Assert.Equal("Tenant Report", englishCatalog[UiTextKey.MainNavigationTenantReport]);
        Assert.Equal("Audit Log", englishCatalog[UiTextKey.MainNavigationAuditLog]);
        Assert.Equal("Guided Setup", englishCatalog[UiTextKey.MainNavigationGuidedSetup]);
        Assert.Equal("Language", englishCatalog[UiTextKey.SettingsLanguageSectionTitle]);
        Assert.Equal("English", englishCatalog[UiTextKey.SettingsLanguageEnglish]);
        Assert.Equal("Deutsch", englishCatalog[UiTextKey.SettingsLanguageGerman]);
        Assert.Equal("Open", englishCatalog[UiTextKey.MainActionOpenAuditLogFolder]);
        Assert.Equal("Install update", englishCatalog[UiTextKey.UpdateInstallAction]);
        Assert.Equal("View release", englishCatalog[UiTextKey.UpdateViewReleaseAction]);

        Assert.Equal("Bereitschaftsprüfung", germanCatalog[UiTextKey.MainNavigationReadinessCheck]);
        Assert.Equal("Konfiguration", germanCatalog[UiTextKey.MainNavigationConfiguration]);
        Assert.Equal("Mandantenbericht", germanCatalog[UiTextKey.MainNavigationTenantReport]);
        Assert.Equal("Audit-Protokoll", germanCatalog[UiTextKey.MainNavigationAuditLog]);
        Assert.Equal("Geführte Einrichtung", germanCatalog[UiTextKey.MainNavigationGuidedSetup]);
        Assert.Equal("Sprache", germanCatalog[UiTextKey.SettingsLanguageSectionTitle]);
        Assert.Equal("English", germanCatalog[UiTextKey.SettingsLanguageEnglish]);
        Assert.Equal("Deutsch", germanCatalog[UiTextKey.SettingsLanguageGerman]);
        Assert.Equal("Öffnen", germanCatalog[UiTextKey.MainActionOpenAuditLogFolder]);
        Assert.Equal("Aktualisierung installieren", germanCatalog[UiTextKey.UpdateInstallAction]);
        Assert.Equal("Release öffnen", germanCatalog[UiTextKey.UpdateViewReleaseAction]);
    }

    [Fact]
    public void MissingCatalogKeyThrowsClearException()
    {
        var service = new TranslationService(
            new InMemoryUserPreferencesStore(),
            new Dictionary<AppLanguage, IReadOnlyDictionary<UiTextKey, string>>
            {
                [AppLanguage.English] = new Dictionary<UiTextKey, string>
                {
                    [UiTextKey.SettingsTitle] = "Settings"
                },
                [AppLanguage.German] = new Dictionary<UiTextKey, string>
                {
                    [UiTextKey.SettingsTitle] = "Einstellungen"
                }
            });

        var exception = Assert.Throws<KeyNotFoundException>(() => _ = service.Text[UiTextKey.UpdateAvailable]);

        Assert.Contains(nameof(UiTextKey.UpdateAvailable), exception.Message);
    }

    private static TranslationService CreateService(AppLanguage? preference = AppLanguage.English)
        => CreateService(new InMemoryUserPreferencesStore(preference));

    private static TranslationService CreateService(InMemoryUserPreferencesStore store)
        => new(
            store,
            new Dictionary<AppLanguage, IReadOnlyDictionary<UiTextKey, string>>
            {
                [AppLanguage.English] = new Dictionary<UiTextKey, string>
                {
                    [UiTextKey.SettingsTitle] = "Settings",
                    [UiTextKey.UpdateAvailable] = "Update {version} is available."
                },
                [AppLanguage.German] = new Dictionary<UiTextKey, string>
                {
                    [UiTextKey.SettingsTitle] = "Einstellungen",
                    [UiTextKey.UpdateAvailable] = "Update {version} ist verfügbar."
                }
            });

    private sealed class InMemoryUserPreferencesStore(AppLanguage? initialLanguage = null) : IUserPreferencesStore
    {
        public AppLanguage? SavedLanguage { get; private set; } = initialLanguage;

        public AppLanguage? LoadLanguage() => SavedLanguage;

        public void SaveLanguage(AppLanguage language) => SavedLanguage = language;
    }

    private static Dictionary<UiTextKey, string> LoadCatalog(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(
                property => Enum.Parse<UiTextKey>(property.Name, ignoreCase: false),
                property => property.Value.GetString() ?? string.Empty);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PhoneDesk.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
