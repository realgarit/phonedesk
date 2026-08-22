using System.Reflection;
using Moq;
using PhoneDesk.Models;
using PhoneDesk.Localization;
using PhoneDesk.Planning;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services.ScriptBuilders;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests;

public sealed class RuntimeLocalizationTests
{
    [Fact]
    public void SwitchingLanguage_LocalizesWizardRuntimeSurface_AndPreservesCustomer()
    {
        var harness = new ViewModelTestHarness();
        harness.SharedStateService.SetupGet(s => s.Variables).Returns(new PhoneManagerVariables
        {
            Customer = "contoso"
        });

        var vm = new WizardViewModel(
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

        Assert.Equal("Review Configuration", vm.StepTitle);
        Assert.Contains("Customer", vm.ReviewSummary, StringComparison.Ordinal);
        Assert.Contains("contoso", vm.ReviewSummary, StringComparison.Ordinal);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        Assert.Equal("Konfiguration prüfen", vm.StepTitle);
        Assert.Contains("Kunde", vm.ReviewSummary, StringComparison.Ordinal);
        Assert.Contains("contoso", vm.ReviewSummary, StringComparison.Ordinal);
        Assert.Equal("Konfiguration bestätigen", vm.ExecuteButtonText);
    }

    [Fact]
    public async Task SwitchingLanguage_LocalizesAutoAttendantCreateSuccess_AndPreservesName()
    {
        var harness = new ViewModelTestHarness();
        harness.SetExecutionResult("SUCCESS: created");
        harness.SharedStateService.SetupGet(s => s.AutoRefreshAfterOperations).Returns(false);
        harness.SharedStateService.SetupGet(s => s.Variables).Returns(new PhoneManagerVariables
        {
            Customer = "contoso",
            CustomerGroupName = "tenant",
            MsFallbackDomain = "@example.com",
            LanguageId = "en-US",
            TimeZoneId = "W. Europe Standard Time",
            CsAppAaId = "app-aa-123"
        });

        var vm = new AutoAttendantsViewModel(
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

        await vm.CreateAutoAttendantCommand.ExecuteAsync(null);
        Assert.Equal("Auto attendant 'aa-Contoso' created successfully.", vm.StatusMessage);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        await vm.CreateAutoAttendantCommand.ExecuteAsync(null);
        Assert.Equal("Automatische Telefonzentrale 'aa-Contoso' wurde erfolgreich erstellt.", vm.StatusMessage);
    }

    [Fact]
    public void SwitchingLanguage_LocalizesBulkPlanSummary_AndPreservesCounts()
    {
        var harness = new ViewModelTestHarness();
        var vm = new BulkOperationsViewModel(
            harness.PowerShellContextService.Object,
            harness.PowerShellCommandService.Object,
            harness.LoggingService.Object,
            harness.SessionManager.Object,
            harness.NavigationService.Object,
            harness.ErrorHandlingService.Object,
            harness.ValidationService.Object,
            harness.SharedStateService.Object,
            harness.DialogService.Object,
            new BulkOperationsScriptBuilder(harness.PowerShellCommandService.Object, CreateVerbatimSanitizer()),
            planBuilder: new DryRunPlanBuilder(harness.ValidationService.Object),
            translationService: harness.TranslationService);

        vm.CsvContent = "Customer,CustomerGroupName,MsFallbackDomain,RaaAnrName,LanguageId,TimeZoneId,UsageLocation,PhoneNumber,PhoneNumberType,OpeningHours1Start,OpeningHours1End,OpeningHours2Start,OpeningHours2End\n"
            + "contoso,hauptnummer,@contoso.onmicrosoft.com,haupt,de-DE,W. Europe Standard Time,CH,+41441234567,DirectRouting,08:00,12:00,13:00,17:00";

        vm.ParseCsvCommand.Execute(null);
        vm.GeneratePlanCommand.Execute(null);

        Assert.Equal("Plan generated: 0 valid, 1 invalid of 1 rows. Fix the issues or enable 'Skip invalid rows'.", vm.StatusMessage);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;
        vm.GeneratePlanCommand.Execute(null);

        Assert.Equal("Plan erstellt: 0 gültig, 1 ungültig von 1 Zeilen. Beheben Sie die Probleme oder aktivieren Sie 'Ungültige Zeilen überspringen'.", vm.StatusMessage);
    }

    [Fact]
    public async Task SwitchingLanguage_LocalizesDocumentationSuccess_AndCopyStatus()
    {
        var harness = new ViewModelTestHarness();
        var docBuilder = CreateDocumentationBuilder();
        harness.SetExecutionResult(string.Empty);

        var vm = new DocumentationViewModel(
            harness.PowerShellContextService.Object,
            harness.PowerShellCommandService.Object,
            harness.LoggingService.Object,
            harness.SessionManager.Object,
            harness.NavigationService.Object,
            harness.ErrorHandlingService.Object,
            harness.ValidationService.Object,
            harness.SharedStateService.Object,
            harness.DialogService.Object,
            docBuilder.Object,
            translationService: harness.TranslationService);

        await vm.ExportDocumentationCommand.ExecuteAsync(null);
        Assert.Equal("Documentation exported successfully. You can copy the text above.", vm.StatusMessage);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        await vm.CopyToClipboardCommand.ExecuteAsync(null);
        Assert.Equal("Zwischenablage ist nicht verfügbar.", vm.StatusMessage);
    }

    [Fact]
    public async Task SwitchingLanguage_LocalizesWizardStepCompletion_AndPreservesStepData()
    {
        var harness = new ViewModelTestHarness();
        harness.PowerShellCommandService
            .Setup(c => c.GetCreateM365GroupCommand(It.IsAny<string>()))
            .Returns("New-CsGroup ...");

        var vm = new WizardViewModel(
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

        await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);
        vm.GoToNextStepCommand.Execute(null);
        harness.SetExecutionResult("SUCCESS: created");

        await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);
        Assert.Equal("Step 2 completed: Create M365 Group", vm.StatusMessage);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;
        vm.RetryStepCommand.Execute(null);

        await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);
        Assert.Equal("Schritt 2 abgeschlossen: M365-Gruppe erstellen", vm.StatusMessage);
    }

    [Fact]
    public void SwitchingLanguage_PreservesWizardTechnicalScriptPreview()
    {
        var harness = new ViewModelTestHarness();
        var vm = new WizardViewModel(
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

        var englishScript = vm.StepScript;

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        Assert.Equal(englishScript, vm.StepScript);
    }

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
    public async Task SwitchingLanguage_LocalizesCallQueueLicenseStatus_AndDisplayedLogWrapper()
    {
        var harness = new ViewModelTestHarness();
        harness.SetExecutionResult("SUCCESS: license assigned");
        harness.SharedStateService.SetupGet(s => s.Variables).Returns(new PhoneDesk.Models.PhoneManagerVariables
        {
            Customer = "contoso",
            CustomerGroupName = "tenant",
            MsFallbackDomain = "@example",
            SkuId = "sku-123"
        });

        var vm = new CallQueuesViewModel(
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

        await vm.AssignLicenseCommand.ExecuteAsync(null);
        Assert.Equal(
            "License assigned to resource account 'racq-contoso-tenant@example' successfully.",
            vm.StatusMessage);
        harness.LoggingService.Verify(
            l => l.Log(
                "License assigned to resource account 'racq-contoso-tenant@example'.",
                LogLevel.Info),
            Times.Once);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        await vm.AssignLicenseCommand.ExecuteAsync(null);
        Assert.Equal(
            "Lizenz wurde dem Ressourcenkonto 'racq-contoso-tenant@example' erfolgreich zugewiesen.",
            vm.StatusMessage);
        harness.LoggingService.Verify(
            l => l.Log(
                "Lizenz wurde dem Ressourcenkonto 'racq-contoso-tenant@example' zugewiesen.",
                LogLevel.Info),
            Times.Once);
    }

    [Fact]
    public async Task SwitchingLanguage_LocalizesCallQueueGroupIdStatus_AndPreservesGroupId()
    {
        var harness = new ViewModelTestHarness();
        harness.SharedStateService.SetupGet(s => s.Variables).Returns(new PhoneManagerVariables
        {
            Customer = "contoso",
            CustomerGroupName = "tenant"
        });
        harness.SetExecutionResult("SUCCESS\nM365GROUPID:11111111-2222-3333-4444-555555555555");

        var vm = new CallQueuesViewModel(
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

        await vm.GetM365GroupIdCommand.ExecuteAsync(null);
        Assert.Equal(
            "M365 Group ID loaded and saved: 11111111-2222-3333-4444-555555555555",
            vm.StatusMessage);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        await vm.GetM365GroupIdCommand.ExecuteAsync(null);
        Assert.Equal(
            "M365-Gruppen-ID geladen und gespeichert: 11111111-2222-3333-4444-555555555555",
            vm.StatusMessage);
    }

    [Fact]
    public async Task SwitchingLanguage_LocalizesM365GroupCreateStatus_AndPreservesName()
    {
        var harness = new ViewModelTestHarness();
        harness.SetExecutionResult("Group created successfully");
        harness.SharedStateService.SetupGet(s => s.AutoRefreshAfterOperations).Returns(false);

        var vm = new M365GroupsViewModel(
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

        vm.NewGroupName = "ttgrp-contoso";

        await vm.CreateNewGroupCommand.ExecuteAsync(null);
        Assert.Equal("Group 'ttgrp-contoso' created successfully.", vm.GroupStatus);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        await vm.CreateNewGroupCommand.ExecuteAsync(null);
        Assert.Equal("Gruppe 'ttgrp-contoso' wurde erfolgreich erstellt.", vm.GroupStatus);
    }

    [Fact]
    public void SwitchingLanguage_LocalizesVariablesHolidaySeriesSaveLog_AndPreservesCount()
    {
        var harness = new ViewModelTestHarness();
        var variables = new PhoneManagerVariables();
        variables.HolidaySeries.Add(new HolidayEntry(new DateTime(2026, 12, 24), TimeSpan.Zero, "Christmas Eve"));
        variables.HolidaySeries.Add(new HolidayEntry(new DateTime(2026, 12, 25), TimeSpan.Zero, "Christmas Day"));
        harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);

        var vm = new VariablesViewModel(
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

        vm.SaveHolidaySeriesCommand.Execute(null);
        harness.LoggingService.Verify(
            l => l.Log("Saved holiday series with 2 holidays", LogLevel.Info),
            Times.Once);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        vm.SaveHolidaySeriesCommand.Execute(null);
        harness.LoggingService.Verify(
            l => l.Log("Feiertagsserie mit 2 Feiertagen gespeichert", LogLevel.Info),
            Times.Once);
    }

    [Fact]
    public void SwitchingLanguage_LocalizesMainWindowSettingsToggleLog()
    {
        var harness = new ViewModelTestHarness();
        var vm = new MainWindowViewModel(
            harness.PowerShellContextService.Object,
            harness.PowerShellCommandService.Object,
            harness.LoggingService.Object,
            harness.SessionManager.Object,
            harness.NavigationService.Object,
            harness.ErrorHandlingService.Object,
            harness.ValidationService.Object,
            harness.SharedStateService.Object,
            harness.DialogService.Object,
            harness.PageViewModelFactory.Object,
            harness.UpdateCheckService.Object,
            harness.UpdateInstallerService.Object,
            harness.BundledModuleVersionService.Object,
            translationService: harness.TranslationService);

        vm.ToggleSettingsCommand.Execute(null);
        harness.LoggingService.Verify(
            l => l.Log("Settings panel opened", LogLevel.Info),
            Times.Once);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        vm.ToggleSettingsCommand.Execute(null);
        harness.LoggingService.Verify(
            l => l.Log("Einstellungsbereich geschlossen", LogLevel.Info),
            Times.Once);
    }

    [Fact]
    public void SwitchingLanguage_LocalizesUpdateBanner_AndPreservesVersion()
    {
        var harness = new ViewModelTestHarness();
        var vm = new MainWindowViewModel(
            harness.PowerShellContextService.Object,
            harness.PowerShellCommandService.Object,
            harness.LoggingService.Object,
            harness.SessionManager.Object,
            harness.NavigationService.Object,
            harness.ErrorHandlingService.Object,
            harness.ValidationService.Object,
            harness.SharedStateService.Object,
            harness.DialogService.Object,
            harness.PageViewModelFactory.Object,
            harness.UpdateCheckService.Object,
            harness.UpdateInstallerService.Object,
            harness.BundledModuleVersionService.Object,
            translationService: harness.TranslationService);

        SetUpdateBannerState(vm, "Available", "3.26.0");
        vm.IsUpdateBannerVisible = true;
        Assert.Equal("Version 3.26.0 is available.", vm.UpdateBannerMessage);

        harness.TranslationService.CurrentLanguage = AppLanguage.German;

        Assert.Equal("Version 3.26.0 ist verfügbar.", vm.UpdateBannerMessage);
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
            "License assigned to resource account '{upn}' successfully.",
            english[UiTextKey.CallQueuesAssignLicenseSuccess]);
        Assert.Equal(
            "Lizenz wurde dem Ressourcenkonto '{upn}' erfolgreich zugewiesen.",
            german[UiTextKey.CallQueuesAssignLicenseSuccess]);
        Assert.Equal(
            "An error occurred while executing the PowerShell command.\n\n{error}",
            english[UiTextKey.ErrorPowerShellMessage]);
        Assert.Equal(
            "Beim Ausführen des PowerShell-Befehls ist ein Fehler aufgetreten.\n\n{error}",
            german[UiTextKey.ErrorPowerShellMessage]);
        Assert.Equal(
            "Auto attendant '{name}' created successfully.",
            english[UiTextKey.AutoAttendantsCreateAutoAttendantSuccess]);
        Assert.Equal(
            "Automatische Telefonzentrale '{name}' wurde erfolgreich erstellt.",
            german[UiTextKey.AutoAttendantsCreateAutoAttendantSuccess]);
        Assert.Equal(
            "Plan generated: {rows} rows, {objects} objects would be created or changed.",
            english[UiTextKey.BulkOperationsPlanGenerated]);
        Assert.Equal(
            "Plan erstellt: {rows} Zeilen, {objects} Objekte würden erstellt oder geändert.",
            german[UiTextKey.BulkOperationsPlanGenerated]);
        Assert.Equal(
            "Documentation exported successfully. You can copy the text above.",
            english[UiTextKey.DocumentationExportSuccess]);
        Assert.Equal(
            "Bericht wurde erfolgreich exportiert. Sie können den Text oben kopieren.",
            german[UiTextKey.DocumentationExportSuccess]);
        Assert.Equal(
            "Step {step} completed: {title}",
            english[UiTextKey.WizardStepCompletedStatus]);
        Assert.Equal(
            "Schritt {step} abgeschlossen: {title}",
            german[UiTextKey.WizardStepCompletedStatus]);
        Assert.Equal(
            "Saved holiday series with {count} holidays",
            english[UiTextKey.VariablesSaveHolidaySeriesLog]);
        Assert.Equal(
            "Feiertagsserie mit {count} Feiertagen gespeichert",
            german[UiTextKey.VariablesSaveHolidaySeriesLog]);
        Assert.Equal(
            "Settings panel {state}",
            english[UiTextKey.MainSettingsPanelStateLog]);
        Assert.Equal(
            "Einstellungsbereich {state}",
            german[UiTextKey.MainSettingsPanelStateLog]);
    }

    private static Mock<IDocumentationScriptBuilder> CreateDocumentationBuilder()
    {
        var mock = new Mock<IDocumentationScriptBuilder>();
        mock.Setup(d => d.GetExportTenantInfoCommand()).Returns("Get-TenantInfo");
        mock.Setup(d => d.GetExportResourceAccountsCommand()).Returns("Get-ResourceAccounts");
        mock.Setup(d => d.GetExportAutoAttendantsCommand()).Returns("Get-AutoAttendants");
        mock.Setup(d => d.GetExportCallQueuesCommand()).Returns("Get-CallQueues");
        mock.Setup(d => d.GetExportSchedulesCommand()).Returns("Get-Schedules");
        mock.Setup(d => d.GetExportPhoneNumbersCommand()).Returns("Get-PhoneNumbers");
        mock.Setup(d => d.GetExportVoiceUsersCommand()).Returns("Get-VoiceUsers");
        return mock;
    }

    private static IPowerShellSanitizationService CreateVerbatimSanitizer()
    {
        var mock = new Mock<IPowerShellSanitizationService>();
        mock.Setup(s => s.SanitizeString(It.IsAny<string>())).Returns<string>(x => x);
        mock.Setup(s => s.SanitizeIdentifier(It.IsAny<string>())).Returns<string>(x => x);
        return mock.Object;
    }

    private static void SetUpdateBannerState(MainWindowViewModel viewModel, string stateName, string version)
    {
        var stateType = typeof(MainWindowViewModel).GetNestedType("UpdateBannerState", BindingFlags.NonPublic);
        Assert.NotNull(stateType);
        var method = typeof(MainWindowViewModel).GetMethod(
            "SetUpdateBannerState",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var state = Enum.Parse(stateType!, stateName);
        method!.Invoke(viewModel, new object?[] { state, version, null });
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
