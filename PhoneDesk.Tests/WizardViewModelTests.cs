using Moq;
using PhoneDesk.Models;
using PhoneDesk.Localization;
using PhoneDesk.Services;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Tests.TestSupport;
using PhoneDesk.ViewModels;

namespace PhoneDesk.Tests
{
    /// <summary>
    /// Covers <see cref="WizardViewModel"/> step-transition logic: forward/backward navigation and its
    /// boundary conditions, per-step execution (happy path, failure, retry, skip), and that wizard state
    /// (the shared <see cref="PhoneManagerVariables"/>) is carried across steps rather than reset.
    /// </summary>
    public class WizardViewModelTests
    {
        private static WizardViewModel CreateViewModel(ViewModelTestHarness harness) =>
            new WizardViewModel(
                harness.PowerShellContextService.Object,
                harness.PowerShellCommandService.Object,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object);

        private static void CompleteReview(WizardViewModel vm)
            => vm.ExecuteCurrentStepCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        private static void MoveToStepOne(WizardViewModel vm)
        {
            CompleteReview(vm);
            vm.GoToNextStepCommand.Execute(null);
        }

        private static void MarkProvisioningStepsCompleted(WizardViewModel vm)
        {
            for (var index = 1; index < vm.TotalSteps - 1; index++)
            {
                vm.Steps[index].IsCompleted = true;
            }
        }

        [Fact]
        public void Construction_InitializesStepsAndStartsAtStepZero()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            Assert.Equal(10, vm.TotalSteps);
            Assert.Equal(0, vm.CurrentStep);
            Assert.Equal("Review Configuration", vm.StepTitle);
            Assert.False(vm.CanGoPrevious);
            Assert.False(vm.CanGoNext);
            Assert.True(vm.CanExecuteStep);
        }

        [Fact]
        public void InvalidConfiguration_CannotCompleteReviewOrAdvance()
        {
            var validation = new ValidationResult();
            validation.AddError("Customer name is required.");
            var harness = new ViewModelTestHarness();
            harness.ValidationService
                .Setup(v => v.ValidateVariables(It.IsAny<IPhoneManagerVariables>()))
                .Returns(validation);

            var vm = CreateViewModel(harness);

            Assert.False(vm.ConfigurationReady);
            Assert.False(vm.CanExecuteStep);
            Assert.False(vm.CanGoNext);
            Assert.Contains("Customer name is required", vm.ValidationSummary);
            Assert.Contains("not available yet", vm.ReviewSummary, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void InvalidConfiguration_ReadinessDetailsStartCollapsedAndExposeCompactSummary()
        {
            var validation = new ValidationResult();
            validation.AddError("Customer name is required.");
            var harness = new ViewModelTestHarness();
            harness.ValidationService
                .Setup(v => v.ValidateVariables(It.IsAny<IPhoneManagerVariables>()))
                .Returns(validation);

            var vm = CreateViewModel(harness);

            Assert.True(vm.HasValidationIssues);
            Assert.Equal(1, vm.ValidationIssueCount);
            Assert.Equal("Open checks: 1", vm.ValidationIssueSummary);
            Assert.False(vm.IsValidationDetailsExpanded);
            Assert.Equal("Show details", vm.ValidationDetailsAction);
            Assert.Contains("Customer name is required.", vm.ValidationIssues);
        }

        [Fact]
        public void ToggleValidationDetails_ChangesActionWithoutChangingValidationIssues()
        {
            var validation = new ValidationResult();
            validation.AddError("Customer name is required.");
            var harness = new ViewModelTestHarness();
            harness.ValidationService
                .Setup(v => v.ValidateVariables(It.IsAny<IPhoneManagerVariables>()))
                .Returns(validation);

            var vm = CreateViewModel(harness);

            vm.ToggleValidationDetailsCommand.Execute(null);

            Assert.True(vm.IsValidationDetailsExpanded);
            Assert.Equal("Hide details", vm.ValidationDetailsAction);
            Assert.Contains("Customer name is required.", vm.ValidationIssues);
        }

        [Fact]
        public void SwitchingLanguage_LocalizesValidationSummaryAndDetailsAction()
        {
            var validation = new ValidationResult();
            validation.AddError("Customer name is required.");
            var harness = new ViewModelTestHarness();
            harness.ValidationService
                .Setup(v => v.ValidateVariables(It.IsAny<IPhoneManagerVariables>()))
                .Returns(validation);
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

            harness.TranslationService.CurrentLanguage = AppLanguage.German;

            Assert.Equal("Offene Prüfungen: 1", vm.ValidationIssueSummary);
            Assert.Equal("Details anzeigen", vm.ValidationDetailsAction);

            vm.ToggleValidationDetailsCommand.Execute(null);

            Assert.Equal("Details ausblenden", vm.ValidationDetailsAction);
        }

        [Fact]
        public void SwitchingLanguage_LocalizesValidationMessagesInReadinessDetails()
        {
            var validation = new ValidationResult();
            validation.AddError(ValidationErrorCode.CustomerNameRequired, "Customer name is required.");
            var harness = new ViewModelTestHarness();
            harness.ValidationService
                .Setup(v => v.ValidateVariables(It.IsAny<IPhoneManagerVariables>()))
                .Returns(validation);
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

            harness.TranslationService.CurrentLanguage = AppLanguage.German;

            Assert.Contains("Kundenname ist erforderlich.", vm.ValidationIssues);
        }

        [Fact]
        public async Task InvalidConfiguration_ReviewCommandDoesNotCompleteStep()
        {
            var validation = new ValidationResult();
            validation.AddError("Customer name is required.");
            var harness = new ViewModelTestHarness();
            harness.ValidationService
                .Setup(v => v.ValidateVariables(It.IsAny<IPhoneManagerVariables>()))
                .Returns(validation);
            var vm = CreateViewModel(harness);

            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);

            Assert.False(vm.StepCompleted);
            Assert.Equal(0, vm.CurrentStep);
            Assert.Contains("Customer name is required", vm.StatusMessage);
        }

        [Fact]
        public void IncompletePrerequisites_BlocksProvisioningAndLeavingReview()
        {
            var prerequisites = new ValidationResult();
            prerequisites.AddError("Not connected to Microsoft Teams.");
            var harness = new ViewModelTestHarness();
            harness.ValidationService
                .Setup(v => v.ValidatePrerequisites())
                .Returns(prerequisites);
            var vm = CreateViewModel(harness);

            Assert.True(vm.ConfigurationReady);
            Assert.False(vm.PrerequisitesReady);
            Assert.True(vm.CanExecuteStep); // The read-only review itself is still allowed.
            Assert.False(vm.CanGoNext);

            vm.CurrentStep = 1;

            Assert.False(vm.CanExecuteStep);
            Assert.Contains("Get Started", vm.ReadinessMessage);
        }

        [Fact]
        public void ReadinessProperties_ExposeHumanReadableStepNumber()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            Assert.Equal("Step 1 of 10", vm.StepNumberText);
            Assert.True(vm.ConfigurationReady);
            Assert.True(vm.PrerequisitesReady);

            CompleteReview(vm);
            vm.GoToNextStepCommand.Execute(null);

            Assert.Equal("Step 2 of 10", vm.StepNumberText);
        }

        [Fact]
        public void SwitchingLanguage_LocalizesWizardMetadataAndComputedSummary()
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

            Assert.Equal("Step 1 of 10", vm.StepNumberText);
            Assert.Equal("Review Configuration", vm.StepTitle);
            Assert.Contains("Customer", vm.ReviewSummary, StringComparison.Ordinal);
            Assert.Contains("contoso", vm.ReviewSummary, StringComparison.Ordinal);

            harness.TranslationService.CurrentLanguage = AppLanguage.German;

            Assert.Equal("Schritt 1 von 10", vm.StepNumberText);
            Assert.Equal("Konfiguration prüfen", vm.StepTitle);
            Assert.Contains("Kunde", vm.ReviewSummary, StringComparison.Ordinal);
            Assert.Contains("contoso", vm.ReviewSummary, StringComparison.Ordinal);

            vm.CurrentStep = 1;
            Assert.Equal("M365-Gruppe erstellen", vm.StepTitle);
            Assert.Contains("Prüfen Sie diesen Schritt", vm.ReadinessMessage, StringComparison.Ordinal);
        }

        // ── Forward/backward navigation and boundaries ─────────────────────

        [Fact]
        public void GoToNextStep_AdvancesCurrentStepAndUpdatesStepTitle()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            CompleteReview(vm);
            vm.GoToNextStepCommand.Execute(null);

            Assert.Equal(1, vm.CurrentStep);
            Assert.Equal("Create M365 Group", vm.StepTitle);
        }

        [Fact]
        public void GoToNextStep_CannotJumpOverAnUncompletedStep()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.GoToNextStepCommand.Execute(null);

            Assert.Equal(0, vm.CurrentStep);
            Assert.Contains("Complete or skip this step", vm.StatusMessage);
        }

        [Fact]
        public void GoToPreviousStep_AtStepZero_DoesNotGoNegative()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.GoToPreviousStepCommand.Execute(null);

            Assert.Equal(0, vm.CurrentStep);
            Assert.False(vm.CanGoPrevious);
        }

        [Fact]
        public void GoToNextStep_AtLastStep_DoesNotGoPastTotalSteps()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            CompleteReview(vm);
            for (var step = 1; step < vm.TotalSteps - 1; step++)
            {
                vm.Steps[step].IsCompleted = true;
                vm.StepCompleted = true;
                vm.GoToNextStepCommand.Execute(null);
            }
            vm.GoToNextStepCommand.Execute(null);

            Assert.Equal(vm.TotalSteps - 1, vm.CurrentStep);
            Assert.False(vm.CanGoNext);
        }

        [Fact]
        public void GoToPreviousStep_AfterAdvancing_MovesBackOneStep()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            MoveToStepOne(vm);
            vm.Steps[1].IsCompleted = true;
            vm.StepCompleted = true;
            vm.GoToNextStepCommand.Execute(null);

            vm.GoToPreviousStepCommand.Execute(null);

            Assert.Equal(1, vm.CurrentStep);
            Assert.Equal("Create M365 Group", vm.StepTitle);
        }

        // ── Wizard state carried between steps ──────────────────────────────

        [Fact]
        public void StepScript_ForReviewStep_IncludesCurrentSharedVariables()
        {
            var harness = new ViewModelTestHarness();
            var sharedVars = new PhoneManagerVariables { Customer = "fabrikam", CustomerGroupName = "empfang" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(sharedVars);
            var vm = CreateViewModel(harness);

            Assert.Contains("fabrikam", vm.StepScript);
            Assert.Contains("empfang", vm.StepScript);
        }

        [Fact]
        public void LicenseStepScriptsKeepResourceSpecificTechnicalComments()
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

            vm.CurrentStep = 3;
            Assert.Contains("# Step 1: Set usage location for call queue resource account", vm.StepScript);

            vm.CurrentStep = 6;
            Assert.Contains("# Step 1: Set usage location for automatic attendant resource account", vm.StepScript);

            harness.TranslationService.CurrentLanguage = AppLanguage.German;
            Assert.Contains("# Step 1: Set usage location for automatic attendant resource account", vm.StepScript);
        }

        // ── Step execution: happy path, failure, retry, skip ───────────────

        [Fact]
        public async Task ExecuteCurrentStepAsync_StepZero_IsReviewOnly_CompletesWithoutRunningPowerShell()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);

            Assert.True(vm.StepCompleted);
            Assert.Equal("Configuration reviewed.", vm.StepResult);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ExecuteCurrentStepAsync_HappyPath_MarksStepCompleted()
        {
            var harness = new ViewModelTestHarness();
            harness.PowerShellCommandService.Setup(c => c.GetCreateM365GroupCommand(It.IsAny<string>())).Returns("New-CsGroup ...");
            var vm = CreateViewModel(harness);
            MoveToStepOne(vm);
            harness.SetExecutionResult("SUCCESS: group created");

            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);

            Assert.True(vm.StepCompleted);
            Assert.False(vm.StepFailed);
            Assert.True(vm.Steps[1].IsCompleted);
            Assert.Equal("SUCCESS: group created", vm.StepResult);
        }

        [Fact]
        public async Task ExecuteCurrentStepAsync_ErrorPath_MarksStepFailedAndDoesNotComplete()
        {
            var harness = new ViewModelTestHarness();
            harness.PowerShellCommandService.Setup(c => c.GetCreateM365GroupCommand(It.IsAny<string>())).Returns("New-CsGroup ...");
            var vm = CreateViewModel(harness);
            MoveToStepOne(vm);
            harness.SetExecutionResult("ERROR: group creation failed", hadErrors: true);

            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);

            Assert.True(vm.StepFailed);
            Assert.False(vm.StepCompleted);
            Assert.True(vm.Steps[1].IsFailed);
            Assert.False(vm.CanExecuteStep);
            Assert.Contains("failed", vm.StatusMessage);
        }

        [Fact]
        public async Task GoToNextStepCommand_AfterStepFailure_CannotAdvancePastFailedStep()
        {
            // Acceptance criterion: cannot advance past a failed step. CanGoNext (bound to the Next
            // button's IsEnabled in WizardView.axaml) consults StepFailed/Steps[CurrentStep].IsFailed
            // in addition to the bounds check, and GoToNextStep() guards identically, so once a step
            // fails the Next command is disabled and calling it does not move the wizard forward.
            var harness = new ViewModelTestHarness();
            harness.PowerShellCommandService.Setup(c => c.GetCreateM365GroupCommand(It.IsAny<string>())).Returns("New-CsGroup ...");
            var vm = CreateViewModel(harness);
            MoveToStepOne(vm);
            harness.SetExecutionResult("ERROR: boom", hadErrors: true);
            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);
            Assert.True(vm.StepFailed);

            var canExecuteAfterFailure = vm.GoToNextStepCommand.CanExecute(null);
            Assert.False(canExecuteAfterFailure);

            vm.GoToNextStepCommand.Execute(null);

            Assert.Equal(1, vm.CurrentStep);
        }

        [Fact]
        public async Task GoToNextStepCommand_AfterFailureIsRetriedSuccessfully_CanAdvanceAgain()
        {
            var harness = new ViewModelTestHarness();
            harness.PowerShellCommandService.Setup(c => c.GetCreateM365GroupCommand(It.IsAny<string>())).Returns("New-CsGroup ...");
            var vm = CreateViewModel(harness);
            MoveToStepOne(vm);
            harness.SetExecutionResult("ERROR: boom", hadErrors: true);
            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);
            Assert.False(vm.GoToNextStepCommand.CanExecute(null));

            vm.RetryStepCommand.Execute(null);
            harness.SetExecutionResult("SUCCESS: group created");
            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);

            Assert.True(vm.GoToNextStepCommand.CanExecute(null));

            vm.GoToNextStepCommand.Execute(null);

            Assert.Equal(2, vm.CurrentStep);
        }

        [Fact]
        public async Task RetryStep_ClearsFailedAndCompletedState()
        {
            var harness = new ViewModelTestHarness();
            harness.PowerShellCommandService.Setup(c => c.GetCreateM365GroupCommand(It.IsAny<string>())).Returns("New-CsGroup ...");
            var vm = CreateViewModel(harness);
            MoveToStepOne(vm);
            harness.SetExecutionResult("ERROR: boom", hadErrors: true);
            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);
            Assert.True(vm.StepFailed);

            vm.RetryStepCommand.Execute(null);

            Assert.False(vm.StepFailed);
            Assert.False(vm.StepCompleted);
            Assert.Equal(string.Empty, vm.StepResult);
            Assert.False(vm.Steps[1].IsFailed);
            Assert.False(vm.Steps[1].IsCompleted);
        }

        [Fact]
        public void SkipStep_MarksCurrentStepSkippedAndAdvances()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.SkipStepCommand.Execute(null);

            Assert.True(vm.Steps[0].IsSkipped);
            Assert.Equal("Skipped by user.", vm.Steps[0].Result);
            Assert.Equal(1, vm.CurrentStep);
        }

        [Fact]
        public async Task ExecuteCurrentStepAsync_LastStep_CompletesAsSummaryWithoutRunningPowerShell()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            CompleteReview(vm);
            MarkProvisioningStepsCompleted(vm);
            vm.CurrentStep = 9;
            Assert.Equal(9, vm.CurrentStep);

            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);

            Assert.True(vm.StepCompleted);
            Assert.Equal("Setup complete!", vm.StepResult);
            harness.PowerShellContextService.Verify(
                p => p.ExecuteCommandWithDetailsAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>(), It.IsAny<IProgress<PowerShellProgress>?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ExecuteCurrentStepAsync_SummaryRefusesSuccessWhenProvisioningWasSkipped()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            CompleteReview(vm);

            while (vm.CurrentStep < vm.TotalSteps - 1)
            {
                vm.SkipStepCommand.Execute(null);
            }

            Assert.Equal(vm.TotalSteps - 1, vm.CurrentStep);

            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);

            Assert.False(vm.StepCompleted);
            Assert.Contains("not complete", vm.StepResult, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CanExecuteStep_BecomesFalseAfterCompletion_AndTrueAgainAfterRetry()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);
            Assert.True(vm.CanExecuteStep);

            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null); // step 0, review-only, completes

            Assert.False(vm.CanExecuteStep);

            vm.RetryStepCommand.Execute(null);

            Assert.True(vm.CanExecuteStep);
        }

        // ---- Progress across navigation, step list, results ----

        private static ViewModelTestHarness CreateHarnessWithSharedProgress(PhoneManagerVariables? variables = null)
        {
            var harness = new ViewModelTestHarness();
            harness.SharedStateService.SetupProperty(s => s.WizardProgress);
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(variables ?? new PhoneManagerVariables
            {
                Customer = "ewp",
                CustomerGroupName = "hauptnummer",
                MsFallbackDomain = "contoso.onmicrosoft.com"
            });
            // Scripts carry their inputs, as the real builders do; the step fingerprint is a hash of the script.
            harness.PowerShellCommandService.Setup(c => c.GetCreateM365GroupCommand(It.IsAny<string>())).Returns<string>(name => "New-MgGroup " + name);
            harness.PowerShellCommandService.Setup(c => c.GetCreateResourceAccountCommand(It.IsAny<IPhoneManagerVariables>())).Returns("New-CsOnlineApplicationInstance ...");
            return harness;
        }

        [Fact]
        public async Task Progress_SurvivesLeavingAndReopeningTheGuidedSetup()
        {
            var harness = CreateHarnessWithSharedProgress();
            var first = CreateViewModel(harness);
            MoveToStepOne(first);
            harness.SetExecutionResult("SUCCESS: group created");
            await first.ExecuteCurrentStepCommand.ExecuteAsync(null);
            first.GoToNextStepCommand.Execute(null);
            harness.SetExecutionResult("ERROR: Failed to create resource account: boom", hadErrors: true);
            await first.ExecuteCurrentStepCommand.ExecuteAsync(null);
            first.Dispose();

            // Navigation creates a new page ViewModel (for example after a Configuration fix).
            var reopened = CreateViewModel(harness);

            Assert.Equal(2, reopened.CurrentStep);
            Assert.True(reopened.Steps[0].IsCompleted);
            Assert.True(reopened.Steps[1].IsCompleted);
            Assert.True(reopened.Steps[2].IsFailed);
            Assert.True(reopened.StepFailed);
            Assert.True(reopened.ShowRetryAction);
            Assert.Contains("boom", reopened.StepResult);
        }

        [Fact]
        public async Task GoToStep_OpensACompletedStepWithItsOwnResult()
        {
            var harness = CreateHarnessWithSharedProgress();
            var vm = CreateViewModel(harness);
            MoveToStepOne(vm);
            harness.SetExecutionResult("SUCCESS: group created");
            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);
            vm.GoToNextStepCommand.Execute(null);
            Assert.Equal(string.Empty, vm.StepResult);

            vm.GoToStepCommand.Execute(vm.Steps[1]);

            Assert.Equal(1, vm.CurrentStep);
            Assert.True(vm.StepCompleted);
            Assert.Equal("SUCCESS: group created", vm.StepResult);
            Assert.True(vm.Steps[1].IsCurrent);
            Assert.False(vm.Steps[2].IsCurrent);
            Assert.True(vm.CanRunAgain);
        }

        [Fact]
        public void GoToStep_CannotOpenAStepAfterTheFirstOpenStep()
        {
            var harness = CreateHarnessWithSharedProgress();
            var vm = CreateViewModel(harness);

            vm.GoToStepCommand.Execute(vm.Steps[4]);

            Assert.Equal(0, vm.CurrentStep);
            Assert.True(vm.Steps[0].IsReachable);
            Assert.False(vm.Steps[1].IsReachable);
        }

        [Fact]
        public void ChangingStep_ClosesTheDryRunPlan()
        {
            var harness = CreateHarnessWithSharedProgress();
            var planBuilder = new Mock<IDryRunPlanBuilder>();
            planBuilder.Setup(b => b.BuildWizardPlan(It.IsAny<IPhoneManagerVariables>()))
                .Returns(new PhoneDesk.Planning.DryRunPlan("wizard", System.DateTimeOffset.UtcNow, System.Array.Empty<PhoneDesk.Planning.DryRunEntry>()));
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
                planBuilder.Object);
            MoveToStepOne(vm);
            vm.GeneratePlanCommand.Execute(null);
            Assert.NotNull(vm.Plan);

            vm.GoToPreviousStepCommand.Execute(null);

            Assert.Equal(0, vm.CurrentStep);
            Assert.Null(vm.Plan);
        }

        [Fact]
        public async Task ExecuteCurrentStep_SuccessLineFollowedByErrorRecord_FailsWithShortError()
        {
            var harness = CreateHarnessWithSharedProgress();
            var vm = CreateViewModel(harness);
            MoveToStepOne(vm);
            harness.SetExecutionResult("SUCCESS: group created");
            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);
            vm.GoToNextStepCommand.Execute(null);
            harness.SetExecutionResult(
                "SUCCESS: Resource account created successfully\nERROR: Length cannot be less than zero.",
                hadErrors: true,
                errors: new[] { new PowerShellErrorInfo { Message = "Length cannot be less than zero." } });

            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);

            Assert.True(vm.StepFailed);
            Assert.False(vm.Steps[2].IsCompleted);
            Assert.True(vm.HasStepError);
            Assert.Equal("Length cannot be less than zero.", vm.StepErrorMessage);
        }

        [Fact]
        public async Task SkippingAFailedStep_ClearsTheFailureAndUnlocksTheNextStep()
        {
            var harness = CreateHarnessWithSharedProgress();
            var vm = CreateViewModel(harness);
            MoveToStepOne(vm);
            harness.SetExecutionResult("ERROR: boom", hadErrors: true);
            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);

            vm.SkipStepCommand.Execute(null);

            Assert.Equal(2, vm.CurrentStep);
            Assert.False(vm.Steps[1].IsFailed);
            Assert.True(vm.Steps[1].IsSkipped);
            Assert.True(vm.Steps[2].IsReachable);
        }

        [Fact]
        public async Task ChangedConfiguration_NamesTheStepsThatRanWithEarlierValues()
        {
            var variables = new PhoneManagerVariables
            {
                Customer = "ewp",
                CustomerGroupName = "hauptnummer",
                MsFallbackDomain = "contoso.onmicrosoft.com"
            };
            var harness = CreateHarnessWithSharedProgress(variables);
            var vm = CreateViewModel(harness);
            MoveToStepOne(vm);
            harness.SetExecutionResult("SUCCESS: group created");
            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);
            Assert.False(vm.HasConfigurationChangedNotice);
            vm.Dispose();

            variables.CustomerGroupName = "zentrale";
            var reopened = CreateViewModel(harness);

            Assert.True(reopened.HasConfigurationChangedNotice);
            Assert.Contains(reopened.Steps[1].Title, reopened.ConfigurationChangedNotice);
        }

        [Fact]
        public void ChangedOpeningHours_FlagOnlyTheAutoAttendantStep()
        {
            var variables = new PhoneManagerVariables
            {
                Customer = "ewp",
                CustomerGroupName = "hauptnummer",
                MsFallbackDomain = "contoso.onmicrosoft.com",
                RaaAnrName = "hn",
                LanguageId = "de-DE",
                TimeZoneId = "W. Europe Standard Time"
            };
            var harness = CreateHarnessWithSharedProgress(variables);
            var san = new PowerShellSanitizationService();
            var realCommands = new PowerShellCommandService(
                new PhoneDesk.Services.ScriptBuilders.CommonScriptBuilder(san),
                new PhoneDesk.Services.ScriptBuilders.CallQueueScriptBuilder(san),
                new PhoneDesk.Services.ScriptBuilders.AutoAttendantScriptBuilder(san),
                new PhoneDesk.Services.ScriptBuilders.HolidayScriptBuilder(san),
                new PhoneDesk.Services.ScriptBuilders.ResourceAccountScriptBuilder(san),
                new PhoneDesk.Services.ScriptBuilders.DashboardScriptBuilder());
            var vm = new WizardViewModel(
                harness.PowerShellContextService.Object,
                realCommands,
                harness.LoggingService.Object,
                harness.SessionManager.Object,
                harness.NavigationService.Object,
                harness.ErrorHandlingService.Object,
                harness.ValidationService.Object,
                harness.SharedStateService.Object,
                harness.DialogService.Object);
            const int callQueueStep = 4;
            const int autoAttendantStep = 7;
            foreach (var index in new[] { callQueueStep, autoAttendantStep })
            {
                vm.Steps[index].IsCompleted = true;
                vm.Steps[index].ConfigurationFingerprint = vm.ComputeStepFingerprint(index);
            }
            Assert.False(vm.HasConfigurationChangedNotice);

            variables.OpeningHours1End = new TimeSpan(18, 0, 0);

            Assert.Contains(vm.Steps[autoAttendantStep].Title, vm.ConfigurationChangedNotice);
            Assert.DoesNotContain(vm.Steps[callQueueStep].Title, vm.ConfigurationChangedNotice);
        }

        [Fact]
        public async Task StartOver_AfterConfirmation_ClearsAllProgress()
        {
            var harness = CreateHarnessWithSharedProgress();
            var vm = CreateViewModel(harness);
            MoveToStepOne(vm);
            harness.SetExecutionResult("SUCCESS: group created");
            await vm.ExecuteCurrentStepCommand.ExecuteAsync(null);
            Assert.True(vm.HasProgress);

            await vm.StartOverCommand.ExecuteAsync(null);

            Assert.Equal(0, vm.CurrentStep);
            Assert.False(vm.HasProgress);
            Assert.All(harness.SharedStateService.Object.WizardProgress!.Steps, s => Assert.False(s.IsCompleted));
        }
    }
}
