using System.ComponentModel;
using ModelContextProtocol.Server;
using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;

namespace PhoneDesk.Mcp.Tools;

/// <summary>
/// Delete operations. Each one accepts only an object this server created in the connected
/// tenant, unless the operator set PHONEDESK_MCP_ALLOW_DELETE_EXISTING=1 for the server.
/// </summary>
[McpServerToolType]
internal sealed class DeleteTools
{
    private const string PlanIdDescription = "The planId from the first call. Omit to get the plan.";
    private const string DeleteNote = " DELETES FROM THE TENANT. By default only objects this server created can be deleted (see created_objects). Call it first without planId: it returns the exact script as a plan and deletes nothing. Show the plan to the user and get a clear yes, then call it again with the same arguments and the planId.";

    private readonly IPowerShellCommandService _commands;
    private readonly ChangeGate _changes;
    private readonly CreatedObjectGuard _guard;
    private readonly TenantGateway _tenant;

    public DeleteTools(
        IPowerShellCommandService commands,
        ChangeGate changes,
        CreatedObjectGuard guard,
        TenantGateway tenant)
    {
        _commands = commands;
        _changes = changes;
        _guard = guard;
        _tenant = tenant;
    }

    [McpServerTool(Name = "delete_call_queue", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Deletes a call queue by name." + DeleteNote)]
    public Task<string> DeleteCallQueue(
        [Description("Exact call queue name.")] string name,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => DeleteAsync(TenantObjectType.CallQueue, name, "RemoveCallQueue",
            key => _commands.GetRemoveCallQueueCommand(key), $"Delete call queue '{name}'.", planId, cancellationToken);

    [McpServerTool(Name = "delete_auto_attendant", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Deletes an auto attendant by name. Its resource account associations are released and the schedules attached to it are removed, also a holiday schedule that was created separately and attached; such a schedule needs no delete_holiday_schedule call afterwards." + DeleteNote)]
    public Task<string> DeleteAutoAttendant(
        [Description("Exact auto attendant name.")] string name,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => DeleteAsync(TenantObjectType.AutoAttendant, name, "RemoveAutoAttendant",
            key => _commands.GetRemoveAutoAttendantCommand(key), $"Delete auto attendant '{name}'.", planId, cancellationToken);

    [McpServerTool(Name = "delete_holiday_schedule", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Deletes a holiday schedule by name." + DeleteNote)]
    public Task<string> DeleteHolidaySchedule(
        [Description("Exact schedule name.")] string name,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => DeleteAsync(TenantObjectType.HolidaySchedule, name, "RemoveSchedule",
            key => _commands.GetRemoveScheduleCommand(key), $"Delete holiday schedule '{name}'.", planId, cancellationToken);

    [McpServerTool(Name = "delete_resource_account", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Deletes a resource account by user principal name. Its association is released first. The deleted user stays restorable in Microsoft Entra for 30 days." + DeleteNote)]
    public Task<string> DeleteResourceAccount(
        [Description("User principal name of the resource account.")] string upn,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => DeleteAsync(TenantObjectType.ResourceAccount, upn, "RemoveResourceAccount",
            key => _commands.GetRemoveResourceAccountCommand(key), $"Delete resource account '{upn}'.", planId, cancellationToken);

    [McpServerTool(Name = "delete_m365_group", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Deletes a Microsoft 365 group. The deleted group stays restorable in Microsoft Entra for 30 days." + DeleteNote)]
    public Task<string> DeleteM365Group(
        [Description("Object ID of the group (from lookup_m365_group_id or list_m365_groups).")] string groupId,
        [Description("Display name of the group.")] string groupName,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        // The group is deleted by its object ID, so the ID is what the guard checks.
        => DeleteAsync(TenantObjectType.M365Group, groupId, "RemoveM365Group",
            key => _commands.GetRemoveM365GroupCommand(key, ProvisioningTools.Require(groupName, "group name")),
            $"Delete Microsoft 365 group '{groupName}' ({groupId}).", planId, cancellationToken,
            async (id, token) =>
            {
                // The plan shows the name, the script deletes by ID: both must be the same group.
                var wanted = ProvisioningTools.Require(groupName, "group name");
                var group = (await _tenant.GetTopologyAsync(refresh: true, token)).Groups
                    .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (group is not null && !string.Equals(group.DisplayName?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The group with ID {id} is named '{group.DisplayName}', not '{wanted}'. Nothing was deleted. Check the ID with list_m365_groups.");
                }
            });

    [McpServerTool(Name = "release_phone_number", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Removes an unassigned Direct Routing phone number from the tenant's number inventory. A number from an operator or from Microsoft is never released. A Direct Routing number that assign_phone_number added stays in the inventory after its resource account is deleted; this removes it. An assigned number is never released." + DeleteNote)]
    public Task<string> ReleasePhoneNumber(
        [Description("Phone number in E.164 format, for example +41441234567.")] string phoneNumber,
        [Description(PlanIdDescription)] string? planId = null,
        CancellationToken cancellationToken = default)
        => DeleteAsync(TenantObjectType.PhoneNumber, (phoneNumber ?? string.Empty).Trim(), "ReleasePhoneNumber",
            PhoneNumberScripts.GetReleaseUnassignedPhoneNumberCommand, $"Release unassigned phone number {phoneNumber?.Trim()}.", planId, cancellationToken,
            async (number, token) =>
            {
                if (!PhoneNumberScripts.IsE164(number))
                {
                    throw new InvalidOperationException("The phone number must be in E.164 format, for example +41441234567.");
                }

                await _guard.RequireUnassignedPhoneNumberAsync(number, token);
            });

    private Task<string> DeleteAsync(
        TenantObjectType type,
        string key,
        string operation,
        Func<string, string> buildScript,
        string summary,
        string? planId,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? checkFirst = null)
        => McpJson.RunAsync(async () =>
        {
            var target = ProvisioningTools.Require(key, "name");
            if (checkFirst is not null)
            {
                await checkFirst(target, cancellationToken);
            }

            await _guard.RequireDeletableAsync(type, target, cancellationToken);

            return await _changes.PlanOrApplyAsync(
                new ChangeRequest(operation, target, buildScript(target), summary)
                {
                    OnSuccess = _ => _guard.Forget(type, target),
                    ChangeWasMadeDespiteError = async (_, token) => !await _guard.ExistsAsync(type, target, token)
                },
                planId,
                cancellationToken);
        });
}
