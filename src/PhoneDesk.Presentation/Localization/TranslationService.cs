using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Localization;

public sealed class TranslationService : ITranslationService
{
    private readonly IUserPreferencesStore _preferencesStore;
    private readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<UiTextKey, string>> _catalogs;
    private AppLanguage _currentLanguage;

    public TranslationService(
        IUserPreferencesStore preferencesStore,
        IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<UiTextKey, string>> catalogs)
    {
        _preferencesStore = preferencesStore;
        _catalogs = catalogs;

        if (!_catalogs.TryGetValue(AppLanguage.English, out var englishCatalog))
        {
            throw new InvalidOperationException("An English translation catalog is required.");
        }

        Text = new TranslationCatalog(new ReadOnlyDictionary<UiTextKey, string>(new Dictionary<UiTextKey, string>(englishCatalog)));

        var storedLanguage = NormalizeLanguage(_preferencesStore.LoadLanguage());
        _currentLanguage = storedLanguage;
        Text.Update(BuildEffectiveCatalog(storedLanguage));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppLanguage CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            var normalizedLanguage = NormalizeLanguage(value);
            if (_currentLanguage == normalizedLanguage)
            {
                return;
            }

            _currentLanguage = normalizedLanguage;
            Text.Update(BuildEffectiveCatalog(normalizedLanguage));
            _preferencesStore.SaveLanguage(normalizedLanguage);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public TranslationCatalog Text { get; }

    public string Get(UiTextKey key, IReadOnlyDictionary<string, object?>? parameters = null)
        => Text.Get(key, parameters);

    private AppLanguage NormalizeLanguage(AppLanguage? language)
        => language is { } candidate && Enum.IsDefined(candidate) ? candidate : AppLanguage.English;

    private IReadOnlyDictionary<UiTextKey, string> BuildEffectiveCatalog(AppLanguage language)
    {
        var englishCatalog = _catalogs[AppLanguage.English];
        _catalogs.TryGetValue(language, out var selectedCatalog);

        var effectiveCatalog = new Dictionary<UiTextKey, string>(englishCatalog);
        if (selectedCatalog is not null)
        {
            foreach (var entry in selectedCatalog)
            {
                effectiveCatalog[entry.Key] = entry.Value;
            }
        }

        return new ReadOnlyDictionary<UiTextKey, string>(effectiveCatalog);
    }
}
