using Moq;
using PhoneDesk.Models;
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
        public void Variables_ReflectsSharedStateAcrossStepTransitions_NotResetLocally()
        {
            var harness = new ViewModelTestHarness();
            var sharedVars = new PhoneManagerVariables { Customer = "contoso", CustomerGroupName = "hauptnummer" };
            harness.SharedStateService.SetupGet(s => s.Variables).Returns(sharedVars);
            var vm = CreateViewModel(harness);

            Assert.Equal("contoso", vm.Variables.Customer);

            vm.CurrentStep = 3;

            // The same shared variables instance (and the value captured before navigating) is still
            // visible after moving through several steps — wizard state is not reset per step.
            Assert.Equal("contoso", vm.Variables.Customer);
            Assert.Same(sharedVars, vm.Variables);
        }

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

        [Fact]
        public void GoToVariablesPageCommand_NavigatesToVariablesPage()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.GoToVariablesPageCommand.Execute(null);

            harness.NavigationService.Verify(n => n.NavigateTo(ConstantsService.Pages.Variables), Times.Once);
        }

        [Fact]
        public void GoToHolidaysPageCommand_NavigatesToHolidaysPage()
        {
            var harness = new ViewModelTestHarness();
            var vm = CreateViewModel(harness);

            vm.GoToHolidaysPageCommand.Execute(null);

            harness.NavigationService.Verify(n => n.NavigateTo(ConstantsService.Pages.Holidays), Times.Once);
        }
    }
}
