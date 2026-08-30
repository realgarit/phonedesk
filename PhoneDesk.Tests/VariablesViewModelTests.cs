using Moq;
using PhoneDesk.Models;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Portability;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    public class VariablesViewModelTests
    {
        private static VariablesViewModel CreateViewModel(ViewModelTestHarness harness)
            => new VariablesViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object);

        private static VariablesViewModel CreateViewModel(
            ViewModelTestHarness harness,
            ITenantAsCodeService tenantAsCodeService,
            IPortabilityFileService portabilityFileService)
            => new VariablesViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object,
                tenantAsCodeService: tenantAsCodeService,
                portabilityFileService: portabilityFileService);

        // ─────────────────────── Construction ───────────────────────

        [Fact]
        public void Constructor_LogsPageLoadedAndSubscribesToVariables()
        {
            var harness = new ViewModelTestHarness();

            var vm = CreateViewModel(harness);

            harness.LoggingService.Verify(l => l.Log("Variables page loaded", LogLevel.Info), Times.Once);
            Assert.NotNull(vm.Variables);
        }

        [Fact]
        public void Constructor_PrefillsCallQueueTargetsWhenM365GroupIdAlreadySet()
        {
            var variables = new PhoneManagerVariables { M365GroupId = "group-123" };
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables);

            CreateViewModel(harness);

            // Either PhoneManagerVariables' own OnM365GroupIdChanged or the VM's PrefillCallQueueTargets
            // ends up filling these; the observable contract is that they get the group id once set.
            Assert.Equal("group-123", variables.CqOverflowActionTarget);
            Assert.Equal("group-123", variables.CqTimeoutActionTarget);
            Assert.Equal("group-123", variables.CqNoAgentActionTarget);
        }

        [Fact]
        public async Task ImportPreviewsChangesAndDoesNotApplyUntilConfirmed()
        {
            var current = new PhoneManagerVariables { Customer = "Contoso", CustomerGroupName = "Support" };
            var incoming = new PhoneManagerVariables { Customer = "Fabrikam", CustomerGroupName = "Support" };
            var service = new TenantAsCodeService();
            var json = service.SerializeConfiguration(PhoneManagerVariablesPortabilityMapper.ToDocument(incoming));
            var files = new Mock<IPortabilityFileService>();
            files.Setup(file => file.OpenJsonAsync(It.IsAny<string>()))
                .ReturnsAsync(new PortableTextFile("fabrikam.json", "C:\\fabrikam.json", json));
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupProperty(state => state.Variables, current);
            var vm = CreateViewModel(harness, service, files.Object);

            await vm.LoadVariablesFromFileCommand.ExecuteAsync(null);

            Assert.Same(current, harness.SharedStateService.Object.Variables);
            Assert.Equal("Contoso", harness.SharedStateService.Object.Variables.Customer);
            Assert.True(vm.ShowConfigurationImportPreview);
            Assert.Equal("fabrikam.json", vm.PendingConfigurationFileName);
            Assert.Contains(vm.PendingConfigurationChanges, change =>
                change.Path == "configuration.general.customer" &&
                change.CurrentValue == "Contoso" &&
                change.ImportedValue == "Fabrikam");

            vm.ApplyConfigurationImportCommand.Execute(null);

            Assert.Equal("Fabrikam", harness.SharedStateService.Object.Variables.Customer);
            Assert.False(vm.ShowConfigurationImportPreview);
            Assert.Empty(vm.PendingConfigurationChanges);
        }

        [Fact]
        public async Task ApplyImportPreservesExplicitlyEmptyCallQueueTargets()
        {
            var current = new PhoneManagerVariables { Customer = "Contoso" };
            var incoming = new PhoneManagerVariables
            {
                Customer = "Fabrikam",
                M365GroupId = "group-123",
                CqOverflowActionTarget = null,
                CqTimeoutActionTarget = null,
                CqNoAgentActionTarget = null,
            };
            var service = new TenantAsCodeService();
            var files = new Mock<IPortabilityFileService>();
            files.Setup(file => file.OpenJsonAsync(It.IsAny<string>()))
                .ReturnsAsync(new PortableTextFile(
                    "fabrikam.json",
                    "C:\\fabrikam.json",
                    service.SerializeConfiguration(PhoneManagerVariablesPortabilityMapper.ToDocument(incoming))));
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupProperty(state => state.Variables, current);
            var vm = CreateViewModel(harness, service, files.Object);

            await vm.LoadVariablesFromFileCommand.ExecuteAsync(null);
            vm.ApplyConfigurationImportCommand.Execute(null);

            var applied = harness.SharedStateService.Object.Variables;
            Assert.Equal("group-123", applied.M365GroupId);
            Assert.Null(applied.CqOverflowActionTarget);
            Assert.Null(applied.CqTimeoutActionTarget);
            Assert.Null(applied.CqNoAgentActionTarget);
        }

        [Fact]
        public async Task CancelImportLeavesCurrentVariablesUntouched()
        {
            var current = new PhoneManagerVariables { Customer = "Contoso" };
            var incoming = new PhoneManagerVariables { Customer = "Fabrikam" };
            var service = new TenantAsCodeService();
            var files = new Mock<IPortabilityFileService>();
            files.Setup(file => file.OpenJsonAsync(It.IsAny<string>()))
                .ReturnsAsync(new PortableTextFile(
                    "fabrikam.json",
                    "C:\\fabrikam.json",
                    service.SerializeConfiguration(PhoneManagerVariablesPortabilityMapper.ToDocument(incoming))));
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupProperty(state => state.Variables, current);
            var vm = CreateViewModel(harness, service, files.Object);

            await vm.LoadVariablesFromFileCommand.ExecuteAsync(null);
            vm.CancelConfigurationImportCommand.Execute(null);

            Assert.Same(current, harness.SharedStateService.Object.Variables);
            Assert.Equal("Contoso", current.Customer);
            Assert.False(vm.ShowConfigurationImportPreview);
            Assert.Empty(vm.PendingConfigurationChanges);
        }

        [Fact]
        public async Task ExportUsesSchemaServiceAndFileAbstraction()
        {
            var current = new PhoneManagerVariables { Customer = "Contoso" };
            var service = new TenantAsCodeService();
            var files = new Mock<IPortabilityFileService>();
            files.Setup(file => file.SaveJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync("C:\\phonedesk-config-contoso.json");
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupProperty(state => state.Variables, current);
            var vm = CreateViewModel(harness, service, files.Object);

            await vm.SaveVariablesToFileCommand.ExecuteAsync(null);

            files.Verify(file => file.SaveJsonAsync(
                It.IsAny<string>(),
                "phonedesk-config-Contoso.json",
                It.Is<string>(json =>
                    json.Contains("\"kind\": \"phonedesk.configuration\"", StringComparison.Ordinal) &&
                    !json.Contains("token", StringComparison.OrdinalIgnoreCase))), Times.Once);
        }

        // ─────────────────────── Holiday time picker ───────────────────────

        [Fact]
        public void OpenHolidayTimePicker_RoundsCurrentTimeAndShowsPicker()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.Variables.HolidayTime = new TimeSpan(9, 37, 0);

            vm.OpenHolidayTimePickerCommand.Execute(null);

            Assert.Equal(new TimeSpan(9, 30, 0), vm.SelectedHolidayTime);
            Assert.True(vm.ShowHolidayTimePicker);
        }

        [Fact]
        public void CloseHolidayTimePicker_HidesPicker()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowHolidayTimePicker = true;

            vm.CloseHolidayTimePickerCommand.Execute(null);

            Assert.False(vm.ShowHolidayTimePicker);
        }

        [Fact]
        public void SaveHolidayTime_WithSelectedTime_UpdatesVariablesAndClosesPicker()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.SelectedHolidayTime = new TimeSpan(14, 15, 0);
            vm.ShowHolidayTimePicker = true;

            vm.SaveHolidayTimeCommand.Execute(null);

            Assert.Equal(new TimeSpan(14, 15, 0), vm.Variables.HolidayTime);
            Assert.False(vm.ShowHolidayTimePicker);
        }

        [Fact]
        public void SaveHolidayTime_WithoutSelectedTime_LeavesHolidayTimeUnchanged()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            var originalTime = vm.Variables.HolidayTime;
            vm.SelectedHolidayTime = null;
            vm.ShowHolidayTimePicker = true;

            vm.SaveHolidayTimeCommand.Execute(null);

            Assert.Equal(originalTime, vm.Variables.HolidayTime);
            Assert.False(vm.ShowHolidayTimePicker);
        }

        // ─────────────────────── Holiday series manager ───────────────────────

        [Fact]
        public void OpenHolidaySeriesManager_SnapshotsExistingSeriesAndShowsManager()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.Variables.HolidaySeries.Add(new HolidayEntry(new DateTime(2026, 1, 1), new TimeSpan(9, 0, 0), "New Year"));

            vm.OpenHolidaySeriesManagerCommand.Execute(null);

            Assert.True(vm.ShowHolidaySeriesManager);
            var snapshot = Assert.Single(vm.OriginalHolidaySeries);
            // OpenHolidaySeriesManager snapshots via the (date, time) constructor, so Name is intentionally
            // not carried over into the restore-on-cancel snapshot.
            Assert.Equal(new DateTime(2026, 1, 1), snapshot.Date);
            Assert.Equal(new TimeSpan(9, 0, 0), snapshot.Time);
        }

        [Fact]
        public void CloseHolidaySeriesManager_HidesManagerAndClearsEditingHoliday()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowHolidaySeriesManager = true;
            vm.EditingHoliday = new HolidayEntry();

            vm.CloseHolidaySeriesManagerCommand.Execute(null);

            Assert.False(vm.ShowHolidaySeriesManager);
            Assert.Null(vm.EditingHoliday);
        }

        [Fact]
        public void CancelHolidaySeriesManager_RestoresOriginalSeries()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.Variables.HolidaySeries.Add(new HolidayEntry(new DateTime(2026, 1, 1), new TimeSpan(9, 0, 0), "Original"));
            vm.OpenHolidaySeriesManagerCommand.Execute(null); // snapshots "Original" into OriginalHolidaySeries

            // Simulate unsaved edits: add a second holiday that should be discarded on cancel.
            vm.Variables.HolidaySeries.Add(new HolidayEntry(new DateTime(2026, 2, 2), new TimeSpan(10, 0, 0), "Unsaved"));

            vm.CancelHolidaySeriesManagerCommand.Execute(null);

            var restored = Assert.Single(vm.Variables.HolidaySeries);
            // Restored from the (date, time)-only snapshot, so Name is not preserved (see snapshot test above).
            Assert.Equal(new DateTime(2026, 1, 1), restored.Date);
            Assert.Equal(new TimeSpan(9, 0, 0), restored.Time);
            Assert.False(vm.ShowHolidaySeriesManager);
        }

        [Fact]
        public void SaveHolidaySeries_LogsAndClosesManager()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowHolidaySeriesManager = true;

            vm.SaveHolidaySeriesCommand.Execute(null);

            Assert.False(vm.ShowHolidaySeriesManager);
            harness.LoggingService.Verify(l => l.Log(It.Is<string>(m => m.Contains("Saved holiday series")), LogLevel.Info), Times.Once);
        }

        // ─────────────────────── Add / Edit / Remove holiday ───────────────────────

        [Fact]
        public void AddHoliday_ResetsStateAndOpensEditDialog()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.AddHolidayCommand.Execute(null);

            Assert.Null(vm.EditingHoliday);
            Assert.Equal(new TimeSpan(0, 0, 0), vm.NewHolidayTime);
            Assert.True(vm.ShowEditHolidayDialog);
        }

        [Fact]
        public void EditHoliday_WithHoliday_PopulatesDialogState()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            var holiday = new HolidayEntry(new DateTime(2026, 5, 1), new TimeSpan(11, 37, 0), "May Day")
            {
                EndDate = new DateTime(2026, 5, 2),
                EndTime = new TimeSpan(8, 15, 0)
            };

            vm.EditHolidayCommand.Execute(holiday);

            Assert.Same(holiday, vm.EditingHoliday);
            Assert.Equal(new TimeSpan(11, 30, 0), vm.SelectedEditHolidayTime);
            Assert.True(vm.DialogHasEndDate);
            Assert.Equal(new DateTime(2026, 5, 2), vm.DialogEndDate);
            Assert.Equal(new TimeSpan(8, 15, 0), vm.SelectedEditEndTime);
            Assert.True(vm.ShowEditHolidayDialog);
        }

        [Fact]
        public void EditHoliday_WithNullHoliday_DoesNotThrowOrOpenDialog()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            var ex = Record.Exception(() => vm.EditHolidayCommand.Execute(null));

            Assert.Null(ex);
            Assert.False(vm.ShowEditHolidayDialog);
        }

        [Fact]
        public void CancelEditHoliday_ResetsDialogState()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowEditHolidayDialog = true;
            vm.EditingHoliday = new HolidayEntry();
            vm.SelectedEditHolidayTime = new TimeSpan(1, 0, 0);
            vm.SelectedEditEndTime = new TimeSpan(2, 0, 0);
            vm.DialogHasEndDate = true;

            vm.CancelEditHolidayCommand.Execute(null);

            Assert.False(vm.ShowEditHolidayDialog);
            Assert.Null(vm.EditingHoliday);
            Assert.Null(vm.SelectedEditHolidayTime);
            Assert.Null(vm.SelectedEditEndTime);
            Assert.False(vm.DialogHasEndDate);
        }

        [Fact]
        public void SaveEditHoliday_NewHoliday_AddsToSeries()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.EditingHoliday = null;
            vm.NewHolidayDate = new DateTime(2026, 12, 25);
            vm.SelectedEditHolidayTime = new TimeSpan(9, 0, 0);
            vm.DialogHasEndDate = false;

            vm.SaveEditHolidayCommand.Execute(null);

            var added = Assert.Single(vm.Variables.HolidaySeries);
            Assert.Equal(new DateTime(2026, 12, 25), added.Date);
            Assert.Equal(new TimeSpan(9, 0, 0), added.Time);
            Assert.Null(added.EndDate);
            Assert.False(vm.ShowEditHolidayDialog);
        }

        [Fact]
        public void SaveEditHoliday_ExistingHoliday_UpdatesInPlace()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            var holiday = new HolidayEntry(new DateTime(2026, 3, 3), new TimeSpan(6, 0, 0), "Old");
            vm.Variables.HolidaySeries.Add(holiday);
            vm.EditingHoliday = holiday;
            vm.SelectedEditHolidayTime = new TimeSpan(18, 45, 0);
            vm.DialogHasEndDate = true;
            vm.DialogEndDate = new DateTime(2026, 3, 4);
            vm.SelectedEditEndTime = new TimeSpan(2, 0, 0);

            vm.SaveEditHolidayCommand.Execute(null);

            Assert.Single(vm.Variables.HolidaySeries);
            Assert.Equal(new TimeSpan(18, 45, 0), holiday.Time);
            Assert.Equal(new DateTime(2026, 3, 4), holiday.EndDate);
            Assert.Equal(new TimeSpan(2, 0, 0), holiday.EndTime);
        }

        [Fact]
        public void RemoveHoliday_RemovesMatchingEntry()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            var holiday = new HolidayEntry(new DateTime(2026, 1, 1), new TimeSpan(9, 0, 0), "New Year");
            vm.Variables.HolidaySeries.Add(holiday);

            vm.RemoveHolidayCommand.Execute(holiday);

            Assert.Empty(vm.Variables.HolidaySeries);
        }

        [Fact]
        public void RemoveHoliday_WithNull_DoesNotThrow()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            var ex = Record.Exception(() => vm.RemoveHolidayCommand.Execute(null));

            Assert.Null(ex);
        }

        [Fact]
        public void DeleteAllHolidays_WithEntries_ClearsSeries()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.Variables.HolidaySeries.Add(new HolidayEntry(new DateTime(2026, 1, 1), new TimeSpan(9, 0, 0)));
            vm.Variables.HolidaySeries.Add(new HolidayEntry(new DateTime(2026, 2, 1), new TimeSpan(9, 0, 0)));

            vm.DeleteAllHolidaysCommand.Execute(null);

            Assert.Empty(vm.Variables.HolidaySeries);
        }

        [Fact]
        public void DeleteAllHolidays_WithEmptySeries_IsNoOp()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            var ex = Record.Exception(() => vm.DeleteAllHolidaysCommand.Execute(null));

            Assert.Null(ex);
            Assert.Empty(vm.Variables.HolidaySeries);
        }

        // ─────────────────────── Predefined holidays wizard ───────────────────────

        [Fact]
        public void OpenPredefinedHolidaysWizard_ResetsSelectionAndShowsWizard()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.SelectedCanton = "Zürich";
            vm.SelectedBezirk = "Aarau (Brugg/Kulm/Lenzburg/Zofingen/Baden - nur Bergdietikon)";

            vm.OpenPredefinedHolidaysWizardCommand.Execute(null);

            Assert.Equal("Switzerland", vm.SelectedCountry);
            Assert.Null(vm.SelectedCanton);
            Assert.Null(vm.SelectedBezirk);
            Assert.True(vm.ShowPredefinedHolidaysWizard);
        }

        [Fact]
        public void CancelPredefinedHolidaysWizard_HidesWizard()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.ShowPredefinedHolidaysWizard = true;

            vm.CancelPredefinedHolidaysWizardCommand.Execute(null);

            Assert.False(vm.ShowPredefinedHolidaysWizard);
        }

        [Fact]
        public void ShowAndCloseAargauInfo_TogglesDialogFlag()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.ShowAargauInfoCommand.Execute(null);
            Assert.True(vm.ShowAargauInfoDialog);

            vm.CloseAargauInfoCommand.Execute(null);
            Assert.False(vm.ShowAargauInfoDialog);
        }

        [Theory]
        [InlineData("France", null, null, 2026, true)]   // non-Switzerland: only year range matters
        [InlineData("France", null, null, 2020, false)]  // year out of range
        [InlineData("Switzerland", "Aargau", null, 2026, false)] // Aargau needs a Bezirk
        [InlineData("Switzerland", "Aargau", "Aarau (Brugg/Kulm/Lenzburg/Zofingen/Baden - nur Bergdietikon)", 2026, true)]
        [InlineData("Switzerland", "Zürich", null, 2026, true)]  // non-Aargau canton only needs canton + year
        [InlineData("Switzerland", null, null, 2026, false)]     // Switzerland without any canton
        public void CanApplyPredefinedSelection_ReflectsCountryCantonAndYear(
            string country, string? canton, string? bezirk, int year, bool expected)
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.SelectedCountry = country;
            vm.SelectedCanton = canton;
            vm.SelectedBezirk = bezirk;
            vm.SelectedYear = year;

            Assert.Equal(expected, vm.CanApplyPredefinedSelection);
        }

        [Fact]
        public void IsSwitzerlandSelected_And_IsAargauSelected_ReflectSelection()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.SelectedCountry = "Switzerland";
            vm.SelectedCanton = "Aargau";
            Assert.True(vm.IsSwitzerlandSelected);
            Assert.True(vm.IsAargauSelected);

            vm.SelectedCountry = "France";
            vm.SelectedCanton = "Zürich";
            Assert.False(vm.IsSwitzerlandSelected);
            Assert.False(vm.IsAargauSelected);
        }

        [Fact]
        public void ApplyPredefinedHolidays_Switzerland_AddsHolidaysAndClosesWizard()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.SelectedCountry = "Switzerland";
            vm.SelectedCanton = "Aargau";
            vm.SelectedBezirk = "Aarau (Brugg/Kulm/Lenzburg/Zofingen/Baden - nur Bergdietikon)";
            vm.SelectedYear = 2026;
            vm.ShowPredefinedHolidaysWizard = true;

            vm.ApplyPredefinedHolidaysCommand.Execute(null);

            Assert.NotEmpty(vm.Variables.HolidaySeries);
            Assert.All(vm.Variables.HolidaySeries, h => Assert.Equal(2026, h.Date.Year));
            Assert.False(vm.ShowPredefinedHolidaysWizard);
        }

        [Fact]
        public void ApplyPredefinedHolidays_NonSwitzerland_AddsNoHolidays()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            vm.SelectedCountry = "France";
            vm.SelectedYear = 2026;
            vm.ShowPredefinedHolidaysWizard = true;

            vm.ApplyPredefinedHolidaysCommand.Execute(null);

            Assert.Empty(vm.Variables.HolidaySeries);
            Assert.False(vm.ShowPredefinedHolidaysWizard);
        }

        // ─────────────────────── Call Queue / Auto Attendant configuration dialogs ───────────────────────

        [Fact]
        public void OpenCancelSaveCallQueueConfiguration_TogglesDialogFlag()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.OpenCallQueueConfigurationDialogCommand.Execute(null);
            Assert.True(vm.ShowCallQueueConfigurationDialog);

            vm.CancelCallQueueConfigurationCommand.Execute(null);
            Assert.False(vm.ShowCallQueueConfigurationDialog);

            vm.ShowCallQueueConfigurationDialog = true;
            vm.SaveCallQueueConfigurationCommand.Execute(null);
            Assert.False(vm.ShowCallQueueConfigurationDialog);
        }

        [Fact]
        public void OpenCancelSaveAutoAttendantConfiguration_TogglesDialogFlag()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.OpenAutoAttendantConfigurationDialogCommand.Execute(null);
            Assert.True(vm.ShowAutoAttendantConfigurationDialog);

            vm.CancelAutoAttendantConfigurationCommand.Execute(null);
            Assert.False(vm.ShowAutoAttendantConfigurationDialog);

            vm.ShowAutoAttendantConfigurationDialog = true;
            vm.SaveAutoAttendantConfigurationCommand.Execute(null);
            Assert.False(vm.ShowAutoAttendantConfigurationDialog);
        }

        // ─────────────────────── Conditional visibility properties ───────────────────────

        [Theory]
        [InlineData("AudioFile", true, false)]
        [InlineData("TextToSpeech", false, true)]
        [InlineData("None", false, false)]
        public void GreetingVisibility_ReflectsCqGreetingType(string greetingType, bool expectAudio, bool expectTts)
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.Variables.CqGreetingType = greetingType;

            Assert.Equal(expectAudio, vm.ShowGreetingAudioFile);
            Assert.Equal(expectTts, vm.ShowGreetingTextToSpeech);
        }

        [Theory]
        [InlineData("TransferToTarget", true)]
        [InlineData("TransferToVoicemail", true)]
        [InlineData("Disconnect", false)]
        public void ShowOverflowTarget_ReflectsCqOverflowAction(string action, bool expected)
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.Variables.CqOverflowAction = action;

            Assert.Equal(expected, vm.ShowOverflowTarget);
        }

        [Fact]
        public void ShowOverflowVoicemailGreeting_RequiresVoicemailActionAndGuidTarget()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.Variables.CqOverflowAction = "TransferToVoicemail";
            vm.Variables.CqOverflowActionTarget = "not-a-guid";
            Assert.False(vm.ShowOverflowVoicemailGreeting);

            vm.Variables.CqOverflowActionTarget = Guid.NewGuid().ToString();
            Assert.True(vm.ShowOverflowVoicemailGreeting);

            vm.Variables.CqOverflowAction = "Disconnect";
            Assert.False(vm.ShowOverflowVoicemailGreeting);
        }

        [Theory]
        [InlineData("AudioFile", true, false)]
        [InlineData("TextToSpeech", false, true)]
        public void AaDefaultGreetingVisibility_ReflectsAaDefaultGreetingType(string greetingType, bool expectAudio, bool expectTts)
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.Variables.AaDefaultGreetingType = greetingType;

            Assert.Equal(expectAudio, vm.ShowAaDefaultGreetingAudioFile);
            Assert.Equal(expectTts, vm.ShowAaDefaultGreetingTextToSpeech);
        }

        [Fact]
        public void CqPropertyChange_RaisesVisibilityPropertyChangedOnViewModel()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            var raisedProperties = new List<string?>();
            vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

            vm.Variables.CqGreetingType = "AudioFile";

            Assert.Contains(nameof(vm.ShowGreetingAudioFile), raisedProperties);
        }

        [Fact]
        public void AaPropertyChange_RaisesVisibilityPropertyChangedOnViewModel()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            var raisedProperties = new List<string?>();
            vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

            vm.Variables.AaDefaultAction = "TransferToTarget";

            Assert.Contains(nameof(vm.ShowAaDefaultTarget), raisedProperties);
        }

        [Fact]
        public void M365GroupIdChange_PrefillsEmptyCallQueueTargets()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.Variables.M365GroupId = "new-group-id";

            Assert.Equal("new-group-id", vm.Variables.CqOverflowActionTarget);
            Assert.Equal("new-group-id", vm.Variables.CqTimeoutActionTarget);
            Assert.Equal("new-group-id", vm.Variables.CqNoAgentActionTarget);
        }

        // ─────────────────────── Audio file selection (no Avalonia host in tests) ───────────────────────

        [Fact]
        public async Task SelectGreetingAudioFile_NoAvaloniaApp_DoesNotThrowOrImport()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            var ex = await Record.ExceptionAsync(() => vm.SelectGreetingAudioFileCommand.ExecuteAsync(null));

            Assert.Null(ex);
            harness.PowerShellCommandService.Verify(p => p.GetImportAudioFileCommand(It.IsAny<string>()), Times.Never);
            Assert.Null(vm.Variables.CqGreetingAudioFileId);
        }

        [Fact]
        public async Task SelectAaDefaultGreetingAudioFile_NoAvaloniaApp_DoesNotThrowOrImport()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            var ex = await Record.ExceptionAsync(() => vm.SelectAaDefaultGreetingAudioFileCommand.ExecuteAsync(null));

            Assert.Null(ex);
            harness.PowerShellCommandService.Verify(p => p.GetImportAudioFileCommand(It.IsAny<string>()), Times.Never);
        }
    }
}
