# Tenant health checks design

## Scope

Issue #78 adds a read-only Health Check page backed by the topology already loaded on the Dashboard and one dedicated enrichment query. The query adds only the data absent from the Dashboard model: Teams Phone Resource Account license assignment, resource-account usage location, auto-attendant after-hours handling, and unassigned voice-application phone numbers. It never invokes a `Set-*`, `New-*`, `Remove-*`, or `Update-*` command and does not alter existing ScriptBuilder output or authentication.

## Rule catalog

Application owns a declarative rule catalog. Each entry contains its stable ID, default state, bilingual operator text, severity, evaluator, and optional destination page. The evaluator service only iterates this catalog, so adding a rule does not require ViewModel or service control-flow changes.

The initial catalog covers:

- resource accounts not associated with an auto attendant or call queue;
- missing Teams Phone Resource Account licenses and licensed-but-unassociated accounts;
- call queues with no direct agents or distribution lists;
- auto attendants without after-hours handling;
- unassigned numbers with `VoiceApplicationAssignment` capability;
- resource accounts without a usage location.

Microsoft's current guidance requires a Teams Phone Resource Account license for every resource account, whether or not it has a phone number. Microsoft also documents `Get-CsPhoneNumberAssignment -PstnAssignmentStatus Unassigned -CapabilitiesContain VoiceApplicationAssignment` as the query for available service numbers.

## Persistence and suppression

Rule toggles and suppressed finding IDs are stored locally in an atomic JSON file keyed by tenant ID. Findings use deterministic IDs derived from rule, object, and variant. The tenant ID must be known before a check runs; settings never leak across tenants. Suppressing a finding hides it from the active view and documentation appendix until restored.

## UI and documentation

Health Check is an Overview navigation page. It requires a loaded Dashboard topology, runs the enrichment query with the standard read-only retry/audit pipeline, and displays severity, object, explanation, recommended remediation, suppression, and an in-app destination when available. The most recent active findings are cached per session and appended to the Tenant Report for the same tenant.

## Verification

- pure catalog tests cover every initial rule, severity, stable identity, toggles, and suppression filtering;
- parser and ScriptBuilder tests prove strict marker handling and read-only command text;
- persistence tests prove per-tenant isolation and atomic round-trip;
- ViewModel tests prove the loaded-topology prerequisite, zero-mutation execution, deep links, and documentation export;
- English/German empty, populated, rule-toggle, and suppressed states are rendered and visually inspected;
- the complete unit, integration, Pester, localization, packaging, and fail-closed review gates pass before merge.
