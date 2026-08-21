using System.Collections.Generic;
using System.ComponentModel;

namespace PhoneDesk.Localization;

public sealed class TranslationCatalog : INotifyPropertyChanged
{
    private IReadOnlyDictionary<UiTextKey, string> _values;

    public TranslationCatalog(IReadOnlyDictionary<UiTextKey, string> values)
    {
        _values = values;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[UiTextKey key] => Get(key);

    public string Get(UiTextKey key, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (!_values.TryGetValue(key, out var value))
        {
            throw new KeyNotFoundException($"Missing translation for key '{key}'.");
        }

        if (parameters is null || parameters.Count == 0)
        {
            return value;
        }

        foreach (var parameter in parameters)
        {
            value = value.Replace(
                "{" + parameter.Key + "}",
                parameter.Value?.ToString() ?? string.Empty,
                StringComparison.Ordinal);
        }

        return value;
    }

    internal void Update(IReadOnlyDictionary<UiTextKey, string> values)
    {
        _values = values;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
