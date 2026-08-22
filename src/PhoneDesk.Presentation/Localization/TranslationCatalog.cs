using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace PhoneDesk.Localization;

public sealed class TranslationCatalog : INotifyPropertyChanged
{
    private static readonly Regex PlaceholderRegex = new(
        @"\{(?<name>[^{}]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        return FormatTemplate(value, parameters);
    }

    internal static string FormatTemplate(
        string template,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return template;
        }

        return PlaceholderRegex.Replace(
            template,
            match => parameters.TryGetValue(match.Groups["name"].Value, out var parameter)
                ? parameter?.ToString() ?? string.Empty
                : match.Value);
    }

    internal void Update(IReadOnlyDictionary<UiTextKey, string> values)
    {
        _values = values;
        foreach (var key in _values.Keys)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs($"Item[{key}]"));
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
