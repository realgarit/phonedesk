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
| Configuration (local) | `configuration_get`, `configuration_options`, `configuration_update`, `configuration_replace`, `configuration_reset`, `configuration_export`, `configuration_import`, `swiss_holidays`, `swiss_holidays_apply`, `public_holidays`, `public_holidays_apply`, `import_holiday_calendar` |
| Read the tenant | `tenant_overview`, `list_auto_attendants`, `list_call_queues`, `list_resource_accounts`, `list_m365_groups`, `tenant_details`, `audit_history` |
| Health check | `run_health_check`, `health_rules`, `health_rule_set`, `health_finding_set_suppressed` |
| Backup and comparison | `backup_tenant`, `list_backups`, `compare_with_backup`, `created_objects` |
| Guided Setup | `guided_setup_status`, `guided_setup_preview`, `guided_setup_run_step`, `guided_setup_skip_step`, `guided_setup_reset` |
| Single operations | `create_m365_group`, `lookup_m365_group_id`, `create_resource_account`, `set_resource_account_usage_location`, `assign_resource_account_license`, `assign_phone_number`, `create_call_queue`, `create_auto_attendant`, `associate_resource_account`, `create_holiday_schedule`, `verify_auto_attendant`, `attach_holiday_schedule`, `import_audio_file` |
| Holiday schedules that exist | `list_holiday_schedules`, `holiday_call_handling`, `copy_holiday_call_handling` |
| Bulk | `bulk_setup_template`, `bulk_setup_preview`, `bulk_setup` |
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

Each holiday carries the label the desktop picker shows. An additional cantonal rest day is not a
statutory holiday under Art. 20a ArG, but the canton treats it as a rest day and most businesses
are closed. The result names these days in `review` and tells the client to confirm them with the
customer. Customary days without a legal basis, such as Sechseläuten or Knabenschiessen in Zurich,
are in neither head; add them as own entries when the customer is closed.

### Holidays of a new year

A holiday schedule holds dates only. What callers get on those dates is a call flow on each auto
attendant that uses the schedule, so one schedule can send the callers of one number to voicemail
and the callers of another number to an external number. The three tools work per auto attendant
and take every object by its ID, never by its name.

- `list_holiday_schedules` reads the schedules only: ID, date ranges, whether all dates have
  passed, and the auto attendants that use each one.
- `holiday_call_handling` shows, for one auto attendant or for all, what callers get on each
  holiday schedule: the greeting (text, or audio file with its ID), the menu action, the transfer
  target, the voicemail options and the priority. It also lists holiday schedules of the auto
  attendant that overlap and the number of call flows against the limit.
- `copy_holiday_call_handling` attaches a schedule to one auto attendant with the call handling
  another schedule has there. For a new year: create the new schedule, then copy last year's call
  handling, once per auto attendant. The new call flow uses the greetings and the menu of the
  source call flow as they are, so a transfer without a greeting, an audio file and the voicemail
  options are all kept.

Before a plan is made the server reads the auto attendant and refuses the plan when Teams would
not save it. The result names the cause and changes nothing:

- **Same start time.** Two holidays of one auto attendant that start at the same time. Teams
  refuses that; there is no switch for it.
- **Overlap.** The new schedule overlaps a holiday that is attached already, for example a single
  holiday inside a closure of several days. The result shows both date ranges and the call
  handling of the other schedule. `acceptOverlaps: true` allows it.
- **Call flow limit.** An auto attendant can have 21 call flows beside its default call flow.
  At the limit the result lists the attached schedules whose dates have all passed.
  `replaceScheduleId` takes one of them off the auto attendant in the same save; the schedule
  itself stays in the tenant. A schedule with a date that has not passed is not accepted.
- **Authorized users that no longer exist.** Teams does not save an auto attendant that lists
  one. The result names the user IDs; `removeMissingAuthorizedUsers: true` takes these entries
  off in the same save.

The copy script holds IDs only and reads the call flow when it runs. The plan is therefore also
bound to what the preview showed: the source call flow, and the names and dates of the
schedules. When one of these changes in Teams between the preview and the apply call, the plan
is refused, nothing is changed, and a new preview is needed.

After the save the server reads the auto attendant again. `verification` says whether the
schedule is attached and whether the call flow is the same as the source, compared value by
value; a greeting text is compared character by character. When Teams reports an error but the
schedule is attached, the change counts as made. A schedule that is attached already is
reported as `alreadyAttached` and nothing is changed, so a call can be repeated.

`attach_holiday_schedule` (a spoken greeting, then the call ends) has the same checks before the
plan and the same read-back. Its `verification.greetingAsSent` is false when Teams holds another
text than the one that was sent. The script builder removes the characters that can end or
expand a double-quoted PowerShell string from a spoken text: the double quote, the dollar sign
and the backtick. Apostrophes, plain and typographic, arrive unchanged (before 3.37.2 an
apostrophe was written twice, so `aujourd'hui` arrived as `aujourd''hui`; correct such a greeting
in Teams, or attach the schedule again).

### Changes to holiday schedules that exist

The desktop app has these on the **Holiday Manager** page; the server has one tool for each. Both
use the same checks, scripts and read-back (`HolidayScheduleOperations`).

| Change | Tool | What the check shows |
|---|---|---|
| Other dates (add, remove, half day, across a year) | `edit_holiday_schedule_dates` | Added, removed and kept ranges; every auto attendant that uses the schedule; overlaps of a new date |
| Another greeting (text or audio file by ID) | `replace_holiday_greeting` | The old greeting with its audio file ID; other schedules on the same call flow |
| One schedule for several auto attendants | `rollout_holiday_call_handling` | Each auto attendant as verified, planned or blocked |
| Retire a schedule (take it off and delete it) | `retire_holiday_schedules` | Every link that comes off; whether its call flow goes too |

Rules that hold for all four:

- The first call (or **Check** in the app) reads the tenant and sends nothing.
- An approval is bound to what the check showed. When the tenant changed before the second call
  (or before **Apply**), nothing runs and the new check is shown.
- After the change the tenant is read again. The result is taken from that read, not from the
  last line of the script: a script can print a success line and a Teams error together.
- A rollout that stopped in the middle is continued by running it again. Auto attendants that
  have the schedule are skipped, so no call flow or link is made twice.
- `retire_holiday_schedules` deletes schedules the server did not create. It needs the IDs, shows
  every link first, refuses a schedule with a date that has not passed (unless
  `allowNotExpired`), and never touches a schedule that is not named.
- An authorized user is removed only when the lookup confirmed that the user is missing or
  disabled. A lookup that failed (no permission, or another error) proves nothing, so that user
  stays (`uncheckedAuthorizedUsers` with its `kind`).
- When an expired schedule is taken off an auto attendant at the call flow limit, its call flow
  goes only when no other link of the auto attendant uses it.

### Holidays of other countries and calendar files

`public_holidays` lists the public holidays of a country and year, and `public_holidays_apply`
adds them to the holiday entries of the configuration. The dates are computed from the legal
rules:

| Country | Regions | Notes |
| --- | --- | --- |
| Germany (DE) | the sixteen states; Bayern, Sachsen and Thüringen have a second region where a holiday applies to a part of the state | region needed |
| Austria (AT) | none | the thirteen statutory holidays |
| France (FR) | métropole, Alsace-Moselle | Alsace-Moselle has Good Friday and 26 December |
| United Kingdom (GB) | England and Wales, Scotland, Northern Ireland | region needed; a holiday on a weekend is listed with its substitute day |
| United States (US) | none | federal holidays; a holiday on a weekend is listed with its observed day |

Days a business closes without a law (24 December, the day after Thanksgiving, a local custom,
a bank holiday proclaimed for one occasion) are not in a list. Ask the user, and pass the dates
the customer is open on as `excludeDates`.

`import_holiday_calendar` reads an `.ics` file for everything else. It lists the events first
(`apply=false`) and adds them with `apply=true`. An all-day event is a whole day or a range of
days, a timed event keeps its times as written, and a repeating event is read with its first
date only. The desktop holiday picker has the same countries, a check box per suggested date,
and **Import a calendar file (.ics)**.

### The preview against the tenant

`guided_setup_preview` and `bulk_setup_preview` return the dry-run plan from the configuration or
the CSV alone. With `checkTenant=true` they also read the tenant, for every entry:

- which agent group, call queue, auto attendant and resource accounts exist already. The setup
  uses what exists, so this is a warning, not a failure;
- whether enough licenses are free, counted over all entries of the plan. An account that has
  the license already needs none;
- whether the phone number is in the tenant, has the configured type, and is free.

A check with the status `Fail` means that step would fail. What could not be read is reported as
`NotChecked` with the reason, never as a pass. The desktop app has the same check as **Check
tenant** in Guided Setup and on the Bulk Operations page. The script only reads.

### A customer's holiday list and the change table

A customer sends a list of closed days. Two steps use it, in the desktop app (Holiday Manager,
**Compare a customer list**) and in the server.

The list has one entry per line: a day or a range, then the customer's wording.

```
25.12.2027 Weihnachten
24.12.2027 12:00-24:00 Heiligabend Nachmittag
27.12. bis 02.01. Betriebsferien
G 01.08. Bundesfeier
```

A leading letter marks the kind of day: G statutory holiday, B business closure, R additional
rest day, H half day. Without a letter, a part of a day is H and several days are B. An entry
without a year takes the year you give and is flagged.

- `compare_holiday_list` compares every entry with the holidays each auto attendant has, whatever
  the schedules are called: match, covered (a longer range of a schedule contains it), sameStart
  (Teams refuses a second holiday with this start), partialOverlap, disabled (attached but
  switched off, so callers do not get it), or missing. It also lists
  ranges only the tenant has, lines it could not read, and whether the list spans several years.
- `holiday_change_table` writes the table for the customer record after the work. It compares a
  holiday backup from before the work with the tenant now: `done` only when the tenant did not
  have the range before and has it now, `no change` when it was there already, `pending` when it
  is still missing. Rows keep the customer's wording; a range that a longer existing schedule
  covers gets the marker W. The table also comes as tab-separated text.
- Take the backup before the work with `backup_holiday_call_handling`. In the desktop app the
  "before" is what the page read first in the session.

### The right tenant, and a sign-in that hangs

A sign-in can report success and still be wrong: the browser kept the session of the previous
customer, so the connection is to another tenant, or Microsoft Teams and Microsoft Graph are
in different tenants. Both heads now read who the connections are and stop on a problem.

- After each sign-in the tenant of Microsoft Teams (ID, name, domains) and the tenant and account
  of Microsoft Graph are read from the tenant.
- `connect_teams` and `connect_graph` take `expectedTenant` (tenant ID, a domain, or the exact
  name) and `expectedAccount`. In the desktop app the Readiness Check page has a field for the
  expected organization.
- With a problem (two tenants, another tenant, another account) the connect call reports a
  failure although the sign-in worked, `session_status` and `tenant_identity` show the problem,
  and the server sends no read and no change until the user signs out and signs in correctly.
  In the desktop app the page is not ready and every operation that checks its prerequisites
  stops.
- `restart_sign_in` with `graph` drops a Microsoft Graph sign-in that hangs (the browser window
  was closed). The next `connect_graph` opens a new window; a sign-in that is finished later in
  the old window connects nothing; the Microsoft Teams connection stays. The desktop app has
  **Start sign-in again** on the Microsoft Graph card while the sign-in is open.
- A Microsoft Teams sign-in cannot be given up: it holds the PowerShell session until it ends.
  `restart_sign_in` with `teams` says what the user can do. This part of the request is open.

### Phone numbers

The desktop app has these on the **Phone Numbers** page; the server has one tool for each. Both
use the same read scripts, checks and change scripts (`PhoneNumbers`).

| Task | Tool |
|---|---|
| See the inventory: number, type, free or assigned, who has it | `list_phone_numbers` |
| Give a number to an employee or a resource account, or move it | `set_phone_number_assignment` |
| Take a number off an account (it stays in the inventory) | `remove_phone_number_assignment` |

- The target is a user principal name or an object ID. The server also takes the ID or exact name
  of an auto attendant and uses its one resource account.
- The check shows the number with its type and owner, the account that gets it with the number it
  has now, and what stops the change: a number that is not in the inventory (only a Direct Routing
  number can be new), a number that cannot be used for this kind of account (`UserAssignment` for
  an employee, `VoiceApplicationAssignment` for a resource account), or a number another account
  has.
- A number another account has is moved only after a confirmation (`reassign=true`, or the
  checkbox in the app). The check then shows both accounts.
- A move takes the number off the old account and gives it to the new one. When the new account
  does not take it, the script gives it back to the old account and says so; when that fails too,
  it says that the number has no account now.
- After a change the number is read again, and the result says who has it.
- Nothing here releases a number from the inventory. `release_phone_number` is the separate,
  destructive tool for an unassigned Direct Routing number.

### Audio files and the application they belong to

Teams keeps an audio file for one application, and only that application finds it:
`OrgAutoAttendant` for a greeting of an auto attendant, `HuntGroup` for a greeting, the music on
hold or a shared voicemail greeting of a call queue. Before 3.39.0 the desktop app and the server
uploaded every file for call queues, so an audio greeting of an auto attendant was not found when
the auto attendant was created.

- `import_audio_file` needs the application (`autoAttendant` or `callQueue`). Its plan shows the
  application, the file, its size and the SHA-256 of its content.
- The same content for the same application is not uploaded twice: the call returns the ID of the
  earlier upload. A file with the same name and other content is a new upload.
- `list_audio_files` lists the files of one application with their IDs, also when two have the
  same name. Use an ID to take a file that exists.
- Teams converts an upload. A file cannot be recognised by downloading and comparing it; the ID
  identifies it. After a change, `replace_holiday_greeting` reads the call flow again and reports
  whether it has the audio file ID that was sent.
- In the desktop app the greeting of an auto attendant is uploaded for auto attendants, and the
  audio file fields take an ID that is typed in.

### A backup of only what a holiday change touches

The full backup reads the whole phone system and stops when one query warns, for example about a
call queue without agents. A holiday change then no longer stops:

- When the full backup fails, the server writes a backup of the auto attendants and schedules the
  change touches (call flows, links, dates) and runs the change. The result names the file, says
  that the full backup failed and why (`fullBackup`), and that the rest of the tenant is not in it.
- When that backup misses an object the change touches, nothing runs.
- Other changes (creating objects, deleting them) still need the full backup.
- `backup_holiday_call_handling` writes such a backup on request and reports what it captured.
- The desktop app writes the same backup before every change of the Holiday Manager page, to
  `backups` in its local data folder, and runs nothing when the file cannot be written.

### Opening hours, caller ID and the agent delay

Three points from a customer's report after a Guided Setup. They apply to the desktop app and to
the server.

- **Opening hours go to the call queue.** `autoAttendantTemplate.defaultAction` has the value
  `TransferToCallQueue` in a new configuration: during opening hours the auto attendant sends the
  calls to the call queue of the same configuration. An empty value means the same. Before 3.38.0
  an empty value made the auto attendant hang up. `Disconnect` is still there for an auto
  attendant that must hang up; `TransferToTarget` takes the UPN of another resource account. When
  the call queue resource account does not exist, the step stops with an error and builds
  nothing.
- **Outbound caller ID.** With `callQueueTemplate.useAutoAttendantNumberAsCallerId` set to `true`,
  Guided Setup step 8 also sets the auto attendant resource account as caller ID of the call
  queue, so the agents can call out with the main number. It is step 8 and not the call queue
  step because the account and its number exist only from step 6 on. The configuration document
  shows the value only when it is `true`; `configuration_options` lists it under `switches`.
- **Agents from the group wait for calls.** Microsoft documents that new users of a group can
  wait up to eight hours for their first call. The call queue step says so; pass it on to the
  user. It is not an error of the setup.

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
