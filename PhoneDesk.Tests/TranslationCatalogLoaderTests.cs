using System.Collections.Generic;
using PhoneDesk.Localization;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests;

public sealed class TranslationCatalogLoaderTests
{
    [Fact]
    public void Load_ValidEmbeddedCatalogs_ReturnsTypedValues()
    {
        var english = TranslationCatalogLoader.Load(new Uri("avares://PhoneDesk.Presentation/Resources/Localization/Strings.en.json"));
        var german = TranslationCatalogLoader.Load(new Uri("avares://PhoneDesk.Presentation/Resources/Localization/Strings.de.json"));

        Assert.Equal("Settings", english[UiTextKey.SettingsTitle]);
        Assert.Equal("Einstellungen", german[UiTextKey.SettingsTitle]);
        Assert.Equal("Version {version} is available.", english[UiTextKey.UpdateAvailable]);
        Assert.Equal("Version {version} ist verfügbar.", german[UiTextKey.UpdateAvailable]);
    }

    [Fact]
    public void Load_MalformedJson_ReportsSourceContext()
    {
        using var stream = new MemoryStream("""{ "SettingsTitle": "Settings", """u8.ToArray());

        var exception = Assert.Throws<InvalidOperationException>(
            () => TranslationCatalogLoader.Load(stream, "MalformedCatalog"));

        Assert.Contains("MalformedCatalog", exception.Message);
    }

    [Fact]
    public void Load_UnknownKey_ThrowsInsteadOfIgnoringIt()
    {
        using var stream = new MemoryStream("""{ "UnknownKey": "Value" }"""u8.ToArray());

        var exception = Assert.Throws<InvalidOperationException>(
            () => TranslationCatalogLoader.Load(stream, "UnknownKeys"));

        Assert.Contains("UnknownKey", exception.Message);
    }

    [Fact]
    public void ViewModelTextSurface_TracksInjectedTranslationService()
    {
        var harness = new ViewModelTestHarness();
        var vm = new TranslationAwareViewModel(harness);

        Assert.Equal("Settings", vm.Text[UiTextKey.SettingsTitle]);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        Assert.Equal("Einstellungen", vm.Text[UiTextKey.SettingsTitle]);
    }

    private sealed class TranslationAwareViewModel : ViewModelBase
    {
        public TranslationAwareViewModel(ViewModelTestHarness harness)
            : base(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object,
                translationService: harness.TranslationService)
        {
        }
    }
}
