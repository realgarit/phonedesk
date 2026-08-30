# Configuration portability and topology drift design

## Scope

Issue #71 adds two related, offline-first workflows:

1. Export and import the app configuration as an explicit, schema-versioned JSON document.
2. Export the last dashboard topology snapshot and compare a saved snapshot with the current in-memory topology.

Neither workflow executes PowerShell, changes tenant state, or modifies the frozen script builders and authentication flow.

## Configuration document

The root document contains a numeric `schemaVersion`, a fixed `kind` discriminator, and a `configuration` payload. The payload is split into stable sections:

- general/customer variables;
- auto-attendant template;
- call-queue template;
- business-hours template;
- holiday-series entries.

Only explicitly mapped fields are exported. Computed display names are regenerated from their source variables, and runtime state, tokens, credentials, logs, and preferences cannot enter the document. The current schema version is `1`; unsupported versions, wrong document kinds, malformed JSON, unknown fields, missing required sections, invalid time ranges, and invalid holiday ranges are rejected before a preview is shown.

Serialization is deterministic: declaration order fixes property order, weekly schedules use Monday-to-Sunday order, holiday entries use chronological order, JSON uses invariant ISO values and LF newlines, and the export contains no generation timestamp.

## Import workflow

Selecting a file parses and validates it without changing shared state. The app compares the incoming document with an allow-listed projection of the active variables and displays a bounded preview of every value that will change. Cancel leaves the current object untouched. Apply creates a fresh `PhoneManagerVariables` instance, swaps it into shared state only after explicit confirmation, and audit-logs the import without copying document contents into the log.

The existing Save/Load buttons remain the user entry points, but their behavior becomes Export/Import under the schema contract. This preserves discoverability while removing direct serialization of the Presentation model.

## Topology snapshot and drift

The topology document has its own fixed kind and schema version. It captures the cached auto attendants, call queues, resource accounts, groups, and resource-account phone assignments. Objects and nested ID lists are stable-sorted. `retrievedAtUtc` comes from the cached topology and is metadata, not a comparison field.

The Documentation page gains Export snapshot and Compare snapshot actions. Both require a topology previously loaded on the Dashboard. Export writes only cached data. Compare parses a saved topology file and calculates:

- added objects;
- removed objects;
- property-level changes for objects with the same stable identity.

Each drift row contains change kind, object type, object identity/name, property, saved value, and live value. Results are shown in a scrollable panel and never trigger a tenant round-trip.

## Architecture

- Application owns portable document models, drift/change models, and serialization/diff ports.
- Infrastructure implements strict JSON serialization, validation, deterministic ordering, and topology diffing.
- Presentation owns the explicit mapper between `PhoneManagerVariables` and the portable Application model plus Avalonia file-picker plumbing.
- ViewModels consume ports and expose preview/result state. They do not serialize JSON or open files directly.

This maintains the existing dependency rule and keeps ObservableObject types out of inner layers.

## Verification

- Unit tests cover strict schema rejection, deterministic output, secret exclusion, full variable round-trip, cancel/apply semantics, stable topology output, and added/removed/property-level drift.
- Existing unit, integration, localization, dependency-rule, and release tests must remain green.
- Headless screenshots cover the configuration import preview and populated topology drift states in English and German; generated PNGs are visually inspected before the PR.
- The complete PR matrix and fail-closed automated review must pass before merge.
