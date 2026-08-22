using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using PhoneDesk.Localization;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Services
{
    public class DialogService : IDialogService
    {
        private static readonly FontFamily MonospaceFont = new("Cascadia Code, Consolas, Courier New, monospace");
        private readonly ITranslationService? _translationService;

        public DialogService(ITranslationService? translationService = null)
        {
            _translationService = translationService;
        }

        private string GetText(
            UiTextKey key,
            string fallback,
            IReadOnlyDictionary<string, object?>? parameters = null)
            => _translationService?.Get(key, parameters) ?? FormatFallback(fallback, parameters);

        private static string FormatFallback(string template, IReadOnlyDictionary<string, object?>? parameters)
            => TranslationCatalog.FormatTemplate(template, parameters);

        private Window? GetMainWindow()
        {
            return Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
        }

        public async Task ShowMessageAsync(string title, string message)
        {
            var window = GetMainWindow();
            if (window != null)
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = GetText(UiTextKey.DialogOk, "OK"),
                    DefaultButton = ContentDialogButton.Primary
                };
                await dialog.ShowAsync(window);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"DialogService: Cannot show message dialog - window not available. Title: {title}");
            }
        }

        public async Task<bool> ShowConfirmationAsync(string title, string message)
        {
            var window = GetMainWindow();
            if (window != null)
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = GetText(UiTextKey.DialogOk, "OK"),
                    SecondaryButtonText = GetText(UiTextKey.DialogCancel, "Cancel"),
                    DefaultButton = ContentDialogButton.Primary
                };
                var result = await dialog.ShowAsync(window);
                return result == ContentDialogResult.Primary;
            }

            System.Diagnostics.Debug.WriteLine($"DialogService: Cannot show confirmation dialog - window not available. Title: {title}");
            return false;
        }

        public async Task<bool> ShowScriptPreviewAsync(string title, string script)
        {
            var window = GetMainWindow();
            if (window == null) return false;

            var state = CreateScriptPreviewDialogForTesting(title, script);
            var dialog = BuildScriptPreviewDialog(state, maxHeight: 400, minHeight: 200);

            var result = await dialog.ShowAsync(window);
            return result == ContentDialogResult.Primary;
        }

        public async Task<bool> ShowConfirmationWithPreviewAsync(string title, string message, string script)
        {
            var window = GetMainWindow();
            if (window == null) return false;

            var state = CreateConfirmationWithPreviewDialogForTesting(title, message, script);
            var dialog = BuildConfirmationWithPreviewDialog(state, maxHeight: 300, minHeight: 150);

            var result = await dialog.ShowAsync(window);
            return result == ContentDialogResult.Primary;
        }

        private ContentDialog BuildScriptPreviewDialog(InspectableDialogState state, double maxHeight, double minHeight)
        {
            var panel = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = state.ChromeText, TextWrapping = TextWrapping.Wrap },
                    CreateScriptViewer(state.ScriptBody, state.Watermark, maxHeight, minHeight)
                }
            };

            return new ContentDialog
            {
                Title = state.Title,
                Content = panel,
                PrimaryButtonText = state.PrimaryButtonText,
                SecondaryButtonText = state.SecondaryButtonText,
                DefaultButton = ContentDialogButton.Secondary
            };
        }

        private ContentDialog BuildConfirmationWithPreviewDialog(ConfirmationDialogState state, double maxHeight, double minHeight)
        {
            var panel = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = state.Message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.OrangeRed,
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock { Text = state.ChromeText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) },
                    CreateScriptViewer(state.ScriptBody, state.Watermark, maxHeight, minHeight)
                }
            };

            return new ContentDialog
            {
                Title = state.Title,
                Content = panel,
                PrimaryButtonText = state.PrimaryButtonText,
                SecondaryButtonText = state.SecondaryButtonText,
                DefaultButton = ContentDialogButton.Secondary
            };
        }

        private static ScrollViewer CreateScriptViewer(string script, string watermark, double maxHeight, double minHeight)
        {
            var textBox = new TextBox
            {
                Text = script,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = MonospaceFont,
                FontSize = 12,
                MaxHeight = maxHeight,
                MinHeight = minHeight,
                Watermark = watermark
            };

            return new ScrollViewer
            {
                Content = textBox,
                MaxHeight = maxHeight,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
        }

        private InspectableDialogState CreateScriptPreviewDialogForTesting(string title, string script)
            => new(
                title,
                GetText(UiTextKey.DialogExecute, "Execute"),
                GetText(UiTextKey.DialogCancel, "Cancel"),
                GetText(UiTextKey.DialogReviewPowerShellScript, "Review the PowerShell script that will be executed:"),
                GetText(UiTextKey.DialogPowerShellScriptWatermark, "PowerShell Script"),
                script);

        private ConfirmationDialogState CreateConfirmationWithPreviewDialogForTesting(string title, string message, string script)
            => new(
                title,
                message,
                GetText(UiTextKey.DialogConfirmAndExecute, "Confirm & Execute"),
                GetText(UiTextKey.DialogCancel, "Cancel"),
                GetText(UiTextKey.DialogScriptToBeExecuted, "Script to be executed:"),
                GetText(UiTextKey.DialogPowerShellScriptWatermark, "PowerShell Script"),
                script);

        private sealed record InspectableDialogState(
            string Title,
            string PrimaryButtonText,
            string SecondaryButtonText,
            string ChromeText,
            string Watermark,
            string ScriptBody);

        private sealed record ConfirmationDialogState(
            string Title,
            string Message,
            string PrimaryButtonText,
            string SecondaryButtonText,
            string ChromeText,
            string Watermark,
            string ScriptBody);
    }
}
