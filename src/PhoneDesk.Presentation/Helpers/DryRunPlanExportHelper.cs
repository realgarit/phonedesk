using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PhoneDesk.Localization;

namespace PhoneDesk.Helpers
{
    /// <summary>
    /// Shared helper for saving an exported dry-run plan to disk via Avalonia's platform
    /// <see cref="IStorageProvider"/>. Keeps the file-picker plumbing out of the view models and identical
    /// between the wizard and bulk-operations pages. Returns the saved file name, or null if the user
    /// cancelled or no window was available.
    /// </summary>
    internal static class DryRunPlanExportHelper
    {
        public static async Task<string?> SavePlanAsync(
            string content,
            string suggestedFileName,
            string extension,
            ITranslationService? translationService = null)
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (topLevel == null)
                return null;

            var isJson = string.Equals(extension, "json", StringComparison.OrdinalIgnoreCase);
            var typeName = isJson
                ? translationService?.Get(UiTextKey.DryRunPlanJsonFiles) ?? "JSON files"
                : translationService?.Get(UiTextKey.DryRunPlanCsvFiles) ?? "CSV files";
            var pattern = isJson ? "*.json" : "*.csv";

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = translationService?.Get(UiTextKey.DryRunPlanExportTitle) ?? "Export dry-run plan",
                DefaultExtension = extension,
                SuggestedFileName = suggestedFileName,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(typeName) { Patterns = new[] { pattern } }
                }
            });

            if (file == null)
                return null;

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new System.IO.StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(content);
            return file.Name;
        }
    }
}
