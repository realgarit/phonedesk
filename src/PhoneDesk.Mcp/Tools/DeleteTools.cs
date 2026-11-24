using System.ComponentModel;
using ModelContextProtocol.Server;
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
    private readonly TenantGateway _tenant;
    private readonly CreatedObjectLedger _ledger;
    private readonly McpEnvironment _environment;

    public DeleteTools(
        IPowerShellCommandService commands,
        ChangeGate changes,
        TenantGateway tenant,
        CreatedObjectLedger ledger,
        McpEnvironment environment)
    {
        _commands = commands;
        _changes = changes;
        _tenant = tenant;
        _ledger = ledger;
        _environment = environment;
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
    [Description("Deletes an auto attendant by name. Its resource account associations are released and the schedules it owns are removed." + DeleteNote)]
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
        => DeleteAsync(TenantObjectType.M365Group, groupName, "RemoveM365Group",
            key => _commands.GetRemoveM365GroupCommand(ProvisioningTools.Require(groupId, "group ID"), key),
            $"Delete Microsoft 365 group '{groupName}' ({groupId}).", planId, cancellationToken);

    private Task<string> DeleteAsync(
        TenantObjectType type,
        string key,
        string operation,
        Func<string, string> buildScript,
        string summary,
        string? planId,
        CancellationToken cancellationToken)
        => McpJson.RunAsync(async () =>
        {
            var target = ProvisioningTools.Require(key, "name");
            _tenant.RequireReady();
            if (!_environment.AllowDeleteExisting && !_ledger.Contains(_tenant.TenantId, type, target))
            {
                throw new InvalidOperationException(
                    $"'{target}' was not created by this server, so it is not deleted. Objects this server created are listed by " +
                    $"created_objects. To allow deleting other objects, the operator must start the server with " +
                    $"{McpEnvironment.AllowDeleteExistingVariable}=1, or delete the object in the desktop app or the Teams admin center.");
            }

            return await _changes.PlanOrApplyAsync(
                new ChangeRequest(operation, target, buildScript(target), summary)
                {
                    OnSuccess = _ => _ledger.Remove(_tenant.TenantId, type, target)
                },
                planId,
                cancellationToken);
        });
}
