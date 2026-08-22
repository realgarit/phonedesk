# Task 3 Report — Main shell, Settings language choice, and catalog semantics

Date: 2026-08-21

## Scope completed

Implemented the Task 3 shell/settings localization slice for PhoneDesk:

- Added a live language selector in the Settings panel with `English` and `Deutsch`.
- Added a `MainWindowViewModel.CurrentLanguage` proxy to `ITranslationService.CurrentLanguage`.
- Localized the active `MainWindow` shell and Settings panel with typed translation keys, including navigation labels, settings sections, update banner actions, accessibility names, tooltips, log viewer chrome, and connection status text.
- Applied the required semantic source-English terms in the shell:
  - `Readiness Check`
  - `Configuration`
  - `Audit Log`
  - `Tenant Report`
  - `Guided Setup`
- Added shell/settings English and German catalog entries.

## Test-first sequence

### RED

Added failing focused tests in:

- `PhoneDesk.Tests/MainWindowViewModelTests.cs`
- `PhoneDesk.Tests/LocalizationServiceTests.cs`

Initial focused run:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~LocalizationServiceTests"
```

Expected RED result was confirmed:

- `MainWindowViewModel` did not expose `CurrentLanguage`
- `UiTextKey` did not contain the required shell/settings keys

### GREEN

Implemented the shell/settings localization changes and reran the focused slice:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~LocalizationServiceTests"
```

Result:

- Passed: 29
- Failed: 0

## Required deviations from the brief write set

Task 3 could not be completed exactly within the listed six files because two required prerequisites were missing in the repository state:

1. `src/PhoneDesk.Presentation/Localization/UiTextKey.cs`
   - The typed shell/settings keys required by the brief did not exist.
   - Without adding them, `TranslateExtension` bindings could not be authored for the shell.

2. Screenshot/runtime live-refresh support required non-listed support changes:
   - `src/PhoneDesk.Presentation/Localization/TranslationCatalog.cs`
     - Added broader key/indexer change notifications so live bindings refresh when the language changes.
   - `PhoneDesk.Tests/ScreenshotGenerator.cs`
     - Initialized `Application.Resources["TranslationCatalog"]` before `MainWindow` construction.
     - Added dedicated English/German shell/settings screenshot states.

These were the smallest in-scope changes needed to satisfy the typed binding requirement and the screenshot verification step.

## Screenshot generation and inspection

Command used:

```text
$env:GENERATE_SCREENSHOTS="1"
$env:SCREENSHOT_FRAMELESS="1"
$env:SCREENSHOT_OUT="C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task3-screens"
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~ScreenshotGenerator
```

Generated Task 3 verification images:

- `shell-settings-en.png`
- `shell-settings-de.png`

Visual inspection findings:

- English shell/settings state rendered correctly.
- German shell/settings state rendered correctly after fixing translation-catalog binding refresh.
- No clipping observed in:
  - `Einstellungen`
  - `Anzeigesprache`
  - `Skriptvorschau überspringen`
  - `Löschbestätigung überspringen`
  - `Nach Vorgängen automatisch aktualisieren`
  - `Speicherort des Prüfprotokolls`
  - `Öffnen`
- No stale English remained in the Task 3 shell/settings surface after the refresh fix.
- Background page content still contains English copy outside Task 3 scope; that belongs to later localization tasks.

## Full verification

Full suite command:

```text
dotnet test PhoneDesk.slnx --no-restore
```

Result:

- Passed: 572
- Failed: 0

## Files changed

Task 3 requested files:

- `src/PhoneDesk.Presentation/MainWindow.axaml`
- `src/PhoneDesk.Presentation/ViewModels/MainWindowViewModel.cs`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json`
- `PhoneDesk.Tests/MainWindowViewModelTests.cs`
- `PhoneDesk.Tests/LocalizationServiceTests.cs`

Required support files changed to make Task 3 actually work:

- `src/PhoneDesk.Presentation/Localization/UiTextKey.cs`
- `src/PhoneDesk.Presentation/Localization/TranslationCatalog.cs`
- `PhoneDesk.Tests/ScreenshotGenerator.cs`

## Commit

Required commit message:

```text
feat: localize shell and settings
```
