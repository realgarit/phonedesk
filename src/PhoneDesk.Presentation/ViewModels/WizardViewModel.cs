using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneDesk.Localization;
using PhoneDesk.Services.Interfaces;
using PhoneDesk.Services;
using PhoneDesk.Models;
using PhoneDesk.Planning;
using PhoneDesk.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;

namespace PhoneDesk.ViewModels
{
    public partial class WizardViewModel : ViewModelBase, IDisposable
    {
        private bool _disposed;

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
        private bool _isValidationDetailsExpanded;

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
        private IReadOnlyList<string> _validationIssues = Array.Empty<string>();
        private UiTextKey? _stepResultKey;
        private string? _stepResultFallback;

        public string StepNumberText => GetText(
            UiTextKey.WizardStepNumber,
            "Step {step} of {totalSteps}",
            new Dictionary<string, object?>
            {
                ["step"] = Math.Clamp(CurrentStep + 1, 1, TotalSteps),
                ["totalSteps"] = TotalSteps
            });
        public string StepNumberBadgeText => Math.Clamp(CurrentStep + 1, 1, TotalSteps).ToString();
        public double WizardProgressValue => Math.Clamp(CurrentStep + 1, 1, TotalSteps);
        public bool IsReviewStep => CurrentStep == 0;
        public bool IsSummaryStep => CurrentStep == TotalSteps - 1;
        public string ExecuteButtonText => IsReviewStep
            ? GetText(UiTextKey.WizardConfirmConfigurationAction, "Confirm configuration")
            : IsSummaryStep
                ? GetText(UiTextKey.WizardFinishSetupAction, "Finish setup")
                : GetText(UiTextKey.WizardExecuteStepAction, "Execute step");
        public bool ConfigurationReady => _configurationValidation.IsValid;
        public bool PrerequisitesReady => _prerequisiteValidation.IsValid;
        public IReadOnlyList<string> ValidationIssues => _validationIssues;
        public bool HasValidationIssues => _validationIssues.Count > 0;
        public int ValidationIssueCount => _validationIssues.Count;
        public string ValidationIssueSummary => GetText(
            UiTextKey.WizardValidationIssueSummary,
            "Open checks: {count}",
            new Dictionary<string, object?> { ["count"] = ValidationIssueCount });
        public string ValidationDetailsAction => IsValidationDetailsExpanded
            ? GetText(UiTextKey.WizardHideValidationDetails, "Hide details")
            : GetText(UiTextKey.WizardShowValidationDetails, "Show details");
        public string ReviewSummary
        {
            get
            {
                var vars = Variables;
                return $"""
                {GetText(UiTextKey.WizardReviewCustomerLabel, "Customer")}
                {DisplayValue(vars.Customer)}

                {GetText(UiTextKey.WizardReviewCustomerGroupLabel, "Customer group")}
                {DisplayValue(vars.CustomerGroupName)}

                {GetText(UiTextKey.WizardReviewFallbackDomainLabel, "Fallback domain")}
                {DisplayValue(vars.MsFallbackDomain)}

                {GetText(UiTextKey.WizardReviewLanguageTimeZoneLabel, "Language and time zone")}
                {DisplayValue(vars.LanguageId)} · {DisplayValue(vars.TimeZoneId)}

                {GetText(UiTextKey.WizardReviewUsageLocationLabel, "Usage location")}
                {DisplayValue(vars.UsageLocation)}

                {GetText(UiTextKey.WizardReviewComputedNamesLabel, "Computed Teams Phone names")}
                {GetText(UiTextKey.WizardReviewM365GroupLabel, "M365 group")}: {DisplayComputedValue(vars.M365Group)}
                {GetText(UiTextKey.WizardReviewCallQueueAccountLabel, "Call queue account")}: {DisplayComputedValue(vars.RacqUPN)}
                {GetText(UiTextKey.WizardReviewAutoAttendantAccountLabel, "Auto attendant account")}: {DisplayComputedValue(vars.RaaaUPN)}

                {GetText(UiTextKey.WizardReviewPhoneNumberLabel, "Phone number")}
                {DisplayValue(vars.RaaAnr)} ({DisplayValue(vars.PhoneNumberType)})
                """;
            }
        }

        public string ValidationSummary
            => string.Join(Environment.NewLine, _validationIssues);

        public string ReadinessMessage
        {
            get
            {
                if (!ConfigurationReady)
                {
                    return GetText(
                        UiTextKey.WizardReadinessConfigurationRequired,
                        "Complete the required configuration in Configuration before continuing.");
                }

                if (!PrerequisitesReady)
                {
                    return GetText(
                        UiTextKey.WizardReadinessConnectionsRequired,
                        "Complete the connection checks in Get Started before provisioning.");
                }

                if (IsSummaryStep)
                {
                    return GetText(UiTextKey.WizardReadinessSummary, "Review the completed steps or add holidays next.");
                }

                if (IsReviewStep)
                {
                    return GetText(
                        UiTextKey.WizardReadinessReview,
                        "Review the configuration, then confirm it before starting setup.");
                }

                return GetText(
                    UiTextKey.WizardReadinessPreview,
                    "Preview this step before you run it. It changes the tenant only after you confirm it.");
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
            IAuditLog? auditLog = null,
            ITranslationService? translationService = null)
            : base(powerShellContextService, powerShellCommandService, loggingService,
                  sessionManager, navigationService, errorHandlingService, validationService, sharedStateService, dialogService, auditLog, translationService)
        {
            _planBuilder = planBuilder;
            _planExporter = planExporter;
            LogLocalized(
                UiTextKey.WizardPageLoadedLog,
                "Guided setup page loaded",
                LogLevel.Info);
            if (_translationService is not null)
            {
                _translationService.PropertyChanged += OnTranslationServicePropertyChanged;
            }
            InitializeSteps();
            UpdateCurrentStep();
        }

        private void InitializeSteps()
        {
            Steps = new ObservableCollection<WizardStepInfo>
            {
                CreateStep(0, UiTextKey.WizardReviewConfigurationTitle, "Review Configuration", UiTextKey.WizardReviewConfigurationDescription, "Verify all variables before starting the setup.", UiTextKey.WizardReviewConfigurationAction, "Settings"),
                CreateStep(1, UiTextKey.WizardCreateM365GroupTitle, "Create M365 Group", UiTextKey.WizardCreateM365GroupDescription, "Create the Microsoft 365 distribution group for call queue agents.", UiTextKey.WizardCreateM365GroupAction, "Execute"),
                CreateStep(2, UiTextKey.WizardCreateCallQueueResourceAccountTitle, "Create CQ Resource Account", UiTextKey.WizardCreateCallQueueResourceAccountDescription, "Create the resource account for the Call Queue.", UiTextKey.WizardCreateCallQueueResourceAccountAction, "Execute"),
                CreateStep(3, UiTextKey.WizardLicenseCallQueueResourceAccountTitle, "License CQ Resource Account", UiTextKey.WizardLicenseCallQueueResourceAccountDescription, "Set usage location and assign Teams Phone license to the CQ resource account.", UiTextKey.WizardLicenseCallQueueResourceAccountAction, "Execute"),
                CreateStep(4, UiTextKey.WizardCreateCallQueueTitle, "Create Call Queue", UiTextKey.WizardCreateCallQueueDescription, "Create the Call Queue and associate it with the resource account.", UiTextKey.WizardCreateCallQueueAction, "Execute"),
                CreateStep(5, UiTextKey.WizardCreateAutoAttendantResourceAccountTitle, "Create AA Resource Account", UiTextKey.WizardCreateAutoAttendantResourceAccountDescription, "Create the resource account for the Auto Attendant.", UiTextKey.WizardCreateAutoAttendantResourceAccountAction, "Execute"),
                CreateStep(6, UiTextKey.WizardLicenseAutoAttendantResourceAccountTitle, "License AA Resource Account", UiTextKey.WizardLicenseAutoAttendantResourceAccountDescription, "Set usage location, assign license, and assign phone number to the AA resource account.", UiTextKey.WizardLicenseAutoAttendantResourceAccountAction, "Execute"),
                CreateStep(7, UiTextKey.WizardCreateAutoAttendantTitle, "Create Auto Attendant", UiTextKey.WizardCreateAutoAttendantDescription, "Create call flows, schedule, and the Auto Attendant (runs as one script).", UiTextKey.WizardCreateAutoAttendantAction, "Execute"),
                CreateStep(8, UiTextKey.WizardAssociateAutoAttendantResourceAccountTitle, "Associate AA Resource Account", UiTextKey.WizardAssociateAutoAttendantResourceAccountDescription, "Associate the AA resource account with the Auto Attendant.", UiTextKey.WizardAssociateAutoAttendantResourceAccountAction, "Execute"),
                CreateStep(9, UiTextKey.WizardSetupCompleteTitle, "Setup Complete", UiTextKey.WizardSetupCompleteDescription, "All components have been created. You can now add holidays from the Holidays page.", UiTextKey.WizardSetupCompleteAction, "Summary"),
            };
            TotalSteps = Steps.Count;
        }

        private WizardStepInfo CreateStep(
            int stepNumber,
            UiTextKey titleKey,
            string titleFallback,
            UiTextKey descriptionKey,
            string descriptionFallback,
            UiTextKey actionKey,
            string actionFallback)
            => new(
                stepNumber,
                titleKey,
                titleFallback,
                descriptionKey,
                descriptionFallback,
                actionKey,
                actionFallback,
                (key, fallback) => GetText(key, fallback));

        private void RefreshReadiness()
        {
            _configurationValidation = _validationService.ValidateVariables(Variables);
            _prerequisiteValidation = _validationService.ValidatePrerequisites();
            RebuildValidationIssues();

            OnPropertyChanged(nameof(ConfigurationReady));
            OnPropertyChanged(nameof(PrerequisitesReady));
            OnPropertyChanged(nameof(ReadinessMessage));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanExecuteStep));
            GoToNextStepCommand.NotifyCanExecuteChanged();
        }

        private void RebuildValidationIssues()
        {
            var issues = new List<string>(_configurationValidation.Issues.Count + 1);
            foreach (var issue in _configurationValidation.Issues)
            {
                issues.Add(GetValidationErrorMessage(issue));
            }
            if (!PrerequisitesReady)
            {
                issues.Add(GetText(
                    UiTextKey.WizardValidationConnectionsIncomplete,
                    "Connection checks are incomplete. Open Get Started to connect to Teams and Microsoft Graph."));
            }

            _validationIssues = issues;
            if (!HasValidationIssues && IsValidationDetailsExpanded)
            {
                IsValidationDetailsExpanded = false;
            }

            OnPropertyChanged(nameof(ValidationIssues));
            OnPropertyChanged(nameof(HasValidationIssues));
            OnPropertyChanged(nameof(ValidationIssueCount));
            OnPropertyChanged(nameof(ValidationIssueSummary));
            OnPropertyChanged(nameof(ValidationSummary));
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
                : GetText(
                    UiTextKey.WizardReadinessConnectionsRequired,
                    "Complete the connection checks in Get Started before provisioning.");
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

            StatusMessage = GetText(
                UiTextKey.WizardCompleteOrSkipCurrentStepError,
                "Complete or skip this step before moving on.");
            return false;
        }

        private void UpdateCurrentStep()
        {
            if (CurrentStep >= 0 && CurrentStep < Steps.Count)
            {
                var step = Steps[CurrentStep];
                _stepResultKey = null;
                _stepResultFallback = null;
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
                    9 => GetText(
                        UiTextKey.WizardSetupCompleteScript,
                        "🎉 Setup complete! All Teams Phone components have been created."),
                    _ => string.Empty
                };
            }
            catch (Exception ex)
            {
                return GetText(
                    UiTextKey.WizardScriptPreviewError,
                    "# Error generating script preview:\n# {details}",
                    new Dictionary<string, object?> { ["details"] = ex.Message });
            }
        }

        private string FormatVariablesSummary(PhoneManagerVariables vars)
        {
            return GetText(
                UiTextKey.WizardConfigurationReviewScript,
                "# ======================================\n# Configuration Review\n# ======================================\n\n# Customer:          {customer}\n# Customer Group:    {customerGroup}\n# Fallback Domain:   {fallbackDomain}\n# Language:           {language}\n# Time Zone:          {timeZone}\n# Usage Location:     {usageLocation}\n\n# --- Computed Names ---\n# M365 Group:         {m365Group}\n# CQ Resource UPN:    {cqResourceUpn}\n# CQ Display Name:    {cqDisplayName}\n# AA Resource UPN:    {aaResourceUpn}\n# AA Display Name:    {aaDisplayName}\n# Phone Number:       {phoneNumber}\n# Phone Number Type:  {phoneNumberType}\n\n# --- Licensing ---\n# SKU ID:             {skuId}\n# CQ App ID:          {cqAppId}\n# AA App ID:          {aaAppId}\n\n# ======================================\n# Ensure all values are correct before\n# proceeding. Use the Variables page to\n# make changes.\n# ======================================",
                new Dictionary<string, object?>
                {
                    ["customer"] = DisplayValue(vars.Customer),
                    ["customerGroup"] = DisplayValue(vars.CustomerGroupName),
                    ["fallbackDomain"] = DisplayValue(vars.MsFallbackDomain),
                    ["language"] = DisplayValue(vars.LanguageId),
                    ["timeZone"] = DisplayValue(vars.TimeZoneId),
                    ["usageLocation"] = DisplayValue(vars.UsageLocation),
                    ["m365Group"] = DisplayComputedValue(vars.M365Group),
                    ["cqResourceUpn"] = DisplayComputedValue(vars.RacqUPN),
                    ["cqDisplayName"] = DisplayComputedValue(vars.CqDisplayName),
                    ["aaResourceUpn"] = DisplayComputedValue(vars.RaaaUPN),
                    ["aaDisplayName"] = DisplayComputedValue(vars.AaDisplayName),
                    ["phoneNumber"] = DisplayValue(vars.RaaAnr),
                    ["phoneNumberType"] = DisplayValue(vars.PhoneNumberType),
                    ["skuId"] = DisplayValue(vars.SkuId),
                    ["cqAppId"] = DisplayValue(vars.CsAppCqId),
                    ["aaAppId"] = DisplayValue(vars.CsAppAaId)
                });
        }

        private string DisplayValue(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? GetText(UiTextKey.WizardValueNotSet, "(not set)")
                : value;

        private string DisplayComputedValue(string? value)
            => string.IsNullOrWhiteSpace(value)
                || value is "ttgrp--" or "racq--" or "cq--" or "raaa---" or "aa---"
                ? GetText(UiTextKey.WizardValueNotAvailable, "(not available yet)")
                : value;

        private string BuildLicenseCqScript(PhoneManagerVariables vars)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# {GetText(
                UiTextKey.WizardScriptSetUsageLocationComment,
                "Step 1: Set usage location for {resource}",
                new Dictionary<string, object?> { ["resource"] = "call queue resource account" })}");
            sb.AppendLine(_powerShellCommandService.GetUpdateResourceAccountUsageLocationCommand(vars.RacqUPN, vars.UsageLocation));
            sb.AppendLine();
            sb.AppendLine($"# {GetText(UiTextKey.WizardScriptAssignLicenseComment, "Step 2: Assign Teams Phone license")}");
            sb.AppendLine(_powerShellCommandService.GetAssignLicenseCommand(vars.RacqUPN, vars.SkuId));
            return sb.ToString();
        }

        private string BuildLicenseAaScript(PhoneManagerVariables vars)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# {GetText(
                UiTextKey.WizardScriptSetUsageLocationComment,
                "Step 1: Set usage location for {resource}",
                new Dictionary<string, object?> { ["resource"] = "automatic attendant resource account" })}");
            sb.AppendLine(_powerShellCommandService.GetUpdateAutoAttendantResourceAccountUsageLocationCommand(vars.RaaaUPN, vars.UsageLocation));
            sb.AppendLine();
            sb.AppendLine($"# {GetText(UiTextKey.WizardScriptAssignLicenseComment, "Step 2: Assign Teams Phone license")}");
            sb.AppendLine(_powerShellCommandService.GetAssignAutoAttendantLicenseCommand(vars.RaaaUPN, vars.SkuId));
            sb.AppendLine();
            sb.AppendLine($"# {GetText(UiTextKey.WizardScriptAssignPhoneNumberComment, "Step 3: Assign phone number")}");
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
                SetLocalizedStepResult(UiTextKey.WizardConfigurationReviewedResult, "Configuration reviewed.");
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
                    SetLocalizedStepResult(
                        UiTextKey.WizardNotCompleteStatus,
                        "Setup is not complete. Return to the skipped or incomplete steps before finishing.");
                    StatusMessage = StepResult;
                    return;
                }

                step.IsCompleted = true;
                StepCompleted = true;
                SetLocalizedStepResult(UiTextKey.WizardSetupCompleteResult, "Setup complete!");
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
                StatusMessage = GetText(
                    UiTextKey.WizardNoScriptError,
                    "No script is available for this step.");
                return;
            }

            // Show preview and confirm before executing
            var previewTitle = GetText(
                UiTextKey.WizardStepPreviewTitle,
                "Wizard step {step}: {title}",
                new Dictionary<string, object?>
                {
                    ["step"] = CurrentStep + 1,
                    ["title"] = step.Title
                });
            var result = await PreviewAndExecuteAsync(script, previewTitle);

            if (result == null)
            {
                StatusMessage = GetText(
                    UiTextKey.WizardStepCancelledStatus,
                    "Step cancelled.");
                return;
            }

            var output = result.Value ?? string.Empty;

            if (result.HasErrorMarker)
            {
                step.IsFailed = true;
                step.Result = output;
                StepFailed = true;
                StepResult = output;
                StatusMessage = GetText(
                    UiTextKey.WizardStepFailedStatus,
                    "Step {step} failed. Review the output and retry or skip.",
                    new Dictionary<string, object?> { ["step"] = CurrentStep + 1 });
                LogLocalized(
                    UiTextKey.WizardStepFailedLog,
                    "Wizard step {step} ({title}) failed: {details}",
                    LogLevel.Error,
                    new Dictionary<string, object?> { ["step"] = CurrentStep + 1, ["title"] = step.Title, ["details"] = output });
            }
            else
            {
                step.IsCompleted = true;
                step.Result = output;
                StepCompleted = true;
                if (string.IsNullOrWhiteSpace(output))
                {
                    SetLocalizedStepResult(UiTextKey.WizardStepCompletedResult, "Step completed successfully.");
                }
                else
                {
                    _stepResultKey = null;
                    _stepResultFallback = null;
                    StepResult = output;
                }
                StatusMessage = GetText(
                    UiTextKey.WizardStepCompletedStatus,
                    "Step {step} completed: {title}",
                    new Dictionary<string, object?> { ["step"] = CurrentStep + 1, ["title"] = step.Title });
                LogLocalized(
                    UiTextKey.WizardStepCompletedLog,
                    "Wizard step {step} ({title}) completed successfully",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["step"] = CurrentStep + 1, ["title"] = step.Title });
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
                step.Result = GetText(
                    UiTextKey.WizardStepSkippedStatus,
                    "Skipped by user.");
                LogLocalized(
                    UiTextKey.WizardStepSkippedLog,
                    "Wizard step {step} ({title}) skipped",
                    LogLevel.Warning,
                    new Dictionary<string, object?> { ["step"] = CurrentStep + 1, ["title"] = step.Title });
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
        private void ToggleValidationDetails()
        {
            IsValidationDetailsExpanded = !IsValidationDetailsExpanded;
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
                StatusMessage = GetText(
                    UiTextKey.WizardPlanPreviewUnavailable,
                    "Plan preview is unavailable.");
                return;
            }

            Plan = _planBuilder.BuildWizardPlan(Variables);
            var entry = Plan.Entries.Count > 0 ? Plan.Entries[0] : null;
            if (entry != null && !entry.IsValid)
            {
                StatusMessage = GetText(
                    UiTextKey.WizardPlanGeneratedWithIssues,
                    "Plan generated with {count} validation issue(s). Review before executing.",
                    new Dictionary<string, object?> { ["count"] = entry.ValidationErrors.Count });
            }
            else
            {
                StatusMessage = GetText(
                    UiTextKey.WizardPlanGenerated,
                    "Plan generated: {objects} objects would be created or changed.",
                    new Dictionary<string, object?> { ["objects"] = Plan.TotalObjectCount });
            }
            LogLocalized(
                UiTextKey.WizardPlanGeneratedLog,
                "Wizard dry-run plan generated",
                LogLevel.Info);
        }

        [RelayCommand]
        private Task ExportPlanJsonAsync() => ExportPlanAsync(asJson: true);

        [RelayCommand]
        private Task ExportPlanCsvAsync() => ExportPlanAsync(asJson: false);

        private async Task ExportPlanAsync(bool asJson)
        {
            if (_planBuilder == null || _planExporter == null)
            {
                StatusMessage = GetText(
                    UiTextKey.WizardPlanExportUnavailable,
                    "Plan export is unavailable.");
                return;
            }

            Plan ??= _planBuilder.BuildWizardPlan(Variables);

            var content = asJson ? _planExporter.ToJson(Plan) : _planExporter.ToCsv(Plan);
            var extension = asJson ? "json" : "csv";
            var saved = await DryRunPlanExportHelper.SavePlanAsync(content, $"wizard-dry-run-plan.{extension}", extension, _translationService);
            if (saved != null)
            {
                StatusMessage = GetText(
                    UiTextKey.WizardPlanExportedStatus,
                    "Plan exported to {path}",
                    new Dictionary<string, object?> { ["path"] = saved });
                LogLocalized(
                    UiTextKey.WizardPlanExportedLog,
                    "Wizard dry-run plan exported to: {path}",
                    LogLevel.Info,
                    new Dictionary<string, object?> { ["path"] = saved });
            }
            else
            {
                StatusMessage = GetText(
                    UiTextKey.WizardPlanExportCancelled,
                    "Plan export cancelled.");
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

        private void OnTranslationServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ITranslationService.CurrentLanguage))
            {
                return;
            }

            foreach (var step in Steps)
            {
                step.RefreshText();
            }

            if (CurrentStep >= 0 && CurrentStep < Steps.Count)
            {
                StepTitle = Steps[CurrentStep].Title;
                StepDescription = Steps[CurrentStep].Description;
            }

            if (_stepResultKey is { } resultKey && _stepResultFallback is not null)
            {
                StepResult = GetText(resultKey, _stepResultFallback);
                if (CurrentStep >= 0 && CurrentStep < Steps.Count)
                {
                    Steps[CurrentStep].Result = StepResult;
                }
            }

            RebuildValidationIssues();

            OnPropertyChanged(nameof(StepNumberText));
            OnPropertyChanged(nameof(ExecuteButtonText));
            OnPropertyChanged(nameof(ReviewSummary));
            OnPropertyChanged(nameof(ValidationDetailsAction));
            OnPropertyChanged(nameof(ReadinessMessage));
        }

        partial void OnIsValidationDetailsExpandedChanged(bool value)
        {
            OnPropertyChanged(nameof(ValidationDetailsAction));
        }

        private void SetLocalizedStepResult(UiTextKey key, string fallback)
        {
            _stepResultKey = key;
            _stepResultFallback = fallback;
            StepResult = GetText(key, fallback);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_translationService is not null)
            {
                _translationService.PropertyChanged -= OnTranslationServicePropertyChanged;
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Represents a single wizard step with its state.
    /// </summary>
    public partial class WizardStepInfo : ObservableObject
    {
        public int StepNumber { get; }
        private readonly UiTextKey _titleKey;
        private readonly string _titleFallback;
        private readonly UiTextKey _descriptionKey;
        private readonly string _descriptionFallback;
        private readonly UiTextKey _stepTypeKey;
        private readonly string _stepTypeFallback;
        private readonly Func<UiTextKey, string, string> _getText;

        public string Title => _getText(_titleKey, _titleFallback);
        public string Description => _getText(_descriptionKey, _descriptionFallback);
        public string StepType => _getText(_stepTypeKey, _stepTypeFallback);

        [ObservableProperty]
        private bool _isCompleted;

        [ObservableProperty]
        private bool _isFailed;

        [ObservableProperty]
        private bool _isSkipped;

        [ObservableProperty]
        private string _result = string.Empty;

        public string StatusIcon => IsCompleted ? "✅" : IsFailed ? "❌" : IsSkipped ? "⏭️" : "⬜";

        public WizardStepInfo(
            int stepNumber,
            UiTextKey titleKey,
            string titleFallback,
            UiTextKey descriptionKey,
            string descriptionFallback,
            UiTextKey stepTypeKey,
            string stepTypeFallback,
            Func<UiTextKey, string, string>? getText = null)
        {
            StepNumber = stepNumber;
            _titleKey = titleKey;
            _titleFallback = titleFallback;
            _descriptionKey = descriptionKey;
            _descriptionFallback = descriptionFallback;
            _stepTypeKey = stepTypeKey;
            _stepTypeFallback = stepTypeFallback;
            _getText = getText ?? ((_, fallback) => fallback);
        }

        public void RefreshText()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(StepType));
        }

        partial void OnIsCompletedChanged(bool value) => OnPropertyChanged(nameof(StatusIcon));
        partial void OnIsFailedChanged(bool value) => OnPropertyChanged(nameof(StatusIcon));
        partial void OnIsSkippedChanged(bool value) => OnPropertyChanged(nameof(StatusIcon));
    }
}
