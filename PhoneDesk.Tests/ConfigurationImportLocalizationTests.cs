using System.Text.Json;
using PhoneDesk.Localization;

namespace PhoneDesk.Tests
{
    public sealed class ConfigurationImportLocalizationTests
    {
        [Theory]
        [InlineData("en")]
        [InlineData("de")]
        public void ImportFailureIncludesValidationReason(string language)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PhoneDesk.slnx")))
            {
                directory = directory.Parent;
            }
            Assert.NotNull(directory);
            var path = Path.Combine(directory.FullName, "src", "PhoneDesk.Presentation",
                "Resources", "Localization", "Strings." + language + ".json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var template = document.RootElement.GetProperty("VariablesLoadFailedMessage").GetString();
            Assert.NotNull(template);
            var entries = new Dictionary<UiTextKey, string>();
            entries.Add(UiTextKey.VariablesLoadFailedMessage, template);
            var catalog = new TranslationCatalog(entries);
            const string reason = "Unsupported schema version: 99";
            var parameters = new Dictionary<string, object?>();
            parameters.Add("error", reason);
            Assert.Contains(reason, catalog.Get(UiTextKey.VariablesLoadFailedMessage, parameters));
        }
    }
}
