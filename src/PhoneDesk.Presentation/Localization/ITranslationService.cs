using System.Collections.Generic;
using System.ComponentModel;

namespace PhoneDesk.Localization;

public interface ITranslationService : INotifyPropertyChanged
{
    AppLanguage CurrentLanguage { get; set; }

    TranslationCatalog Text { get; }

    string Get(UiTextKey key, IReadOnlyDictionary<string, object?>? parameters = null);
}
