using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Mcp.Tools;

[McpServerToolType]
internal sealed class GuidedSetupTools
{
    private readonly ConfigurationStore _store;
    private readonly GuidedSetupProgress _progress;
    private readonly GuidedSetupScriptFactory _scripts;
    private readonly ChangeGate _changes;
    private readonly CreatedObjectGuard _guard;
    private readonly IValidationService _validation;
    private readonly IDryRunPlanBuilder _planBuilder;
    private readonly IDryRunPlanExporter _planExporter;
    private readonly TenantConnectionService _connection;

    public GuidedSetupTools(
        ConfigurationStore store,
        GuidedSetupProgress progress,
        GuidedSetupScriptFactory scripts,
        ChangeGate changes,
        CreatedObjectGuard guard,
        IValidationService validation,
        IDryRunPlanBuilder planBuilder,
        IDryRunPlanExporter planExporter,
        TenantConnectionService connection)
    {
        _store = store;
        _progress = progress;
        _scripts = scripts;
        _changes = changes;
        _guard = guard;
        _validation = validation;
        _planBuilder = planBuilder;
        _planExporter = planExporter;
        _connection = connection;
    }

    [McpServerTool(Name = "guided_setup_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows the Guided Setup: the eight steps that build a complete phone system from the configuration (agent group, call queue with resource account and license, auto attendant with resource account, license, phone number and call flows), the state of each step, which step is next, and whether the configuration and the connections are ready. Steps run in order.")]
    public string Status() => McpJson.Run(() =>
    {
        var variables = _store.Variables;
        var validation = _validation.ValidateVariables(variables);
        return new
        {
            connected = _connection.IsReady,
            configurationValid = validation.IsValid,
            configurationProblems = validation.Issues.Select(issue => issue.Message),
            nextStep = _progress.NextStep,
            allCompleted = _progress.AllCompleted,
            steps = GuidedSetupProgress.Steps.Select(step =>
            {
                var state = _progress.Get(step.Step);
                var script = _scripts.GetProvisioningScript(step.Step, variables);
                return new
                {
                    step = step.Step,
                    title = step.Title,
                    description = step.Description,
                    status = state.Status,
                    canRun = _progress.CanRun(step.Step),
                    error = state.Error,
                    configurationChangedSinceRun = state.Fingerprint is not null &&
                        !string.Equals(state.Fingerprint, GuidedSetupScriptFactory.ComputeFingerprint(script), StringComparison.Ordinal)
                };
            })
        };
    });

    [McpServerTool(Name = "guided_setup_preview", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the dry-run plan of the whole Guided Setup: every object that would be created or assigned with its resolved settings, plus preflight checks. Built from the configuration only; nothing is sent to the tenant.")]
    public string Preview() => McpJson.Run(() =>
        (object)JsonNode.Parse(_planExporter.ToJson(_planBuilder.BuildWizardPlan(_store.Variables)))!);

    [McpServerTool(Name = "guided_setup_run_step", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Runs one Guided Setup step in the tenant. CHANGES THE TENANT. Call it first without planId: it returns the exact script as a plan and changes nothing. Show the plan to the user, then call it again with the planId to run it. Steps reuse objects that already exist, so running a step again does not create duplicates. Steps must run in order (1 to 8).")]
    public Task<string> RunStep(
        [Description("Step number, 1 to 8. See guided_setup_status.")] int step,
        [Description("The planId from the first call. Omit to get the plan.")] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var state = _progress.Get(step);
            var variables = _store.Variables;
            var validation = _validation.ValidateVariables(variables);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(
                    "The configuration is not valid: " + string.Join(" ", validation.Issues.Select(issue => issue.Message)));
            }

            if (!_progress.CanRun(step))
            {
                throw new InvalidOperationException(
                    $"Step {step} cannot run yet. Complete or skip step {_progress.NextStep} first.");
            }

            var script = _scripts.GetProvisioningScript(step, variables);
            var definition = GuidedSetupProgress.Steps.First(item => item.Step == step);
            var created = CreatedObjectFor((GuidedSetupStep)step, variables);
            var lateErrorCheck = created is { Type: TenantObjectType.AutoAttendant }
                ? await _guard.CreatedDespiteLateModuleErrorAsync(
                    created.Value.Type, created.Value.Key, applying: !string.IsNullOrWhiteSpace(planId), cancellationToken)
                : null;
            return await _changes.PlanOrApplyAsync(
                new ChangeRequest($"GuidedSetupStep{step}", created?.Key ?? variables.AaDisplayName, script, $"Step {step}: {definition.Title}. {definition.Description}")
                {
                    // Any error record fails the step, even when the script printed its own SUCCESS line first.
                    IsSuccess = result => result.IsSuccess,
                    OnSuccess = result =>
                    {
                        state.Status = GuidedStepStatus.Completed;
                        state.Error = null;
                        state.Fingerprint = GuidedSetupScriptFactory.ComputeFingerprint(script);
                    },
                    OnSuccessAsync = created is null
                        ? null
                        : (result, token) => _guard.ClaimAsync(
                            created.Value.Type, created.Value.Key, $"GuidedSetupStep{step}", result.RawOutput, token),
                    OnFailure = result =>
                    {
                        state.Status = GuidedStepStatus.Failed;
                        state.Error = result.ErrorMessage;
                        state.Fingerprint = null;
                    },
                    // A failed script can still have created the object; the guard then confirms it in the tenant.
                    OnFailureAsync = created is null
                        ? null
                        : (result, token) => _guard.ClaimAfterFailureAsync(
                            created.Value.Type, created.Value.Key, $"GuidedSetupStep{step}", result, token),
                    ChangeWasMadeDespiteError = lateErrorCheck
                },
                planId,
                cancellationToken);
        });

    [McpServerTool(Name = "guided_setup_skip_step", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Marks a Guided Setup step as skipped so the next step can run, for example when the object already exists and must not be touched. Nothing is sent to the tenant. A skipped step keeps the setup incomplete.")]
    public string SkipStep([Description("Step number, 1 to 8.")] int step) => McpJson.Run(() =>
    {
        var state = _progress.Get(step);
        if (!_progress.CanRun(step))
        {
            throw new InvalidOperationException(
                $"Step {step} cannot be skipped yet. Complete or skip step {_progress.NextStep} first.");
        }

        if (state.Status != GuidedStepStatus.Completed)
        {
            state.Status = GuidedStepStatus.Skipped;
            state.Error = null;
        }

        return new { success = true, step, status = state.Status, nextStep = _progress.NextStep };
    });

    [McpServerTool(Name = "guided_setup_reset", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Clears the Guided Setup progress of this session. Objects that already exist in the tenant stay unchanged; steps that run again reuse them.")]
    public string Reset() => McpJson.Run(() =>
    {
        _progress.Reset();
        return new { success = true, nextStep = _progress.NextStep };
    });

    private static (TenantObjectType Type, string Key)? CreatedObjectFor(GuidedSetupStep step, ConfigurationDocumentVariables variables)
        => step switch
        {
            GuidedSetupStep.CreateM365Group => (TenantObjectType.M365Group, variables.M365Group),
            GuidedSetupStep.CreateCallQueueResourceAccount => (TenantObjectType.ResourceAccount, variables.RacqUPN),
            GuidedSetupStep.CreateCallQueue => (TenantObjectType.CallQueue, variables.CqDisplayName),
            GuidedSetupStep.CreateAutoAttendantResourceAccount => (TenantObjectType.ResourceAccount, variables.RaaaUPN),
            GuidedSetupStep.LicenseAutoAttendantResourceAccount => (TenantObjectType.PhoneNumber, variables.RaaAnr.Trim()),
            GuidedSetupStep.CreateAutoAttendant => (TenantObjectType.AutoAttendant, variables.AaDisplayName),
            _ => null
        };
}
