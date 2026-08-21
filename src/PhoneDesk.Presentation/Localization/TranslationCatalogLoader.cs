using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Platform;

namespace PhoneDesk.Localization;

public static class TranslationCatalogLoader
{
    public static IReadOnlyDictionary<UiTextKey, string> Load(Uri resourceUri)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);

        try
        {
            using var stream = new StandardAssetLoader().Open(resourceUri, null);
            return Load(stream, resourceUri.ToString());
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not load translation catalog '{resourceUri}'.", ex);
        }
    }

    public static IReadOnlyDictionary<UiTextKey, string> Load(Stream stream, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        try
        {
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Translation catalog '{sourceName}' must contain a JSON object.");
            }

            var values = new Dictionary<UiTextKey, string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!Enum.TryParse<UiTextKey>(property.Name, ignoreCase: false, out var key) ||
                    !Enum.IsDefined(key))
                {
                    throw new InvalidOperationException(
                        $"Translation catalog '{sourceName}' contains unknown key '{property.Name}'.");
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException(
                        $"Translation catalog '{sourceName}' key '{property.Name}' must contain a string value.");
                }

                values[key] = property.Value.GetString() ?? string.Empty;
            }

            return new ReadOnlyDictionary<UiTextKey, string>(values);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Translation catalog '{sourceName}' contains invalid JSON.", ex);
        }
    }

}
