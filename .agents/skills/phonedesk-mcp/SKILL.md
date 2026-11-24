---
name: phonedesk-mcp
description: Use when working on the PhoneDesk MCP server (src/PhoneDesk.Mcp) or the operation logic it shares with the desktop app (src/PhoneDesk.Application/Operations) — adding or changing a tool, a shared operation, the safety gate, or the server's packaging.
---

# Working on the PhoneDesk MCP server

PhoneDesk has two heads: the Avalonia desktop app and `phonedesk-mcp`, a stdio MCP server for AI
clients. Read `docs/mcp-server.md` for what the server does and how users connect a client.

## Rules

1. **Shared logic goes below the heads.** Anything both heads need (script choice, success test,
   parsing of script output, sequencing) lives in `src/PhoneDesk.Application/Operations`. A
   ViewModel or a tool class only adapts it: the ViewModel adds dialogs and status text, the tool
   adds JSON. Do not copy logic from a ViewModel into a tool.
2. **The frozen surface stays frozen.** The server calls the same ScriptBuilders and auth
   services as the app. Do not change their output for the server. If a tool needs different
   script text, stop and ask.
3. **Every tenant change goes through `ChangeGate.PlanOrApplyAsync`.** Never call the command
   runner directly from a changing tool. The gate gives the plan step, the backup and the
   single-use plan ID.
4. **Every delete goes through `CreatedObjectGuard.RequireDeletableAsync`,** and every create
   reports to `CreatedObjectGuard.Claim`. The delete scripts match by name and take the first
   match; the guard is what makes that safe.
5. **Nothing writes to standard output.** It carries the protocol. Use `ILoggingService` or
   `ILogger`; both go to standard error.
6. **Tools return JSON text built with `McpJson`.** Expected failures return
   `{ "success": false, "error": "..." }`; they do not throw.
7. **A tool that reads or writes a file** takes the path from an AI client. Read only through
   `McpFiles.ReadJsonFile`; write only inside `McpEnvironment` folders.

## Adding a tool

1. Put the operation in `Application/Operations` if the desktop app has or will have it too.
2. Add the method to a class in `src/PhoneDesk.Mcp/Tools` with `[McpServerTool]` and a
   `[Description]` on the method and on every parameter. Names are `snake_case`.
3. Set the hints: `ReadOnly = true` for reads; `Destructive = true` only for `delete_*` tools.
4. Describe the two-call plan flow in the description of a changing tool (reuse `ChangeNote`).
5. Add a test in `PhoneDesk.IntegrationTests/Mcp/McpToolTests.cs` using `McpTestHost`.
6. Add the tool to the table in `docs/mcp-server.md` and, when it must exist in every release,
   to `EXPECTED_TOOLS` in `scripts/smoke-test-mcp-server.py`.

## Checks before a pull request

```powershell
dotnet build PhoneDesk.slnx -c Release
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj -c Release --no-build
dotnet test PhoneDesk.IntegrationTests/PhoneDesk.IntegrationTests.csproj -c Release --no-build
```

A change in `Application/Operations` also changes the desktop app: run the whole unit test
project, not only the MCP tests.

## Packaging

The server is published into the app's folder (`scripts/publish-mcp-server.sh`) and shares the
runtime, the PowerShell SDK and the bundled modules. The script fails when a shared file
differs, so a package bump in `src/PhoneDesk.Mcp` can break packaging: keep
`Microsoft.Extensions.Hosting` on 10.0.x and `Microsoft.Extensions.DependencyInjection` on the
app's version.

## Testing against a real tenant

The sign-in is a global administrator on a live tenant. Take a backup first (`backup_tenant`),
create only clearly named test objects, and delete only those. Deleted users and groups stay
restorable in Microsoft Entra for 30 days.
