## Task 6 report — Cluster B2

Date: 2026-08-22

### Scope in this cluster

- Localized the remaining fixed runtime sinks in:
  - `WelcomeViewModel`
  - `HistoryViewModel`
  - `VariablesViewModel`
  - `HolidaysViewModel`
  - `MainWindowViewModel`
  - `ViewModelBase`
- Added typed `UiTextKey` entries and English/German catalog parity for:
  - fixed page-load and action log wrappers
  - update/settings/log-viewer shell messages in the main window
  - variables file load/save, holiday-manager, and audio-import messages
  - holiday create/verify/attach log wrappers
  - base audit-write and exception wrapper logs
- Preserved inserted technical/runtime values exactly:
  - file paths, exception text, holiday names, counts, audio file IDs, raw import results, versions, log levels, and native output/details
- Kept frozen infrastructure/auth/script builders, Domain/API contracts, XAML, screenshot code, and unrelated production files untouched.

### RED

Added the new focused runtime-localization coverage first, then ran:

`dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~RuntimeLocalizationTests`

Initial RED evidence:

- build failed because the new B2 runtime tests referenced missing typed keys:
  - `UiTextKey.VariablesSaveHolidaySeriesLog`
  - `UiTextKey.MainSettingsPanelStateLog`
- that confirmed the remaining B2 wrappers were still hard-coded and not yet represented in the typed runtime catalog surface.

### GREEN

Focused runtime localization tests:

`dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~RuntimeLocalizationTests`

Result: Passed 14/14

Focused affected B2 ViewModel/runtime slice:

`dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeLocalizationTests|FullyQualifiedName~WelcomeViewModelTests|FullyQualifiedName~HistoryViewModelTests|FullyQualifiedName~VariablesViewModelTests|FullyQualifiedName~HolidaysViewModelTests|FullyQualifiedName~MainWindowViewModelTests"`

Result: Passed 111/111

Full solution:

`dotnet test PhoneDesk.slnx --no-restore`

Result: Passed 591/591

### Runtime sink coverage in this cluster

- `WelcomeViewModel`
  - page-load log
  - open-documentation log
  - open-documentation failure log wrapper
- `HistoryViewModel`
  - page-load log
  - audit-read failure log wrapper
  - open-audit-folder failure log wrapper
- `VariablesViewModel`
  - page-load log
  - configuration save/load success and failure wrappers
  - load-picker title and file-type labels
  - holiday-time update log
  - add-holiday / predefined-holidays / Aargau-reference open and failure logs
  - predefined-holiday incomplete-region warning and per-holiday add logs
  - edit/save/delete holiday logs and delete-all/save-series logs
  - call-queue / auto-attendant configuration save logs
  - audio-picker title and file-type labels
  - audio file size/type/import/parse/import-failure wrappers and success messages
  - operator-visible audio contexts without internal `AA` / `CQ` wording
- `HolidaysViewModel`
  - page-load log
  - create holiday-series start/success/error logs
  - verify auto-attendant start/success/error logs
  - attach-holiday start/success/error logs
  - exception wrapper logs through localized base helper
- `MainWindowViewModel`
  - application-start log
  - update available / installer missing / installer start / download cancelled / installation failed logs
  - open-release-page failure log
  - skip-preview / skip-delete / auto-refresh / minimum-log-level logs
  - open-audit-folder failure log
  - theme-changed log
  - settings-panel and log-viewer open/close logs
  - log-cleared log
  - live language refresh for the next emitted shell/runtime log message
- `ViewModelBase`
  - localized audit-log write failure wrapper
  - localized exception wrapper helper used by B2 callers

### Runtime localization tests added in this cluster

- `SwitchingLanguage_LocalizesVariablesHolidaySeriesSaveLog_AndPreservesCount`
- `SwitchingLanguage_LocalizesMainWindowSettingsToggleLog`

### Files changed in this cluster

- `.superpowers/sdd/2026-08-21-i18n-german-plan/task-6-report.md`
- `PhoneDesk.Tests/MainWindowViewModelTests.cs`
- `PhoneDesk.Tests/RuntimeLocalizationTests.cs`
- `src/PhoneDesk.Presentation/Localization/UiTextKey.cs`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json`
- `src/PhoneDesk.Presentation/ViewModels/HistoryViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/HolidaysViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/MainWindowViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/VariablesViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/ViewModelBase.cs`
- `src/PhoneDesk.Presentation/ViewModels/WelcomeViewModel.cs`

## Task 6 follow-up — Wizard runtime surface and screenshot harness

Date: 2026-08-22

### Scope

- Moved Wizard step metadata, step number, actions, review labels, readiness and validation guidance, placeholders, fixed results, and script-preview comments to typed `UiTextKey` entries.
- Added live language refresh for Wizard step metadata, computed summaries, and localized fixed step results. Native PowerShell output, commands, identifiers, tenant values, and exception details remain unchanged.
- Replaced copied English/German strings in the Task 5 and failed-Wizard screenshot scenarios with production `ITranslationService.Get` calls and named placeholders.
- Added Wizard language-switch coverage to `WizardViewModelTests` and `RuntimeLocalizationTests`.

### Catalog checks

- `Strings.en.json`: 963 keys
- `Strings.de.json`: 963 keys
- Missing English keys: 0
- Missing German keys: 0

### Verification

- `dotnet build src/PhoneDesk.Presentation/PhoneDesk.Presentation.csproj --no-restore`: passed, 0 warnings, 0 errors.
- Focused test execution was attempted, but the shared tree contains incomplete unrelated Task 7 guard scaffolding. The root application glob currently compiles that untracked scaffold and fails with duplicate generated-attribute errors; the guard project also has no valid build entry point. No guard files were changed or removed.
- Screenshot generation was not executable for the same shared-tree build blocker. Screenshot harness code was reviewed to use typed production translation paths; fresh PNG inspection remains deferred until Task 7 scaffolding is buildable.

### Deferred boundary

The full solution test/build matrix remains blocked by the pre-existing Task 7 guard scaffold. This follow-up does not modify Infrastructure, authentication, script-builder behavior, or any guard scaffold.
