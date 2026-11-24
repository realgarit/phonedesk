using System.Security.Cryptography;
using System.Text;
using PhoneDesk.Models;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Operations;

/// <summary>The Guided Setup steps, in the order they run.</summary>
public enum GuidedSetupStep
{
    ReviewConfiguration = 0,
    CreateM365Group = 1,
    CreateCallQueueResourceAccount = 2,
    LicenseCallQueueResourceAccount = 3,
    CreateCallQueue = 4,
    CreateAutoAttendantResourceAccount = 5,
    LicenseAutoAttendantResourceAccount = 6,
    CreateAutoAttendant = 7,
    AssociateAutoAttendantResourceAccount = 8,
    Summary = 9
}

/// <summary>
/// Comment lines for the two licensing steps, which join several commands into one script.
/// Comments are display text only: a UI may translate them, and they are not part of the fingerprint.
/// </summary>
public sealed record GuidedSetupScriptComments(
    string SetCallQueueUsageLocation,
    string SetAutoAttendantUsageLocation,
    string AssignLicense,
    string AssignPhoneNumber)
{
    public static GuidedSetupScriptComments English { get; } = new(
        "Step 1: Set usage location for call queue resource account",
        "Step 1: Set usage location for automatic attendant resource account",
        "Step 2: Assign Teams Phone license",
        "Step 3: Assign phone number");
}

/// <summary>Builds the script each Guided Setup provisioning step sends to the tenant.</summary>
public sealed class GuidedSetupScriptFactory
{
    public const int FirstProvisioningStep = (int)GuidedSetupStep.CreateM365Group;
    public const int LastProvisioningStep = (int)GuidedSetupStep.AssociateAutoAttendantResourceAccount;

    private readonly IPowerShellCommandService _commands;

    public GuidedSetupScriptFactory(IPowerShellCommandService commands)
    {
        _commands = commands;
    }

    public static bool IsProvisioningStep(int step)
        => step >= FirstProvisioningStep && step <= LastProvisioningStep;

    /// <summary>Returns the script for a provisioning step, or an empty string for any other step.</summary>
    public string GetProvisioningScript(
        int step,
        IPhoneManagerVariables variables,
        GuidedSetupScriptComments? comments = null)
    {
        ArgumentNullException.ThrowIfNull(variables);
        comments ??= GuidedSetupScriptComments.English;

        return (GuidedSetupStep)step switch
        {
            GuidedSetupStep.CreateM365Group => _commands.GetCreateM365GroupCommand(variables.M365Group),
            GuidedSetupStep.CreateCallQueueResourceAccount => _commands.GetCreateResourceAccountCommand(variables),
            GuidedSetupStep.LicenseCallQueueResourceAccount => BuildLicenseCallQueueScript(variables, comments),
            GuidedSetupStep.CreateCallQueue => _commands.GetCreateCallQueueCommand(variables),
            GuidedSetupStep.CreateAutoAttendantResourceAccount =>
                _commands.GetCreateAutoAttendantResourceAccountCommand(variables),
            GuidedSetupStep.LicenseAutoAttendantResourceAccount => BuildLicenseAutoAttendantScript(variables, comments),
            GuidedSetupStep.CreateAutoAttendant => _commands.GetCreateAutoAttendantCommand(variables),
            GuidedSetupStep.AssociateAutoAttendantResourceAccount =>
                _commands.GetAssociateResourceAccountWithAutoAttendantCommand(variables.RaaaUPN, variables.AaDisplayName),
            _ => string.Empty
        };
    }

    /// <summary>
    /// Fingerprint of everything a step sends to the tenant: a hash of its script without comment
    /// lines. Any configuration value the step uses changes it; values the step does not use do not.
    /// </summary>
    public static string ComputeFingerprint(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var lines = script
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'));
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private string BuildLicenseCallQueueScript(IPhoneManagerVariables variables, GuidedSetupScriptComments comments)
    {
        var script = new StringBuilder();
        script.AppendLine($"# {comments.SetCallQueueUsageLocation}");
        script.AppendLine(_commands.GetUpdateResourceAccountUsageLocationCommand(variables.RacqUPN, variables.UsageLocation));
        script.AppendLine();
        script.AppendLine($"# {comments.AssignLicense}");
        script.AppendLine(_commands.GetAssignLicenseCommand(variables.RacqUPN, variables.SkuId));
        return script.ToString();
    }

    private string BuildLicenseAutoAttendantScript(IPhoneManagerVariables variables, GuidedSetupScriptComments comments)
    {
        var script = new StringBuilder();
        script.AppendLine($"# {comments.SetAutoAttendantUsageLocation}");
        script.AppendLine(_commands.GetUpdateAutoAttendantResourceAccountUsageLocationCommand(variables.RaaaUPN, variables.UsageLocation));
        script.AppendLine();
        script.AppendLine($"# {comments.AssignLicense}");
        script.AppendLine(_commands.GetAssignAutoAttendantLicenseCommand(variables.RaaaUPN, variables.SkuId));
        script.AppendLine();
        script.AppendLine($"# {comments.AssignPhoneNumber}");
        script.AppendLine(_commands.GetAssignPhoneNumberToAutoAttendantCommand(variables.RaaaUPN, variables.RaaAnr, variables.PhoneNumberType));
        return script.ToString();
    }
}
