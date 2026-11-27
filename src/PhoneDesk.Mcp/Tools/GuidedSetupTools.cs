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
    private readonly IDryRunPlanBuilder _planBuilder;
    private readonly IDryRunPlanExporter _planExporter;
    private readonly TenantConnectionService _connection;
    private readonly TenantGateway _tenant;
    private readonly IPowerShellCommandService _commands;
    private readonly McpRunJournal _runs;

    public GuidedSetupTools(
        ConfigurationStore store,
        GuidedSetupProgress progress,
        GuidedSetupScriptFactory scripts,
        ChangeGate changes,
        CreatedObjectGuard guard,
        IDryRunPlanBuilder planBuilder,
        IDryRunPlanExporter planExporter,
        TenantConnectionService connection,
        TenantGateway tenant,
        IPowerShellCommandService commands,
        McpRunJournal runs)
    {
        _tenant = tenant;
        _commands = commands;
        _runs = runs;
        _store = store;
        _progress = progress;
        _scripts = scripts;
        _changes = changes;
        _guard = guard;
        _planBuilder = planBuilder;
        _planExporter = planExporter;
        _connection = connection;
    }

    [McpServerTool(Name = "guided_setup_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows the Guided Setup: the eight steps that build a complete phone system from the configuration (agent group, call queue with resource account and license, auto attendant with resource account, license, phone number and call flows), the state of each step, which step is next, and whether the configuration and the connections are ready. Steps run in order.")]
    public string Status() => McpJson.Run(() =>
    {
        var variables = _store.Variables;
        var problems = _store.Problems();
        return new
        {
            connected = _connection.IsReady,
            configurationValid = problems.Count == 0,
            configurationProblems = problems.Select(problem => problem.Message),
            nextStep = _progress.NextStep,
            allCompleted = _progress.AllCompleted,
            steps = GuidedSetupProgress.Steps.Select(step =>
            {
                var state = _progress.Get(step.Step);
                var script = ScriptFor(step.Step, variables);
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

    [McpServerTool(Name = "guided_setup_preview", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Returns the dry-run plan of the whole Guided Setup: every object that would be created or assigned with its resolved settings, plus preflight checks. Without checkTenant it is built from the configuration only and nothing is sent to the tenant. With checkTenant=true (needs both connections) it also reads the tenant: which of the names and accounts exist already (the setup reuses them: a warning), whether enough licenses are free, and whether the phone number is free. A check with the status Fail means that step would fail. Use checkTenant=true before the first step. Read-only.")]
    public Task<string> Preview(
        [Description("True to compare the plan with what the tenant holds. Needs both connections.")] bool checkTenant = false,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var plan = _planBuilder.BuildWizardPlan(_store.Variables);
            if (checkTenant)
            {
                var sources = new[] { _store.Variables };
                var read = await _tenant.ReadAsync(_commands.GetTenantPreflightCommand(sources), "PreviewTenantCheck", cancellationToken);
                plan = TenantPreflight.Apply(plan, sources, TenantPreflight.Parse(string.IsNullOrEmpty(read.RawOutput) ? read.Value : read.RawOutput));
            }

            return (object)JsonNode.Parse(_planExporter.ToJson(plan))!;
        });

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
            _store.RequireUsable();

            if (!_progress.CanRun(step))
            {
                throw new InvalidOperationException(
                    $"Step {step} cannot run yet. Complete or skip step {_progress.NextStep} first.");
            }

            var script = ScriptFor(step, variables);
            var definition = GuidedSetupProgress.Steps.First(item => item.Step == step);
            var created = CreatedObjectFor((GuidedSetupStep)step, variables);
            var lateErrorCheck = created is { Type: TenantObjectType.AutoAttendant }
                ? await _guard.CreatedDespiteLateModuleErrorAsync(
                    created.Value.Type, created.Value.Key, applying: !string.IsNullOrWhiteSpace(planId), cancellationToken)
                : null;
            return await _changes.PlanOrApplyAsync(
                new ChangeRequest($"GuidedSetupStep{step}", TargetFor((GuidedSetupStep)step, variables), script, $"Step {step}: {definition.Title}. {definition.Description}")
                {
                    // Any error record fails the step, even when the script printed its own SUCCESS line first.
                    IsSuccess = result => result.IsSuccess,
                    OnSuccess = result =>
                    {
                        state.Status = GuidedStepStatus.Completed;
                        state.Error = null;
                        state.Fingerprint = GuidedSetupScriptFactory.ComputeFingerprint(script);
                    },
                    OnSuccessAsync = async (result, token) =>
                    {
                        if (created is not null)
                        {
                            await _guard.ClaimAsync(created.Value.Type, created.Value.Key, $"GuidedSetupStep{step}", result.RawOutput, token);
                        }

                        await _runs.StepAsync(
                            (GuidedSetupStep)step, true, variables, GuidedSetupScriptFactory.ComputeFingerprint(script),
                            string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput, null, token);
                        if (_progress.AllCompleted)
                        {
                            _runs.Complete();
                        }
                    },
                    OnFailure = result =>
                    {
                        state.Status = GuidedStepStatus.Failed;
                        state.Error = result.ErrorMessage;
                        state.Fingerprint = null;
                    },
                    // A failed script can still have created the object; the guard then confirms it in the tenant.
                    OnFailureAsync = async (result, token) =>
                    {
                        if (created is not null)
                        {
                            await _guard.ClaimAfterFailureAsync(created.Value.Type, created.Value.Key, $"GuidedSetupStep{step}", result, token);
                        }

                        await _runs.StepAsync(
                            (GuidedSetupStep)step, false, variables, string.Empty,
                            string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput, result.ErrorMessage, token);
                    },
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
        _runs.Reset();
        return new { success = true, nextStep = _progress.NextStep };
    });

    [McpServerTool(Name = "guided_setup_resume", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Takes up a Guided Setup run that did not finish, for example after the server was restarted or a step failed in an earlier session. The steps the run completed count as completed again when their script is the one the current configuration gives; a step that ran with other configuration values stays open and must run again. Nothing is sent to the tenant. Without runId it takes the newest unfinished Guided Setup run of the connected tenant. Use list_setup_runs to see the runs, and guided_setup_status afterwards for the next step. To remove what a run created instead, use rollback_setup_run.")]
    public string Resume(
        [Description("The id of the run, from list_setup_runs. Omit for the newest unfinished run of the connected tenant.")] string? runId = null)
        => McpJson.Run(() =>
        {
            _tenant.RequireReady();
            var record = string.IsNullOrWhiteSpace(runId)
                ? _runs.Journal.Open(_tenant.TenantId, RunKind.GuidedSetup).FirstOrDefault()
                : _runs.Journal.Find(runId);
            if (record is null || record.Kind != RunKind.GuidedSetup)
            {
                throw new InvalidOperationException("There is no unfinished Guided Setup run to resume. Use list_setup_runs.");
            }

            if (!string.Equals(record.TenantId, _tenant.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The run '{record.Label}' belongs to another tenant ({record.TenantName} {record.TenantId}).");
            }

            if (!record.IsOpen)
            {
                throw new InvalidOperationException($"The run '{record.Label}' is {record.State}; only a run that did not finish can be resumed.");
            }

            _progress.Reset();
            var variables = _store.Variables;
            var restored = new List<int>();
            var mustRunAgain = new List<int>();
            foreach (var (step, fingerprint) in record.CompletedSteps.OrderBy(entry => entry.Key))
            {
                if (step < GuidedSetupScriptFactory.FirstProvisioningStep || step > GuidedSetupScriptFactory.LastProvisioningStep)
                {
                    continue;
                }

                if (string.Equals(fingerprint, GuidedSetupScriptFactory.ComputeFingerprint(ScriptFor(step, variables)), StringComparison.Ordinal))
                {
                    var state = _progress.Get(step);
                    state.Status = GuidedStepStatus.Completed;
                    state.Fingerprint = fingerprint;
                    restored.Add(step);
                }
                else
                {
                    mustRunAgain.Add(step);
                }
            }

            _runs.Adopt(record);
            return new
            {
                success = true,
                run = RunTools.View(record),
                restoredSteps = restored,
                stepsThatMustRunAgain = mustRunAgain,
                note = mustRunAgain.Count == 0 ? null : "These steps ran with other configuration values. They reuse objects that exist, so running them again creates no duplicates.",
                nextStep = _progress.NextStep
            };
        });

    /// <summary>
    /// The script of a step. The call queue step gets the agent group check in front of it, so it
    /// cannot take the group of an earlier step that was run for another configuration.
    /// </summary>
    private string ScriptFor(int step, ConfigurationDocumentVariables variables)
    {
        var script = _scripts.GetProvisioningScript(step, variables);
        return (GuidedSetupStep)step == GuidedSetupStep.CreateCallQueue
            ? AgentGroupCheck.InFrontOf(script, variables.M365Group)
            : script;
    }

    /// <summary>The object a step works on, for the plan, the result and the audit log.</summary>
    private static string TargetFor(GuidedSetupStep step, ConfigurationDocumentVariables variables)
        => step switch
        {
            GuidedSetupStep.CreateM365Group => variables.M365Group,
            GuidedSetupStep.CreateCallQueueResourceAccount or GuidedSetupStep.LicenseCallQueueResourceAccount => variables.RacqUPN,
            GuidedSetupStep.CreateCallQueue => variables.CqDisplayName,
            GuidedSetupStep.CreateAutoAttendantResourceAccount or GuidedSetupStep.LicenseAutoAttendantResourceAccount => variables.RaaaUPN,
            _ => variables.AaDisplayName
        };

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
