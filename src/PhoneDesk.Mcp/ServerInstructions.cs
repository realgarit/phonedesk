namespace PhoneDesk.Mcp;

internal static class ServerInstructions
{
    /// <summary>Sent to the AI client at connect time: how to use the tools in the right order and safely.</summary>
    public const string Text = """
        PhoneDesk manages Microsoft Teams Phone for one tenant: auto attendants, call queues, resource accounts, agent groups, holiday schedules and their licenses and phone numbers.

        Order of work:
        1. session_status. If not connected: check_modules, then connect_teams, then connect_graph. Each sign-in opens a browser window on the user's computer; tell the user before you call it. A connect call returns after about 45 seconds; when it reports waitingForSignIn, ask the user to finish in the browser and call it again. Both sign-ins must use the same organization.
        2. To look at a tenant: tenant_overview, the list_* tools, tenant_details, run_health_check. These only read.
        3. To build a phone system: fill the configuration (configuration_get, configuration_update), then run the Guided Setup steps 1 to 8 in order (guided_setup_status, guided_setup_run_step). Single operations are available as separate tools.

        Changes:
        - Every tool that changes the tenant works in two calls. The first call (no planId) returns the exact script and a planId and changes nothing. Show the user what will happen and get their agreement. The second call, with the same arguments and the planId, runs it.
        - Before the first change of a session the server writes a full backup snapshot of the tenant's phone system to this computer. Report the backup file to the user. The snapshot is a record for comparison (compare_with_backup) and manual restore; it is not an automatic restore.
        - Delete tools accept only objects this server created (created_objects). Do not look for another way to delete an object the server refuses.
        - Never invent names, phone numbers, domains or license IDs. Ask the user.

        The configuration is local to this computer and is kept between sessions. Sign-ins are not kept: each new session signs in again.
        """;
}
