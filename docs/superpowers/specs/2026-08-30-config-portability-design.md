# Configuration portability and topology drift design

## Scope

Issue #71 adds two related workflows:

1. Export and import the app configuration as an explicit, schema-versioned JSON document.
2. Export a complete live tenant snapshot and compare a saved snapshot with newly read tenant state.

Configuration import/export is local. Snapshot actions execute the existing read-only report and dashboard queries, but never change tenant state. The frozen script builders and authentication flow remain byte-identical.

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

The topology document has its own fixed kind and schema version. Schema v2 captures the live Dashboard topology plus a structured superset of every Tenant Report dataset: tenant metadata, detailed auto-attendant call flows/menu options/schedule associations/operators, detailed call queues/agents/distribution lists/overflow/timeout actions, resource-account associations, schedules and ranges, phone-number inventory/assignments, and voice-enabled users/policies. Objects and nested ID lists are stable-sorted. `retrievedAtUtc` records collection time and is metadata, not a comparison field. Schema-v1 snapshots remain readable and compare their original topology fields.

The Documentation page provides Export snapshot and Compare snapshot actions. Each action collects fresh live state through the existing read-only scripts. Export writes that complete snapshot. Compare validates the saved file before querying the tenant, then calculates:

- added objects;
- removed objects;
- property-level changes for objects with the same stable identity.

Each drift row contains change kind, object type, object identity/name, property, saved value, and live value. Results are shown in a scrollable panel. Any failed, warning-only, partial, or marker-incomplete query aborts snapshot generation/comparison instead of producing false removals.

## Architecture

- Application owns portable document models, drift/change models, serialization/diff ports, and the strict parser for frozen report markers.
- Infrastructure implements strict JSON serialization, validation, deterministic ordering, and topology diffing.
- Presentation owns the explicit mapper between `PhoneManagerVariables` and the portable Application model plus Avalonia file-picker plumbing.
- ViewModels consume ports and expose preview/result state. They do not serialize JSON or open files directly.

This maintains the existing dependency rule and keeps ObservableObject types out of inner layers.

## Verification

- Unit tests cover strict schema rejection, deterministic output, secret exclusion, full variable round-trip, cancel/apply semantics, complete live report-marker parsing, schema-v1 compatibility, schema-v2 stable topology output, fail-closed collection, and added/removed/property-level inventory drift.
- Existing unit, integration, localization, dependency-rule, and release tests must remain green.
- Headless screenshots cover the configuration import preview and populated topology drift states in English and German; generated PNGs are visually inspected before the PR.
- The complete PR matrix and fail-closed automated review must pass before merge.
