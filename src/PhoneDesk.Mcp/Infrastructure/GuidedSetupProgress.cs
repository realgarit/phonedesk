using PhoneDesk.Operations;

namespace PhoneDesk.Mcp;

internal enum GuidedStepStatus
{
    Pending,
    Completed,
    Failed,
    Skipped
}

internal sealed class GuidedStepState
{
    public GuidedStepStatus Status { get; set; }

    public string? Error { get; set; }

    /// <summary>Fingerprint of the script the step ran. A different current value means the configuration changed since.</summary>
    public string? Fingerprint { get; set; }
}

/// <summary>
/// Guided Setup progress for this server session. The step order is fixed: a step can run only
/// when every earlier step is completed or skipped.
/// </summary>
internal sealed class GuidedSetupProgress
{
    private readonly Dictionary<int, GuidedStepState> _steps = Enumerable
        .Range(
            GuidedSetupScriptFactory.FirstProvisioningStep,
            GuidedSetupScriptFactory.LastProvisioningStep - GuidedSetupScriptFactory.FirstProvisioningStep + 1)
        .ToDictionary(step => step, _ => new GuidedStepState());

    public static IReadOnlyList<(int Step, string Title, string Description)> Steps { get; } = new[]
    {
        (1, "Create M365 group", "Create the Microsoft 365 group for the call queue agents."),
        (2, "Create call queue resource account", "Create the resource account for the call queue."),
        (3, "License call queue resource account", "Set the usage location and assign the Teams Phone resource account license."),
        (4, "Create call queue", "Create the call queue and associate it with its resource account. Members of the agent group can wait up to eight hours for their first call (a delay in Teams); tell the user."),
        (5, "Create auto attendant resource account", "Create the resource account for the auto attendant."),
        (6, "License auto attendant resource account", "Set the usage location, assign the license and assign the phone number."),
        (7, "Create auto attendant", "Create the call flows, the after-hours schedule and the auto attendant. During opening hours calls go to the call queue of this configuration unless autoAttendantTemplate.defaultAction says otherwise."),
        (8, "Associate auto attendant resource account", "Associate the resource account with the auto attendant. When callQueueTemplate.useAutoAttendantNumberAsCallerId is true, also set this resource account as outbound caller ID of the call queue.")
    };

    public GuidedStepState Get(int step)
        => _steps.TryGetValue(step, out var state)
            ? state
            : throw new ArgumentException(
                $"Step {step} is not a Guided Setup step. Use {GuidedSetupScriptFactory.FirstProvisioningStep} to {GuidedSetupScriptFactory.LastProvisioningStep}.");

    /// <summary>The first step that is neither completed nor skipped, or null when all are done.</summary>
    public int? NextStep
        => _steps
            .Where(entry => entry.Value.Status is GuidedStepStatus.Pending or GuidedStepStatus.Failed)
            .Select(entry => (int?)entry.Key)
            .Min();

    public bool CanRun(int step)
    {
        Get(step);
        return _steps
            .Where(entry => entry.Key < step)
            .All(entry => entry.Value.Status is GuidedStepStatus.Completed or GuidedStepStatus.Skipped);
    }

    public bool AllCompleted => _steps.Values.All(state => state.Status == GuidedStepStatus.Completed);

    public void Reset()
    {
        foreach (var state in _steps.Values)
        {
            state.Status = GuidedStepStatus.Pending;
            state.Error = null;
            state.Fingerprint = null;
        }
    }
}
