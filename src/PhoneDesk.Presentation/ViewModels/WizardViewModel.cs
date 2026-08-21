using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services;
using PhoneDesk.Models;
using PhoneDesk.Planning;
using PhoneDesk.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace PhoneDesk.ViewModels
{
    public partial class WizardViewModel : ViewModelBase
    {
        [ObservableProperty]
        private int _currentStep = 0;

        [ObservableProperty]
        private int _totalSteps = 10;

        [ObservableProperty]
        private string _stepTitle = string.Empty;

        [ObservableProperty]
        private string _stepDescription = string.Empty;

        [ObservableProperty]
        private string _stepScript = string.Empty;

        [ObservableProperty]
        private bool _stepCompleted = false;

        [ObservableProperty]
        private bool _stepFailed = false;

        [ObservableProperty]
        private string _stepResult = string.Empty;

        [ObservableProperty]
        private ObservableCollection<WizardStepInfo> _steps = new();

        private readonly IDryRunPlanBuilder? _planBuilder;
        private readonly IDryRunPlanExporter? _planExporter;

        /// <summary>
        /// The most recently generated dry-run plan for the current configuration. Null until the operator
        /// generates a preview. Generating it performs no tenant mutation and executes no PowerShell.
        /// </summary>
        [ObservableProperty]
        private DryRunPlan? _plan;

        private ValidationResult _configurationValidation = new();
        private ValidationResult _prerequisiteValidation = new();

        public string StepNumberText => $"Step {Math.Clamp(CurrentStep + 1, 1, TotalSteps)} of {TotalSteps}";
        public string StepNumberBadgeText => Math.Clamp(CurrentStep + 1, 1, TotalSteps).ToString();
        public double WizardProgressValue => Math.Clamp(CurrentStep + 1, 1, TotalSteps);
        public bool IsReviewStep => CurrentStep == 0;
        public bool IsSummaryStep => CurrentStep == TotalSteps - 1;
        public string ExecuteButtonText => IsReviewStep
            ? "Confirm configuration"
            : IsSummaryStep ? "Finish setup" : "Execute step";
        public bool ConfigurationReady => _configurationValidation.IsValid;
        public bool PrerequisitesReady => _prerequisiteValidation.IsValid;
        public string ReviewSummary
        {
            get
            {
                var vars = Variables;
                return $"""
                Customer
                {DisplayValue(vars.Customer)}

                Customer group
                {DisplayValue(vars.CustomerGroupName)}

                Fallback domain
                {DisplayValue(vars.MsFallbackDomain)}

                Language and time zone
                {DisplayValue(vars.LanguageId)} · {DisplayValue(vars.TimeZoneId)}

                Usage location
                {DisplayValue(vars.UsageLocation)}

                Computed Teams Phone names
                M365 group: {DisplayComputedValue(vars.M365Group)}
                Call queue account: {DisplayComputedValue(vars.RacqUPN)}
                Auto attendant account: {DisplayComputedValue(vars.RaaaUPN)}

                Phone number
                {DisplayValue(vars.RaaAnr)} ({DisplayValue(vars.PhoneNumberType)})
                """;
            }
        }

        public string ValidationSummary
        {
            get
            {
                var messages = new List<string>(_configurationValidation.Errors);
                if (!PrerequisitesReady)
                {
                    messages.Add("Connection checks are incomplete. Open Get Started to connect to Teams and Microsoft Graph.");
                }

                return string.Join(Environment.NewLine, messages);
            }
        }

        public string ReadinessMessage
        {
            get
            {
                if (!ConfigurationReady)
                {
                    return "Complete the required configuration in Configuration before continuing.";
                }

                if (!PrerequisitesReady)
                {
                    return "Complete the connection checks in Get Started before provisioning.";
                }

                if (IsSummaryStep)
                {
                    return "Review the completed steps or add holidays next.";
                }

                if (IsReviewStep)
                {
                    return "Review the configuration, then confirm it before starting setup.";
                }

                return "Preview this step before you run it. It changes the tenant only after you confirm it.";
            }
        }

        public bool CanGoNext => CurrentStep < TotalSteps - 1
            && !StepFailed
            && ConfigurationReady
            && PrerequisitesReady
            && CurrentStep >= 0
            && CurrentStep < Steps.Count
            && (StepCompleted || Steps[CurrentStep].IsSkipped);
        public bool CanGoPrevious => CurrentStep > 0;
        public bool CanExecuteStep => !IsBusy
            && !StepFailed
            && !StepCompleted
            && (IsSummaryStep
                ? AllProvisioningStepsCompleted()
                : (IsReviewStep ? ConfigurationReady : ConfigurationReady && PrerequisitesReady));

        public PhoneManagerVariables Variables => _sharedStateService?.Variables ?? new PhoneManagerVariables();

        public WizardViewModel(
            IPowerShellContextService powerShellContextService,
            IPowerShellCommandService powerShellCommandService,
            ILoggingService loggingService,
            ISessionManager sessionManager,
            INavigationService navigationService,
            IErrorHandlingService errorHandlingService,
            IValidationService validationService,
            ISharedStateService sharedStateService,
            IDialogService dialogService,
            IDryRunPlanBuilder? planBuilder = null,
            IDryRunPlanExporter? planExporter = null,
            IAuditLog? auditLog = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, sharedStateService, dialogService, auditLog)
        {
            _planBuilder = planBuilder;
            _planExporter = planExporter;
            _loggingService.Log("Wizard page loaded", LogLevel.Info);
            InitializeSteps();
            UpdateCurrentStep();
        }

        private void InitializeSteps()
        {
            Steps = new ObservableCollection<WizardStepInfo>
            {
                new(0, "Review Configuration", "Verify all variables before starting the setup.", "Settings"),
                new(1, "Create M365 Group", "Create the Microsoft 365 distribution group for call queue agents.", "Execute"),
                new(2, "Create CQ Resource Account", "Create the resource account for the Call Queue.", "Execute"),
                new(3, "License CQ Resource Account", "Set usage location and assign Teams Phone license to the CQ resource account.", "Execute"),
                new(4, "Create Call Queue", "Create the Call Queue and associate it with the resource account.", "Execute"),
                new(5, "Create AA Resource Account", "Create the resource account for the Auto Attendant.", "Execute"),
                new(6, "License AA Resource Account", "Set usage location, assign license, and assign phone number to the AA resource account.", "Execute"),
                new(7, "Create Auto Attendant", "Create call flows, schedule, and the Auto Attendant (runs as one script).", "Execute"),
                new(8, "Associate AA Resource Account", "Associate the AA resource account with the Auto Attendant.", "Execute"),
                new(9, "Setup Complete", "All components have been created. You can now add holidays from the Holidays page.", "Summary"),
            };
            TotalSteps = Steps.Count;
        }

        private void RefreshReadiness()
        {
            _configurationValidation = _validationService.ValidateVariables(Variables);
            _prerequisiteValidation = _validationService.ValidatePrerequisites();

            OnPropertyChanged(nameof(ConfigurationReady));
            OnPropertyChanged(nameof(PrerequisitesReady));
            OnPropertyChanged(nameof(ValidationSummary));
            OnPropertyChanged(nameof(ReadinessMessage));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanExecuteStep));
            GoToNextStepCommand.NotifyCanExecuteChanged();
        }

        private bool EnsureConfigurationReady()
        {
            RefreshReadiness();
            if (ConfigurationReady)
            {
                return true;
            }

            StatusMessage = ValidationSummary;
            return false;
        }

        private bool EnsureProvisioningReady()
        {
            RefreshReadiness();
            if (ConfigurationReady && PrerequisitesReady)
            {
                return true;
            }

            StatusMessage = !ConfigurationReady
                ? ValidationSummary
                : "Complete the connection checks in Get Started before provisioning.";
            return false;
        }

        private bool AllProvisioningStepsCompleted()
        {
            for (var index = 1; index < Steps.Count - 1; index++)
            {
                if (!Steps[index].IsCompleted)
                {
                    return false;
                }
            }

            return Steps.Count > 1;
        }

        private bool EnsureAdvanceReady()
        {
            if (!EnsureProvisioningReady())
            {
                return false;
            }

            if (CurrentStep >= 0 && CurrentStep < Steps.Count && (StepCompleted || Steps[CurrentStep].IsSkipped))
            {
                return true;
            }

            StatusMessage = "Complete or skip this step before moving on.";
            return false;
        }

        private void UpdateCurrentStep()
        {
            if (CurrentStep >= 0 && CurrentStep < Steps.Count)
            {
                var step = Steps[CurrentStep];
                StepTitle = step.Title;
                StepDescription = step.Description;
                StepCompleted = step.IsCompleted;
                StepFailed = step.IsFailed;
                StepResult = step.Result;
                StepScript = GetScriptForStep(CurrentStep);
                RefreshReadiness();
                OnPropertyChanged(nameof(ReviewSummary));
                OnPropertyChanged(nameof(StepNumberText));
                OnPropertyChanged(nameof(StepNumberBadgeText));
                OnPropertyChanged(nameof(WizardProgressValue));
                OnPropertyChanged(nameof(IsReviewStep));
                OnPropertyChanged(nameof(IsSummaryStep));
                OnPropertyChanged(nameof(ExecuteButtonText));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(CanGoPrevious));
                OnPropertyChanged(nameof(CanExecuteStep));
                GoToNextStepCommand.NotifyCanExecuteChanged();
            }
        }

        private string GetScriptForStep(int step)
        {
            var vars = Variables;
            try
            {
                return step switch
                {
                    0 => FormatVariablesSummary(vars),
                    1 => _powerShellCommandService.GetCreateM365GroupCommand(vars.M365Group),
                    2 => _powerShellCommandService.GetCreateResourceAccountCommand(vars),
                    3 => BuildLicenseCqScript(vars),
                    4 => _powerShellCommandService.GetCreateCallQueueCommand(vars),
                    5 => _powerShellCommandService.GetCreateAutoAttendantResourceAccountCommand(vars),
                    6 => BuildLicenseAaScript(vars),
                    7 => _powerShellCommandService.GetCreateAutoAttendantCommand(vars),
                    8 => _powerShellCommandService.GetAssociateResourceAccountWithAutoAttendantCommand(vars.RaaaUPN, vars.AaDisplayName),
                    9 => "🎉 Setup complete! All Teams Phone components have been created.",
                    _ => string.Empty
                };
            }
            catch (Exception ex)
            {
                return $"# Error generating script preview:\n# {ex.Message}";
            }
        }

        private string FormatVariablesSummary(PhoneManagerVariables vars)
        {
            return $"""
            # ======================================
            # Configuration Review
            # ======================================
            
            # Customer:          {DisplayValue(vars.Customer)}
            # Customer Group:    {DisplayValue(vars.CustomerGroupName)}
            # Fallback Domain:   {DisplayValue(vars.MsFallbackDomain)}
            # Language:           {DisplayValue(vars.LanguageId)}
            # Time Zone:          {DisplayValue(vars.TimeZoneId)}
            # Usage Location:     {DisplayValue(vars.UsageLocation)}
            
            # --- Computed Names ---
            # M365 Group:         {DisplayComputedValue(vars.M365Group)}
            # CQ Resource UPN:    {DisplayComputedValue(vars.RacqUPN)}
            # CQ Display Name:    {DisplayComputedValue(vars.CqDisplayName)}
            # AA Resource UPN:    {DisplayComputedValue(vars.RaaaUPN)}
            # AA Display Name:    {DisplayComputedValue(vars.AaDisplayName)}
            # Phone Number:       {DisplayValue(vars.RaaAnr)}
            # Phone Number Type:  {DisplayValue(vars.PhoneNumberType)}
            
            # --- Licensing ---
            # SKU ID:             {DisplayValue(vars.SkuId)}
            # CQ App ID:          {DisplayValue(vars.CsAppCqId)}
            # AA App ID:          {DisplayValue(vars.CsAppAaId)}
            
            # ======================================
            # Ensure all values are correct before
            # proceeding. Use the Variables page to
            # make changes.
            # ======================================
            """;
        }

        private static string DisplayValue(string? value)
            => string.IsNullOrWhiteSpace(value) ? "(not set)" : value;

        private static string DisplayComputedValue(string? value)
            => string.IsNullOrWhiteSpace(value)
                || value is "ttgrp--" or "racq--" or "cq--" or "raaa---" or "aa---"
                ? "(not available yet)"
                : value;

        private string BuildLicenseCqScript(PhoneManagerVariables vars)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Step 1: Set usage location for CQ Resource Account");
            sb.AppendLine(_powerShellCommandService.GetUpdateResourceAccountUsageLocationCommand(vars.RacqUPN, vars.UsageLocation));
            sb.AppendLine();
            sb.AppendLine("# Step 2: Assign Teams Phone license");
            sb.AppendLine(_powerShellCommandService.GetAssignLicenseCommand(vars.RacqUPN, vars.SkuId));
            return sb.ToString();
        }

        private string BuildLicenseAaScript(PhoneManagerVariables vars)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Step 1: Set usage location for AA Resource Account");
            sb.AppendLine(_powerShellCommandService.GetUpdateAutoAttendantResourceAccountUsageLocationCommand(vars.RaaaUPN, vars.UsageLocation));
            sb.AppendLine();
            sb.AppendLine("# Step 2: Assign Teams Phone license");
            sb.AppendLine(_powerShellCommandService.GetAssignAutoAttendantLicenseCommand(vars.RaaaUPN, vars.SkuId));
            sb.AppendLine();
            sb.AppendLine("# Step 3: Assign phone number");
            sb.AppendLine(_powerShellCommandService.GetAssignPhoneNumberToAutoAttendantCommand(vars.RaaaUPN, vars.RaaAnr, vars.PhoneNumberType));
            return sb.ToString();
        }

        [RelayCommand]
        private async Task ExecuteCurrentStepAsync()
        {
            if (CurrentStep < 0 || CurrentStep >= Steps.Count)
                return;

            var step = Steps[CurrentStep];

            // Step 0 is review-only and must still pass configuration validation.
            if (CurrentStep == 0)
            {
                if (!EnsureConfigurationReady())
                {
                    return;
                }

                step.IsCompleted = true;
                StepCompleted = true;
                StepResult = "Configuration reviewed.";
                step.Result = StepResult;
                OnPropertyChanged(nameof(CanExecuteStep));
                OnPropertyChanged(nameof(CanGoNext));
                GoToNextStepCommand.NotifyCanExecuteChanged();
                return;
            }

            // Step 9 is a non-mutating summary and may only report success after every provisioning
            // step has completed. A skipped step keeps the operator in a recoverable, incomplete state.
            if (CurrentStep == 9)
            {
                if (!AllProvisioningStepsCompleted())
                {
                    StepResult = "Setup is not complete. Return to the skipped or incomplete steps before finishing.";
                    StatusMessage = StepResult;
                    return;
                }

                step.IsCompleted = true;
                StepCompleted = true;
                StepResult = "Setup complete!";
                step.Result = StepResult;
                OnPropertyChanged(nameof(CanExecuteStep));
                return;
            }

            if (!EnsureProvisioningReady())
            {
                return;
            }

            var script = GetScriptForStep(CurrentStep);
            if (string.IsNullOrEmpty(script))
            {
                StatusMessage = "No script to execute for this step.";
                return;
            }

            // Show preview and confirm before executing
            var result = await PreviewAndExecuteAsync(script, $"Wizard Step {CurrentStep}: {step.Title}");

            if (result == null)
            {
                StatusMessage = "Step cancelled.";
                return;
            }

            var output = result.Value ?? string.Empty;

            if (result.HasErrorMarker)
            {
                step.IsFailed = true;
                step.Result = output;
                StepFailed = true;
                StepResult = output;
                StatusMessage = $"Step {CurrentStep} failed. Review the output and retry or skip.";
                _loggingService.Log($"Wizard step {CurrentStep} ({step.Title}) failed: {output}", LogLevel.Error);
            }
            else
            {
                step.IsCompleted = true;
                step.Result = output;
                StepCompleted = true;
                StepResult = string.IsNullOrWhiteSpace(output) ? "Step completed successfully." : output;
                StatusMessage = $"Step {CurrentStep} completed: {step.Title}";
                _loggingService.Log($"Wizard step {CurrentStep} ({step.Title}) completed successfully", LogLevel.Info);
            }

            OnPropertyChanged(nameof(CanExecuteStep));
            OnPropertyChanged(nameof(CanGoNext));
            GoToNextStepCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(CanGoNext))]
        private void GoToNextStep()
        {
            if (CurrentStep < TotalSteps - 1 && !StepFailed && EnsureAdvanceReady())
            {
                CurrentStep++;
                UpdateCurrentStep();
            }
        }

        [RelayCommand]
        private void GoToPreviousStep()
        {
            if (CurrentStep > 0)
            {
                CurrentStep--;
                UpdateCurrentStep();
            }
        }

        [RelayCommand]
        private void SkipStep()
        {
            if (CurrentStep < TotalSteps - 1
                && (CurrentStep != 0 || EnsureProvisioningReady()))
            {
                var step = Steps[CurrentStep];
                step.IsSkipped = true;
                step.Result = "Skipped by user.";
                _loggingService.Log($"Wizard step {CurrentStep} ({step.Title}) skipped", LogLevel.Warning);
                CurrentStep++;
                UpdateCurrentStep();
            }
        }

        [RelayCommand]
        private void RetryStep()
        {
            if (CurrentStep >= 0 && CurrentStep < Steps.Count)
            {
                var step = Steps[CurrentStep];
                step.IsFailed = false;
                step.IsCompleted = false;
                step.Result = string.Empty;
                StepCompleted = false;
                StepFailed = false;
                StepResult = string.Empty;
                RefreshReadiness();
                OnPropertyChanged(nameof(CanExecuteStep));
                ExecuteCurrentStepCommand.NotifyCanExecuteChanged();
            }
        }

        [RelayCommand]
        private void GoToVariablesPage()
        {
            NavigateToVariables();
        }

        [RelayCommand]
        private void GoToHolidaysPage()
        {
            NavigateToHolidays();
        }

        /// <summary>
        /// Builds a read-only dry-run plan for the current configuration: every object that would be created
        /// with its resolved names, UPNs, number and settings, plus validation and preflight results. This
        /// performs no tenant mutation and executes no PowerShell — it derives purely from the current
        /// variables, so it can never diverge from what execution would emit.
        /// </summary>
        [RelayCommand]
        private void GeneratePlan()
        {
            if (_planBuilder == null)
            {
                StatusMessage = "Plan preview is unavailable.";
                return;
            }

            Plan = _planBuilder.BuildWizardPlan(Variables);
            var entry = Plan.Entries.Count > 0 ? Plan.Entries[0] : null;
            if (entry != null && !entry.IsValid)
            {
                StatusMessage = $"Plan generated with {entry.ValidationErrors.Count} validation issue(s). Review before executing.";
            }
            else
            {
                StatusMessage = $"Plan generated: {Plan.TotalObjectCount} objects would be created/changed.";
            }
            _loggingService.Log("Wizard dry-run plan generated", LogLevel.Info);
        }

        [RelayCommand]
        private Task ExportPlanJsonAsync() => ExportPlanAsync(asJson: true);

        [RelayCommand]
        private Task ExportPlanCsvAsync() => ExportPlanAsync(asJson: false);

        private async Task ExportPlanAsync(bool asJson)
        {
            if (_planBuilder == null || _planExporter == null)
            {
                StatusMessage = "Plan export is unavailable.";
                return;
            }

            Plan ??= _planBuilder.BuildWizardPlan(Variables);

            var content = asJson ? _planExporter.ToJson(Plan) : _planExporter.ToCsv(Plan);
            var extension = asJson ? "json" : "csv";
            var saved = await DryRunPlanExportHelper.SavePlanAsync(content, $"wizard-dry-run-plan.{extension}", extension);
            if (saved != null)
            {
                StatusMessage = $"Plan exported to {saved}";
                _loggingService.Log($"Wizard dry-run plan exported to: {saved}", LogLevel.Info);
            }
            else
            {
                StatusMessage = "Plan export cancelled.";
            }
        }

        partial void OnCurrentStepChanged(int value)
        {
            UpdateCurrentStep();
        }

        partial void OnStepFailedChanged(bool value)
        {
            OnPropertyChanged(nameof(CanExecuteStep));
            OnPropertyChanged(nameof(CanGoNext));
            ExecuteCurrentStepCommand.NotifyCanExecuteChanged();
            GoToNextStepCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Represents a single wizard step with its state.
    /// </summary>
    public partial class WizardStepInfo : ObservableObject
    {
        public int StepNumber { get; }
        public string Title { get; }
        public string Description { get; }
        public string StepType { get; }

        [ObservableProperty]
        private bool _isCompleted;

        [ObservableProperty]
        private bool _isFailed;

        [ObservableProperty]
        private bool _isSkipped;

        [ObservableProperty]
        private string _result = string.Empty;

        public string StatusIcon => IsCompleted ? "✅" : IsFailed ? "❌" : IsSkipped ? "⏭️" : "⬜";

        public WizardStepInfo(int stepNumber, string title, string description, string stepType)
        {
            StepNumber = stepNumber;
            Title = title;
            Description = description;
            StepType = stepType;
        }

        partial void OnIsCompletedChanged(bool value) => OnPropertyChanged(nameof(StatusIcon));
        partial void OnIsFailedChanged(bool value) => OnPropertyChanged(nameof(StatusIcon));
        partial void OnIsSkippedChanged(bool value) => OnPropertyChanged(nameof(StatusIcon));
    }
}
