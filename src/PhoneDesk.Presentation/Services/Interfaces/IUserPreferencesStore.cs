using PhoneDesk.Localization;

namespace PhoneDesk.Services.Interfaces;

public interface IUserPreferencesStore
{
    AppLanguage? LoadLanguage();

    void SaveLanguage(AppLanguage language);
}
