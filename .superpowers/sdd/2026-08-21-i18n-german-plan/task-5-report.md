# Task 5 report — provisioning page localization

Date: 2026-08-21
Branch: `codex/german-localization`
Commit target: `feat: localize provisioning pages`

## Scope completed

Localized every active visible literal in the five Task 5 provisioning views with typed live `TranslateExtension` bindings:

- `M365GroupsView.axaml`
- `CallQueuesView.axaml`
- `AutoAttendantsView.axaml`
- `HolidaysView.axaml`
- `BulkOperationsView.axaml`

Also updated:

- `UiTextKey.cs`
- `Strings.en.json`
- `Strings.de.json`
- `LocalizationCoverageTests.cs`
- `ScreenshotGenerator.cs` (minimal test-only render support for required Task 5 screenshots)

No frozen infrastructure/auth/script-builder files were modified. No production files outside the allowed Task 5 scope were modified.

## Round 1 review correction — 2026-08-22

The review fix is limited to the test-only screenshot harness and this report:

- `PhoneDesk.Tests/ScreenshotGenerator.cs` now closes `IsSettingsOpen` before every non-settings scenario. The two settings scenarios retain the drawer-open behavior needed to capture the settings states.
- Task 5 scenario language is derived from the `-de` suffix. German scenarios now inject the reviewed semantic test-state text for Call Queues, Auto Attendants, Holidays, and Bulk Operations; English scenarios retain their original English values. Tenant/API/sample values are unchanged.
- The fix does not alter any production localization file, view, enum, frozen file, or runtime message.

## Test-first evidence

### RED

Extended `LocalizationCoverageTests` first to include the five Task 5 views, then ran:

```powershell
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~LocalizationCoverageTests
```

Result: failed as expected with raw visible literals across all five Task 5 views.

After the first localization pass, reran the same test and found one missed visible literal:

- `M365GroupsView.axaml` — `Header="ID"`

Replaced that with the shared localized `CommonIdColumn` key.

### GREEN

Final rerun:

```powershell
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~LocalizationCoverageTests
```

Result: passed, `Passed: 5, Failed: 0`.

## Implementation notes

### Localization approach

- Used typed live `TranslateExtension` bindings throughout the five views.
- Localized nested dialogs, tab labels, buttons, filters, grid headers, watermarks, accessibility names, empty states, and guidance text.
- Preserved technical/product/runtime values, tenant/API data, bindings, commands, converters, tags, and CSV/runtime contracts.
- Left runtime ViewModel/dialog/error/log message work for Task 6, per brief.

### Non-literal-safe binding fixes

To avoid leaving raw scoped literals behind while preserving behavior:

- `M365GroupsView.axaml`
  - Replaced `StringFormat='Group ID: {0}'` with localized prefix text plus the bound value in sibling text elements.
- `CallQueuesView.axaml`
  - Replaced `TargetNullValue=''` with `{x:Static sys:String.Empty}`.
- `AutoAttendantsView.axaml`
  - Replaced `TargetNullValue=''` with `{x:Static sys:String.Empty}`.
- `HolidaysView.axaml`
  - Replaced `TargetNullValue=''` with `{x:Static sys:String.Empty}`.
- `BulkOperationsView.axaml`
  - Replaced raw `Row {0}` / `Preflight — {0}` formats with localized prefix runs plus bound values.

### Terminology applied

Applied the reviewed English/German terminology from the Task 5 brief, including:

- M365 group / `M365-Gruppe`
- Resource account / `Ressourcenkonto`
- Call queue / `Anrufwarteschleife`
- Auto attendant / `automatische Telefonzentrale`
- Usage location / `Nutzungsstandort`
- Call flow / `Anruffluss`
- Holiday schedule / `Feiertagszeitplan`
- Link holiday to auto attendant / `Feiertag mit der Telefonzentrale verknüpfen`
- Create forwarding target / `Weiterleitungsziel erstellen`
- Link schedule to call flow / `Zeitplan mit Anruffluss verknüpfen`

## Catalog changes

Added the required Task 5 keys to:

- `src/PhoneDesk.Presentation/Localization/UiTextKey.cs`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json`

Catalog parity was maintained between English and German for the newly introduced Task 5 keys.

## Screenshot rendering and visual inspection

Generated the required Task 5 screenshots with the existing headless harness plus a minimal Task 5 scenario extension in `ScreenshotGenerator.cs`.

Command used:

```powershell
$env:GENERATE_SCREENSHOTS='1'
$env:SCREENSHOT_FRAMELESS='1'
$env:SCREENSHOT_OUT='C:/Users/realgar/.codex/visualizations/2026/08/21/01a025ff-a85b-7640-8a96-77b1e07c8882/task5'
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~ScreenshotGenerator
```

Result: passed, `Passed: 1, Failed: 0`.

Rendered files:

- `C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task5\task5-m365-groups-empty-en.png`
- `C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task5\task5-m365-groups-empty-de.png`
- `C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task5\task5-call-queues-populated-en.png`
- `C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task5\task5-call-queues-populated-de.png`
- `C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task5\task5-auto-attendants-filter-en.png`
- `C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task5\task5-auto-attendants-filter-de.png`
- `C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task5\task5-holidays-dialog-en.png`
- `C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task5\task5-holidays-dialog-de.png`
- `C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task5\task5-bulk-operations-error-en.png`
- `C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task5\task5-bulk-operations-error-de.png`

Visual inspection result:

- All ten fresh English and German states from the Round 1 rerun were opened and inspected manually.
- The settings drawer is absent from every Task 5 image; only the requested Holidays dialog is open in its two dialog captures.
- No localization-specific clipping, overlap, or truncation was found in the Task 5 target surfaces.
- German status/error text is no longer fixed English: the Call Queues and Auto Attendants banners use the reviewed German resource-account messages, the Holidays banner uses the reviewed German holiday-series message, and the Bulk Operations banner visibly reads `Analysefehler: Erforderliche CSV-Spalten fehlen.`. Its execution-log test value is `FEHLER: Erforderliche CSV-Spalten fehlen.` and is assigned in the harness while the CSV-Daten tab remains the captured surface.
- Technical values, identifiers, and sample tenant data remained unchanged.

## Test evidence

### Targeted Task 5 view-model suites

```powershell
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter "FullyQualifiedName~M365GroupsViewModelTests|FullyQualifiedName~CallQueuesViewModelTests|FullyQualifiedName~AutoAttendantsViewModelTests|FullyQualifiedName~HolidaysViewModelTests|FullyQualifiedName~BulkOperationsViewModelTests"
```

Result: passed, `Passed: 134, Failed: 0`.

### Full solution suite

```powershell
dotnet test PhoneDesk.slnx --no-restore
```

Result: passed, `Passed: 577, Failed: 0`.

### Round 1 review verification — 2026-08-22

The post-review harness rerun regenerated all ten Task 5 PNGs and passed `ScreenshotGenerator` with `Passed: 1, Failed: 0`. The fresh full solution rerun above passed `577/577` with zero failures.

## Files changed

- `PhoneDesk.Tests/LocalizationCoverageTests.cs`
- `PhoneDesk.Tests/ScreenshotGenerator.cs`
- `src/PhoneDesk.Presentation/Localization/UiTextKey.cs`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json`
- `src/PhoneDesk.Presentation/Views/M365GroupsView.axaml`
- `src/PhoneDesk.Presentation/Views/CallQueuesView.axaml`
- `src/PhoneDesk.Presentation/Views/AutoAttendantsView.axaml`
- `src/PhoneDesk.Presentation/Views/HolidaysView.axaml`
- `src/PhoneDesk.Presentation/Views/BulkOperationsView.axaml`

## Outcome

Task 5 is complete within the requested scope, and the Round 1 screenshot-review corrections are verified in the test-only harness.

Round 1 fix files:

- `PhoneDesk.Tests/ScreenshotGenerator.cs`
- `.superpowers/sdd/2026-08-21-i18n-german-plan/task-5-report.md`
