using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace PhoneDesk.Localization;

public sealed class TranslateExtension : MarkupExtension
{
    public UiTextKey Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var source = Application.Current?.Resources["TranslationCatalog"]
            ?? throw new InvalidOperationException("Application resource 'TranslationCatalog' is not available.");

        return new Binding
        {
            Source = source,
            Path = $"[{Key}]",
            Mode = BindingMode.OneWay
        };
    }
}
