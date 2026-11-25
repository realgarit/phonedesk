# PhoneDesk MCP server

PhoneDesk has two heads over the same code:

- **PhoneDesk Desktop** is the app with a window, for people.
- **PhoneDesk MCP** (`phonedesk-mcp`) is a local server for AI clients. It speaks the
  [Model Context Protocol](https://modelcontextprotocol.io/) over standard input and output.

Both run the same scripts, the same validation and the same audit log. The server is installed
with the app; there is nothing extra to download.

## What an AI client can do

| Area | Tools |
| --- | --- |
| Session | `session_status`, `check_modules`, `connect_teams`, `connect_graph`, `disconnect` |
| Configuration (local) | `configuration_get`, `configuration_options`, `configuration_update`, `configuration_replace`, `configuration_reset`, `configuration_export`, `configuration_import`, `swiss_holidays`, `swiss_holidays_apply` |
| Read the tenant | `tenant_overview`, `list_auto_attendants`, `list_call_queues`, `list_resource_accounts`, `list_m365_groups`, `tenant_details`, `audit_history` |
| Health check | `run_health_check`, `health_rules`, `health_rule_set`, `health_finding_set_suppressed` |
| Backup and comparison | `backup_tenant`, `list_backups`, `compare_with_backup`, `created_objects` |
| Guided Setup | `guided_setup_status`, `guided_setup_preview`, `guided_setup_run_step`, `guided_setup_skip_step`, `guided_setup_reset` |
| Single operations | `create_m365_group`, `lookup_m365_group_id`, `create_resource_account`, `set_resource_account_usage_location`, `assign_resource_account_license`, `assign_phone_number`, `create_call_queue`, `create_auto_attendant`, `associate_resource_account`, `create_holiday_schedule`, `verify_auto_attendant`, `attach_holiday_schedule`, `import_audio_file` |
| Bulk | `bulk_setup_template`, `bulk_setup` |
| Delete | `delete_call_queue`, `delete_auto_attendant`, `delete_holiday_schedule`, `delete_resource_account`, `delete_m365_group`, `release_phone_number` |

`release_phone_number` removes an unassigned Direct Routing number from the tenant's number inventory. Teams
keeps a Direct Routing number there after the resource account it was assigned to is deleted.
The server records a number that `assign_phone_number`, Guided Setup step 6 or `bulk_setup` added
to the inventory, and releases only such a number. An assigned number, and a number from an operator or from Microsoft, is never released.

`bulk_setup` can fail on its first run, because a new resource account is not visible to Microsoft
Graph for some seconds, and complete on a second run. The server records every object a run
created from that object's own line in the output, also when the run failed later, so
`created_objects` lists the objects of all runs.

`swiss_holidays` lists the public holidays of a Swiss canton and year, and `swiss_holidays_apply`
writes them to the holiday entries of the configuration, like the holiday picker of the desktop app.
In Aargau, Fribourg and Solothurn the holidays depend on the region. Without a region both tools
return the regions of the canton and the localities each one covers, and write nothing. An unknown
region is an error; the server never falls back to a shorter list. The years are 2025 to 2030, and
`includeAdditionalRestDays: false` leaves out the additional cantonal rest days.

### Checks the desktop app does with its controls

The desktop app keeps many values right through its controls: a dropdown offers only valid
texts, a number box has a minimum, a delete button works on the selected row. An AI client sends
free text, so the server checks these values itself:

- **Fixed texts.** Greeting types, music on hold and the actions of the call queue and the auto
  attendant take one of a fixed set of texts. `configuration_options` lists them. A change with
  another text is refused and nothing is saved. Without this check the script would use another
  behaviour (hang up, no greeting) and report success.
- **Complete choices.** An action that transfers the call needs its target, and a greeting from
  an audio file or from text needs the file ID or the text. While one is missing the
  configuration is not valid, and `create_call_queue`, `create_auto_attendant` and the Guided
  Setup steps do not run.
- **Limits.** A call queue timeout under 30 seconds is refused, and a holiday entry has an end
  date and an end time, or neither.
- **Known values.** A language, time zone, usage location or phone number type outside the known
  lists is saved, and the result carries a warning.
- **Agent group of the call queue.** The call queue script reuses the group ID an earlier step
  left in the PowerShell session. The server puts a check in front of the unchanged script, so
  the ID is used only when it belongs to the group the configuration names.
- **Resource accounts only.** Usage location, license and phone number go to a resource account:
  the one of the configuration, one the server created, or one the tenant lists.
- **Unknown filter values are errors.** A misspelled audit outcome, health finding ID or region
  gives an error with the valid values, never an empty result.

`tenant_details` reads the snapshot again after a change the server made, and every row list
gives `retrievedAtUtc`. Long lists are read in parts with `offset` and `nextOffset`.
`list_backups` shows every snapshot file of the tenant, so `compare_with_backup` can compare with
an older state than the newest backup.

The desktop Tenant Report is not a separate tool. `tenant_details` returns the same data in
structured form, and the AI client writes the report in the language and layout you ask for.

## Safety rules the server enforces

The server enforces these rules itself. It does not rely on the AI client to follow them.

1. **Plan first.** Every tool that changes the tenant must be called twice. The first call
   returns the exact script and a plan ID, and changes nothing. The second call, with the plan
   ID, runs it. A plan works once, expires after 15 minutes, and is refused when the arguments or
   the configuration changed in between.
2. **Backup first.** Before the first change of a session the server reads the complete phone
   system of the tenant and writes it to a snapshot file. If the backup fails, the change does
   not run. The snapshot is a record for comparison (`compare_with_backup`) and manual restore.
   It is not an automatic restore.
3. **Delete only what the server created.** A delete tool accepts an object only when the server
   created it. The server counts an object as its own only when the backup taken before the change
   proves it did not exist. The server stores the immutable object ID of each object it created
   as soon as the tenant shows the object (a new resource account is not visible for some
   seconds, so `created_objects` can list it without an ID at first) and compares it before a delete, so another object that later gets the same name is not
   deleted. A delete by name also runs only when the name is unique in the tenant.
4. **Same audit log.** Every read and change goes to the audit log the desktop app shows on its
   History page.
5. **Sign-in is yours.** The server opens the same browser sign-in as the desktop app and keeps
   no sign-in between sessions. Use an account with the roles the desktop app needs.

Two environment variables change these rules. Set them only when you understand the effect.

| Variable | Effect |
| --- | --- |
| `PHONEDESK_MCP_SKIP_BACKUP=1` | Changes run without the automatic backup. The server then cannot prove that an object is new, so it records no created objects and the delete tools refuse everything. |
| `PHONEDESK_MCP_ALLOW_DELETE_EXISTING=1` | Delete tools also accept objects the server did not create. The plan step and the unique-name check still apply. |
| `PHONEDESK_MCP_DATA_DIR=<folder>` | Stores the server's files in another folder. |

## Where the server is

| Install | Command |
| --- | --- |
| Windows installer | `%LOCALAPPDATA%\Programs\PhoneDesk\phonedesk-mcp.exe` |
| Microsoft Store | `phonedesk-mcp.exe` (an app execution alias; no path needed) |
| macOS | `/Applications/PhoneDesk.app/Contents/MacOS/phonedesk-mcp` |
| Linux | `phonedesk-mcp` in the folder where you unpacked the zip |

## Quick start

1. Install PhoneDesk 3.35 or later. The server is in the app's folder (see the table above).
2. Add the server to your AI client (see [Connect an AI client](#connect-an-ai-client)), or
   let the client do it with the [setup prompt](#setup-prompt-for-an-ai-client).
3. Start a new session in the AI client. A session that is already open does not load a
   server that was added later.
4. Ask in plain words (see [First session](#first-session)).

## Setup prompt for an AI client

Use this prompt in an AI client that can run commands on your computer, for example Claude
Code, Codex, or GitHub Copilot in agent mode. Copy the whole block. The prompt tells the client
to ask you before it downloads or installs anything.

```text
Set up the PhoneDesk MCP server on this computer and connect yourself to it.

1. Look for an installed server first:
   - Windows installer: %LOCALAPPDATA%\Programs\PhoneDesk\phonedesk-mcp.exe
     (or the folder that was chosen for an all-users install)
   - Microsoft Store: the command phonedesk-mcp.exe
   - macOS: /Applications/PhoneDesk.app/Contents/MacOS/phonedesk-mcp
   - Linux: phonedesk-mcp in the folder where PhoneDesk was unpacked
2. If there is no server, or the installed PhoneDesk is older than 3.35, tell me, and ask me
   before you download anything. Then get the latest release from
   https://github.com/realgarit/phonedesk/releases/latest :
   - Windows: phonedesk-win-x64-setup.exe (start it and let me click through the installer)
   - macOS: brew tap realgarit/tap, brew trust realgarit/tap, then brew install --cask phonedesk
   - Linux: phonedesk-linux-x64.zip, unpacked completely into a folder of its own;
     make phonedesk-mcp executable
   Compare the SHA-256 of a downloaded file with the SHA256SUMS file of the same release
   before you use it. Stop if they differ.
3. Register the server in your own MCP configuration under the name "phonedesk", as a local
   stdio server whose command is the full path from step 1 or 2, with no arguments.
   Do not set PHONEDESK_MCP_SKIP_BACKUP or PHONEDESK_MCP_ALLOW_DELETE_EXISTING.
4. Tell me the path you registered, and tell me to start a new session: a running session
   does not load a server that was added later.
5. In the new session, call session_status and check_modules and report the result. Do not
   call connect_teams or connect_graph, and do not change anything in a tenant, until I ask.
```

After the new session has started, continue with [First session](#first-session).

## Connect an AI client

Replace the command with the one for your install. On Windows, write the full path with your
user name, for example `C:\Users\anna\AppData\Local\Programs\PhoneDesk\phonedesk-mcp.exe`.

**Visual Studio Code (GitHub Copilot, agent mode).** Create `.vscode/mcp.json` in a folder, or
run *MCP: Open User Configuration* for all folders:

```json
{
  "servers": {
    "phonedesk": {
      "type": "stdio",
      "command": "C:\\Users\\anna\\AppData\\Local\\Programs\\PhoneDesk\\phonedesk-mcp.exe"
    }
  }
}
```

With a company Copilot license, an administrator must allow MCP servers in the organization's
Copilot policies.

**Claude Desktop.** Open *Settings > Developer > Edit Config* and add:

```json
{
  "mcpServers": {
    "phonedesk": {
      "command": "C:\\Users\\anna\\AppData\\Local\\Programs\\PhoneDesk\\phonedesk-mcp.exe"
    }
  }
}
```

**Claude Code.**

```powershell
claude mcp add phonedesk -- "C:\Users\anna\AppData\Local\Programs\PhoneDesk\phonedesk-mcp.exe"
```

**Codex.** Add to `~/.codex/config.toml`:

```toml
[mcp_servers.phonedesk]
command = 'C:\Users\anna\AppData\Local\Programs\PhoneDesk\phonedesk-mcp.exe'
```

Microsoft 365 Copilot and Copilot Studio agents accept only remote servers over HTTPS. A local
server such as this one cannot be connected to them.

## First session

Ask the AI client in plain words, for example:

- "Connect to my tenant and tell me what is wrong with the phone system."
- "Show the call flow of the auto attendant for the main number."
- "Set up a call queue and an auto attendant for the reception of Contoso. Show me the plan first."

The client then calls `session_status`, `check_modules`, `connect_teams` and `connect_graph`.
Two browser windows open, one after the other. Sign in to both with the same organization.
When the browser still has a session for that account, a sign-in can complete without a
visible page.

A step right after a new resource account was created can fail once or twice, because the
account is not visible to all Microsoft services for some minutes. The result then carries a
`retryHint`; the client waits and applies a new plan.

## Files

The server keeps its files in `%LOCALAPPDATA%\PhoneDesk\mcp` on Windows
(`~/.local/share/PhoneDesk/mcp` on Linux, `~/Library/Application Support/PhoneDesk/mcp` on macOS).
The Microsoft Store install redirects this folder into the package's own data folder.

| File or folder | Content |
| --- | --- |
| `configuration.json` | The configuration, kept between sessions. Same format as the desktop export. |
| `backups\<tenant ID>\` | Tenant snapshots, one file for each backup. |
| `exports\` | Files written by `configuration_export`. |
| `created-objects.json` | The objects the server created, for each tenant. |

Backups contain names, phone numbers and user principal names from the tenant. Treat the folder
like other administrative exports.

## For developers

- Project: `src/PhoneDesk.Mcp`. It references Domain, Application and Infrastructure, never
  Presentation or a UI framework.
- Shared operation logic lives in `src/PhoneDesk.Application/Operations`. The desktop ViewModels
  and the server both call it. Add new operations there, not in a ViewModel or a tool class.
- Standard output carries the protocol. Nothing in the server may write to it; logs go to
  standard error.
- The server is published into the app's folder by `scripts/publish-mcp-server.sh`, which fails
  when a file both publishes contain differs. Keep `Microsoft.Extensions.Hosting` on 10.0.x and
  `Microsoft.Extensions.DependencyInjection` on the app's version.
- Tests: `PhoneDesk.IntegrationTests/Mcp` runs every tool group against the real service graph
  with scripted PowerShell output. `scripts/smoke-test-mcp-server.py` starts a published server.
- Run a local build: `dotnet build src/PhoneDesk.Mcp`, then point a client at
  `src/PhoneDesk.Mcp/bin/Debug/net10.0/phonedesk-mcp.exe`. Run `scripts/download-modules.ps1`
  once so the bundled PowerShell modules exist.
