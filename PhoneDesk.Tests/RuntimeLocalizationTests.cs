using System.Reflection;
using Moq;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests;

public sealed class RuntimeLocalizationTests
{
    [Fact]
    public async Task SwitchingLanguage_ChangesNextViewModelStatusMessage_AndPreservesInsertedTechnicalValue()
    {
        var harness = new ViewModelTestHarness();
        harness.SetExecutionResult("SUCCESS: verified");

        var vm = new HolidaysViewModel(
            harness.PowerShellContextService.Object,
            harness.PowerShellCommandService.Object,
            harness.LoggingService.Object,
            harness.SessionManager.Object,
            harness.NavigationService.Object,
            harness.ErrorHandlingService.Object,
            harness.ValidationService.Object,
            harness.SharedStateService.Object,
            harness.DialogService.Object,
            translationService: harness.TranslationService);

        vm.AutoAttendantName = "aa-Contoso";

        await vm.VerifyAutoAttendantCommand.ExecuteAsync(null);
        Assert.Equal(
            "Auto attendant 'aa-Contoso' verified successfully and is ready for holiday configuration",
            vm.StatusMessage);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        await vm.VerifyAutoAttendantCommand.ExecuteAsync(null);
        Assert.Equal(
            "Automatische Telefonzentrale 'aa-Contoso' wurde erfolgreich geprüft und ist für die Feiertagskonfiguration bereit.",
            vm.StatusMessage);
    }

    [Fact]
    public async Task ErrorHandlingService_LocalizesTitlesAndWrappers_AndPreservesCommandAndError()
    {
        var harness = new ViewModelTestHarness();
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowMessageAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        var service = new ErrorHandlingService(
            harness.LoggingService.Object,
            dialog.Object,
            harness.TranslationService);

        await service.HandlePowerShellError("Get-Thing -Identity 42", "boom", "VerifyAutoAttendant");

        dialog.Verify(
            d => d.ShowMessageAsync(
                "PowerShell Error",
                "An error occurred while executing the PowerShell command.\n\nboom"),
            Times.Once);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        await service.HandlePowerShellError("Get-Thing -Identity 42", "boom", "VerifyAutoAttendant");

        dialog.Verify(
            d => d.ShowMessageAsync(
                "PowerShell-Fehler",
                "Beim Ausführen des PowerShell-Befehls ist ein Fehler aufgetreten.\n\nboom"),
            Times.Once);

        harness.LoggingService.Verify(
            l => l.Log(
                It.Is<string>(m =>
                    m.Contains("Get-Thing -Identity 42", StringComparison.Ordinal) &&
                    m.Contains("boom", StringComparison.Ordinal)),
                LogLevel.Error),
            Times.Exactly(2));
    }

    [Fact]
    public void DialogService_LocalizesFixedChrome_WithoutChangingScriptBody()
    {
        var harness = new ViewModelTestHarness();
        var service = new DialogService(harness.TranslationService);
        const string script = "Get-CsCallQueue -Identity cq-Contoso";

        var english = InspectScriptPreviewDialog(service, "Preview", script);
        Assert.Equal("Execute", english.PrimaryButtonText);
        Assert.Equal("Cancel", english.SecondaryButtonText);
        Assert.Equal("Review the PowerShell script that will be executed:", english.ChromeText);
        Assert.Equal("PowerShell Script", english.Watermark);
        Assert.Equal(script, english.ScriptBody);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        var german = InspectScriptPreviewDialog(service, "Vorschau", script);
        Assert.Equal("Ausführen", german.PrimaryButtonText);
        Assert.Equal("Abbrechen", german.SecondaryButtonText);
        Assert.Equal("Prüfen Sie das PowerShell-Skript, das ausgeführt wird:", german.ChromeText);
        Assert.Equal("PowerShell-Skript", german.Watermark);
        Assert.Equal(script, german.ScriptBody);
    }

    [Fact]
    public void RealCatalogs_ContainRuntimeLocalizationKeys()
    {
        var english = TranslationCatalogLoader.Load(new Uri("avares://PhoneDesk.Presentation/Resources/Localization/Strings.en.json"));
        var german = TranslationCatalogLoader.Load(new Uri("avares://PhoneDesk.Presentation/Resources/Localization/Strings.de.json"));

        Assert.Equal(
            "Auto attendant '{name}' verified successfully and is ready for holiday configuration",
            english[UiTextKey.HolidaysVerifyAutoAttendantSuccess]);
        Assert.Equal(
            "Automatische Telefonzentrale '{name}' wurde erfolgreich geprüft und ist für die Feiertagskonfiguration bereit.",
            german[UiTextKey.HolidaysVerifyAutoAttendantSuccess]);
        Assert.Equal(
            "An error occurred while executing the PowerShell command.\n\n{error}",
            english[UiTextKey.ErrorPowerShellMessage]);
        Assert.Equal(
            "Beim Ausführen des PowerShell-Befehls ist ein Fehler aufgetreten.\n\n{error}",
            german[UiTextKey.ErrorPowerShellMessage]);
    }

    private static InspectableDialogState InspectScriptPreviewDialog(DialogService service, string title, string script)
    {
        var method = typeof(DialogService).GetMethod(
            "CreateScriptPreviewDialogForTesting",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method!.Invoke(service, new object[] { title, script });
        Assert.NotNull(result);

        return new InspectableDialogState(
            ReadStringProperty(result!, nameof(InspectableDialogState.PrimaryButtonText)),
            ReadStringProperty(result!, nameof(InspectableDialogState.SecondaryButtonText)),
            ReadStringProperty(result!, nameof(InspectableDialogState.ChromeText)),
            ReadStringProperty(result!, nameof(InspectableDialogState.Watermark)),
            ReadStringProperty(result!, nameof(InspectableDialogState.ScriptBody)));
    }

    private static string ReadStringProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return Assert.IsType<string>(property!.GetValue(instance));
    }

    private sealed record InspectableDialogState(
        string PrimaryButtonText,
        string SecondaryButtonText,
        string ChromeText,
        string Watermark,
        string ScriptBody);
}
