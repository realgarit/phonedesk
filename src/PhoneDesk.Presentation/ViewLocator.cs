using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using PhoneDesk.Localization;
using PhoneDesk.ViewModels;
using System.Collections.Generic;

namespace PhoneDesk
{
    /// <summary>
    /// VM-first view resolution: maps a ViewModel instance to its View by naming convention
    /// (…ViewModels.XxxViewModel → …Views.XxxView). Registered in App.axaml's DataTemplates so a
    /// ContentControl bound to a ViewModel renders the matching View, with the ViewModel as its
    /// DataContext. Replaces the previous per-View `Program.Services.GetService<…>()` service-locator.
    /// </summary>
    public class ViewLocator : IDataTemplate
    {
        private static string GetText(
            UiTextKey key,
            IReadOnlyDictionary<string, object?>? parameters = null)
        {
            if (Application.Current?.Resources["TranslationCatalog"] is TranslationCatalog catalog)
            {
                return catalog.Get(key, parameters);
            }

            return key.ToString();
        }

        public Control Build(object? data)
        {
            if (data is null)
                return new TextBlock { Text = GetText(UiTextKey.ViewLocatorNoViewModel) };

            var name = data.GetType().FullName!
                .Replace("ViewModels", "Views", StringComparison.Ordinal)
                .Replace("ViewModel", "View", StringComparison.Ordinal);

            var type = Type.GetType(name);
            if (type is not null)
                return (Control)Activator.CreateInstance(type)!;

            return new TextBlock
            {
                Text = GetText(
                    UiTextKey.ViewLocatorViewNotFound,
                    new Dictionary<string, object?> { ["name"] = name })
            };
        }

        public bool Match(object? data) => data is ViewModelBase;
    }
}
