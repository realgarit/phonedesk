using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using PhoneDesk.Localization;

namespace PhoneDesk.Converters
{
    /// <summary>
    /// Shows the name of a weekday in the language of the interface. The stored name stays English:
    /// the scripts and the configuration file find a day by it. The second bound value is any text
    /// of the catalog; it only makes the binding run again when the language changes.
    /// </summary>
    public class DayNameConverter : IMultiValueConverter
    {
        private static readonly Dictionary<string, UiTextKey> Keys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Monday"] = UiTextKey.DayMonday,
            ["Tuesday"] = UiTextKey.DayTuesday,
            ["Wednesday"] = UiTextKey.DayWednesday,
            ["Thursday"] = UiTextKey.DayThursday,
            ["Friday"] = UiTextKey.DayFriday,
            ["Saturday"] = UiTextKey.DaySaturday,
            ["Sunday"] = UiTextKey.DaySunday
        };

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            var name = values.Count > 0 ? values[0] as string : null;
            if (name is null)
            {
                return string.Empty;
            }

            return Keys.TryGetValue(name, out var key)
                   && Application.Current?.Resources["TranslationCatalog"] is TranslationCatalog catalog
                ? catalog[key]
                : name;
        }
    }
}
