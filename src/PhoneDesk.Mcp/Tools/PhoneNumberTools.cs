using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;

namespace PhoneDesk.Mcp.Tools;

/// <summary>
/// The phone number inventory, and the assignment of a number to an employee or to a resource
/// account (an auto attendant or a call queue is reached through its resource account). The
/// checks and scripts are the ones the desktop app uses (<see cref="PhoneNumbers"/>).
/// </summary>
[McpServerToolType]
internal sealed class PhoneNumberTools
{
    private const string PlanIdDescription = "The planId from the first call. Omit to get the plan.";
    private const string TwoCalls = " CHANGES THE TENANT. Call it first without planId: it reads the number and the accounts, shows what would change and changes nothing. Show the result to the user, then call it again with the same arguments and the planId to run it. The plan is bound to what the first call showed: when the number or an account changed in between, the plan is refused.";

    private readonly TenantGateway _tenant;
    private readonly ChangeGate _changes;
    private readonly CreatedObjectGuard _guard;

    public PhoneNumberTools(TenantGateway tenant, ChangeGate changes, CreatedObjectGuard guard)
    {
        _tenant = tenant;
        _changes = changes;
        _guard = guard;
    }

    [McpServerTool(Name = "list_phone_numbers", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lists phone numbers of the tenant's inventory: the number, its type (DirectRouting, CallingPlan, OperatorConnect), whether it is assigned, what it can be used for (capabilities: UserAssignment for an employee, VoiceApplicationAssignment for a resource account), and the account that has it. Use it to find a free number or to see who has a number. A Direct Routing number is in the inventory only after it was assigned once. Owners are looked up for the first 100 accounts of a result; narrow the search to see the others. Read-only.")]
    public Task<string> ListPhoneNumbers(
        [Description("Only numbers that contain these digits, for example '4144' or the last four digits.")] string? search = null,
        [Description("'assigned', 'unassigned' or 'all' (default).")] string? status = null,
        [Description("Only numbers of this type: DirectRouting, CallingPlan or OperatorConnect.")] string? type = null,
        [Description("Maximum number of rows (default 50, at most 500).")] int limit = 50,
        [Description("Number of rows to skip, for the next page.")] int offset = 0,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var wantedType = string.IsNullOrWhiteSpace(type) ? null : PhoneNumbers.RequireType(type);
            var result = await _tenant.ReadAsync(PhoneNumbers.GetListCommand(search, status), "ListPhoneNumbers", cancellationToken);
            var read = PhoneNumbers.ParseList(string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput);
            var rows = read
                .Where(row => wantedType is null || string.Equals(row.Type, wantedType, StringComparison.OrdinalIgnoreCase))
                .OrderBy(row => row.Number, StringComparer.Ordinal)
                .ToList();
            var skip = Math.Max(0, offset);
            var page = rows.Skip(skip).Take(Math.Clamp(limit, 1, 500)).Select(RowView).ToList();
            return new
            {
                total = rows.Count,
                assigned = rows.Count(row => row.IsAssigned),
                unassigned = rows.Count(row => !row.IsAssigned),
                offset = skip,
                nextOffset = skip + page.Count < rows.Count ? skip + page.Count : (int?)null,
                // The inventory is read page by page up to a limit; at the limit there can be more numbers.
                possiblyIncomplete = read.Count >= PhoneNumbers.MaximumRows,
                note = read.Count >= PhoneNumbers.MaximumRows
                    ? $"The tenant has {PhoneNumbers.MaximumRows} numbers or more for this filter; only the first {PhoneNumbers.MaximumRows} were read. Narrow the search."
                    : null,
                numbers = page
            };
        });

    [McpServerTool(Name = "set_phone_number_assignment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Assigns a phone number to an employee or to a resource account, or moves it from the account that has it to another one. Name the target by its user principal name or object ID; for an auto attendant give the auto attendant's ID or exact name and the server takes its resource account (when it has several, name the resource account). The first call shows the number with its type and its current owner, the target with the number it has now, and what stops the change: a number that is not in the inventory (only a Direct Routing number can be new), a number that cannot be used for this kind of account, or a number that another account has. A number that another account has is moved only with reassign=true; the result then shows both accounts, and the old account loses the number. If the new account does not take the number, the script gives it back to the old account and says so. After the change the server reads the number again and reports who has it. The number stays in the inventory in every case." + TwoCalls)]
    public Task<string> SetPhoneNumberAssignment(
        [Description("Phone number in E.164 format, for example +41441234567.")] string phoneNumber,
        [Description("The account that gets the number: user principal name or object ID of a user or resource account, or the ID or exact name of an auto attendant.")] string target,
        [Description("DirectRouting, CallingPlan or OperatorConnect. Needed only for a Direct Routing number that is not in the inventory yet; otherwise the type of the inventory is used.")] string? phoneNumberType = null,
        [Description("True to move a number that another account has. The user must agree that the other account loses the number.")] bool reassign = false,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var number = PhoneNumbers.RequireNumber(phoneNumber);
            var identity = await ResolveTargetAsync(target, cancellationToken);
            var state = await CheckAsync(number, identity, cancellationToken);
            var assessment = PhoneNumbers.AssessAssign(state, number, phoneNumberType, reassign);
            var preview = Preview(assessment);
            if (assessment.Blockers.Count > 0)
            {
                return Blocked(assessment, preview);
            }

            if (assessment.NoChange)
            {
                return new { status = "noChange", success = true, note = $"{number} is assigned to this account already. Nothing was changed.", preview };
            }

            var targetLabel = assessment.Target!.Label;
            var summary = assessment.Kind == PhoneNumberChangeKind.Reassign
                ? $"Move {number} ({assessment.Type}) from {assessment.CurrentOwner?.Label ?? assessment.Row!.AssignedToId} to {targetLabel}. The first account loses the number."
                : $"Assign {number} ({assessment.Type}) to {targetLabel}.";
            return await RunAsync(assessment, number, identity, "SetPhoneNumberAssignment", targetLabel, summary, preview, planId, cancellationToken);
        });

    [McpServerTool(Name = "remove_phone_number_assignment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Takes a phone number off the employee or resource account that has it. The number is not released: it stays in the tenant's inventory as unassigned and can be assigned again (release_phone_number is the separate tool that removes a Direct Routing number from the inventory). The first call shows the number and the account that loses it; callers of that number then reach nobody. After the change the server reads the number again and reports its state." + TwoCalls)]
    public Task<string> RemovePhoneNumberAssignment(
        [Description("Phone number in E.164 format, for example +41441234567.")] string phoneNumber,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => McpJson.RunAsync(async () =>
        {
            var number = PhoneNumbers.RequireNumber(phoneNumber);
            var state = await CheckAsync(number, null, cancellationToken);
            var assessment = PhoneNumbers.AssessUnassign(state, number);
            var preview = Preview(assessment);
            if (assessment.Blockers.Count > 0)
            {
                return Blocked(assessment, preview);
            }

            if (assessment.NoChange)
            {
                return new { status = "noChange", success = true, note = $"{number} is not assigned to any account. Nothing was changed.", preview };
            }

            var owner = assessment.CurrentOwner?.Label ?? assessment.Row!.AssignedToId;
            return await RunAsync(
                assessment, number, null, "RemovePhoneNumberAssignment", owner,
                $"Take {number} ({assessment.Type}) off {owner}. The number stays in the inventory.", preview, planId, cancellationToken);
        });

    private async Task<object> RunAsync(
        PhoneNumberAssessment assessment,
        string number,
        string? targetIdentity,
        string operation,
        string target,
        string summary,
        object preview,
        string? planId,
        CancellationToken cancellationToken)
    {
        var applied = false;
        var outcome = await _changes.PlanOrApplyAsync(
            new ChangeRequest(operation, target, PhoneNumbers.GetChangeCommand(assessment), summary)
            {
                ApprovedState = PhoneNumbers.ApprovedState(assessment),
                OnSuccess = result =>
                {
                    applied = true;
                    // A Direct Routing number the tenant did not have before is recorded, so release_phone_number can remove it later.
                    if (assessment.Row is null && string.Equals(assessment.Type, "DirectRouting", StringComparison.OrdinalIgnoreCase))
                    {
                        _guard.Claim(TenantObjectType.PhoneNumber, number, operation, result.RawOutput);
                    }
                },
                // Teams can report an error after it did the work: the number is read again.
                ChangeWasMadeDespiteError = async (_, token) =>
                    PhoneNumbers.Verify(assessment, await CheckAsync(number, targetIdentity, token)).Length == 0
            },
            planId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(planId))
        {
            return new { preview, plan = outcome };
        }

        // Read in every case: after a move that failed the user must know which account has the number.
        var after = await CheckAsync(number, targetIdentity, cancellationToken);
        var problem = PhoneNumbers.Verify(assessment, after);
        return McpJson.With(outcome, "verification", new
        {
            asRequested = problem.Length == 0,
            problem = problem.Length == 0 ? null : problem,
            numberNow = after.Number is null ? null : RowView(after.Number),
            applied
        });
    }

    private async Task<PhoneNumberCheckState> CheckAsync(string number, string? targetIdentity, CancellationToken cancellationToken)
    {
        var result = await _tenant.ReadAsync(PhoneNumbers.GetCheckCommand(number, targetIdentity), "CheckPhoneNumber", cancellationToken);
        return PhoneNumbers.ParseCheck(string.IsNullOrEmpty(result.RawOutput) ? result.Value : result.RawOutput);
    }

    /// <summary>
    /// A target is an account, or an auto attendant that is reached through its one resource
    /// account. An auto attendant with several resource accounts is refused: the caller names one.
    /// </summary>
    private async Task<string> ResolveTargetAsync(string target, CancellationToken cancellationToken)
    {
        var wanted = (target ?? string.Empty).Trim();
        if (wanted.Length == 0)
        {
            throw new ArgumentException("The target is missing.");
        }

        var topology = await _tenant.GetTopologyAsync(false, cancellationToken);
        var attendants = topology.AutoAttendants
            .Where(item => HolidayCallHandling.SameId(item.Identity, wanted) || string.Equals(item.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (attendants.Count > 1)
        {
            throw new InvalidOperationException($"{attendants.Count} auto attendants are named '{wanted}'. Give the ID, or the resource account.");
        }

        if (attendants.Count == 1)
        {
            var accounts = attendants[0].ResourceAccountObjectIds;
            if (accounts.Count != 1)
            {
                var names = topology.ResourceAccounts
                    .Where(account => accounts.Contains(account.ObjectId, StringComparer.OrdinalIgnoreCase))
                    .Select(account => account.UserPrincipalName);
                throw new InvalidOperationException(accounts.Count == 0
                    ? $"Auto attendant '{attendants[0].Name}' has no resource account. Associate one first (associate_resource_account)."
                    : $"Auto attendant '{attendants[0].Name}' has {accounts.Count} resource accounts ({string.Join(", ", names)}). Name the one that gets the number.");
            }

            return PhoneNumbers.RequireIdentity(accounts[0]);
        }

        return PhoneNumbers.RequireIdentity(wanted);
    }

    private static object Blocked(PhoneNumberAssessment assessment, object preview)
        => new
        {
            status = "blocked",
            success = false,
            error = "The change cannot be made: " + string.Join(" ", assessment.Blockers.Select(blocker => blocker.Message)),
            blockers = assessment.Blockers,
            preview,
            next = "Nothing was changed and no plan was made. Remove the cause or change the arguments, then call the tool again."
        };

    private static object Preview(PhoneNumberAssessment assessment)
        => new
        {
            change = assessment.Kind.ToString(),
            number = assessment.Number,
            type = assessment.Type,
            inInventory = assessment.Row is not null,
            numberNow = assessment.Row is null ? null : RowView(assessment.Row),
            losesTheNumber = assessment.Kind is PhoneNumberChangeKind.Reassign or PhoneNumberChangeKind.Unassign ? OwnerView(assessment.CurrentOwner) : null,
            getsTheNumber = OwnerView(assessment.Target),
            warnings = assessment.Warnings
        };

    private static object RowView(PhoneNumberRow row)
        => new
        {
            number = row.Number,
            type = row.Type,
            status = row.Status,
            assigned = row.IsAssigned,
            capabilities = row.Capabilities,
            blockedForAssignment = row.IsBlockedForAssignment ? row.AssignmentBlockedState : null,
            city = string.IsNullOrWhiteSpace(row.City) ? null : row.City,
            assignedToId = row.IsAssigned ? row.AssignedToId : null,
            assignedTo = OwnerView(row.Owner)
        };

    private static object? OwnerView(NumberOwner? owner)
        => owner is null
            ? null
            : new
            {
                id = owner.Id,
                userPrincipalName = owner.UserPrincipalName,
                displayName = owner.DisplayName,
                kind = owner.IsResourceAccount ? "resourceAccount" : "user",
                phoneNumberNow = string.IsNullOrWhiteSpace(owner.PhoneNumber) ? null : owner.PhoneNumber
            };
}
