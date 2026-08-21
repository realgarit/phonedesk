using System.ComponentModel;
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
                    [UiTextKey.UpdateAvailable] = "Update {version} ist verfugbar."
                }
            });

    private sealed class InMemoryUserPreferencesStore(AppLanguage? initialLanguage = null) : IUserPreferencesStore
    {
        public AppLanguage? SavedLanguage { get; private set; } = initialLanguage;

        public AppLanguage? LoadLanguage() => SavedLanguage;

        public void SaveLanguage(AppLanguage language) => SavedLanguage = language;
    }
}
