# Task 6 report — runtime localization for messages and dialogs

Date: 2026-08-21

## Scope delivered

- Added typed runtime localization coverage for ViewModelBase runtime messages, dialog chrome, error dialog titles/wrappers, update banner text, and Holidays runtime statuses.
- Threaded optional `ITranslationService?` through `ViewModelBase` and all 12 concrete ViewModels as the final optional constructor parameter.
- Preserved command text, script body, technical identifiers, and runtime error details exactly as inserted values.
- Kept infrastructure/auth/script builders, Domain/API contracts, AGENTS.md, and emitted PowerShell/script text unchanged.

## RED evidence

Added `PhoneDesk.Tests/RuntimeLocalizationTests.cs` first, then ran:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~RuntimeLocalizationTests
```

Initial result: RED at compile time, driven by missing task-6 production changes:

- `HolidaysViewModel` missing the new optional translation-service constructor parameter
- `ErrorHandlingService` missing the translation-service constructor overload
- `DialogService` missing the translation-service constructor overload
- `UiTextKey` missing new runtime/dialog keys such as `HolidaysVerifyAutoAttendantSuccess` and `ErrorPowerShellMessage`

This established the expected failing starting point before implementation.

## GREEN evidence

Runtime localization tests:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~RuntimeLocalizationTests
```

Result: Passed 4/4

Focused runtime and affected regression set:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeLocalizationTests|FullyQualifiedName~ErrorHandlingServiceTests|FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~HolidaysViewModelTests|FullyQualifiedName~ViewModelBaseTests"
```

Result: Passed 62/62

Screenshot generator:

```text
GENERATE_SCREENSHOTS=1 SCREENSHOT_OUT=C:/Users/realgar/.codex/visualizations/2026/08/21/task6-runtime dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~ScreenshotGenerator
```

Result: Passed 1/1

Full solution:

```text
dotnet test PhoneDesk.slnx --no-restore
```

Result: Passed 581/581

## Runtime sink coverage

- `ViewModelBase`
  - localized fixed status/waiting/cancel/session-expired/error wrapper text
  - added typed helpers for text lookup, status updates, waiting text, localized log wrappers, and error formatting
- `DialogService`
  - localized fixed dialog chrome: `OK`, `Cancel`, `Execute`, `Confirm & Execute`
  - localized fixed script-preview wrapper text and watermark
- `ErrorHandlingService`
  - localized PowerShell/validation/connection/generic/confirmation/success/info titles and wrapper messages
  - preserved inserted `{command}`, `{error}`, `{service}`, `{message}`, `{title}` values exactly
- `MainWindowViewModel`
  - localized update-install confirmation title/message and update-failed title
- `HolidaysViewModel`
  - localized fixed success/precondition/failure/runtime status text added in task 6
- Constructor threading
  - optional translation service threaded through all 12 concrete ViewModels without breaking direct-constructor call sites

## Catalog parity

Added the task-6 runtime/dialog/error keys to:

- `src/PhoneDesk.Presentation/Localization/UiTextKey.cs`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json`
- `PhoneDesk.Tests/TestSupport/ViewModelTestHarness.cs`

During screenshot verification, the real catalogs exposed one missing shipped key:

- `DialogCancel`

This was added to both real catalogs before the final verification pass.

## Screenshot renders and inspection

Output directory:

`C:\Users\realgar\.codex\visualizations\2026\08\21\task6-runtime`

Rendered and inspected:

- `task6-update-banner-en.png`
- `task6-update-banner-de.png`
- `task6-script-preview-en.png`
- `task6-script-preview-de.png`
- `task6-confirmation-en.png`
- `task6-confirmation-de.png`
- `task6-error-dialog-en.png`
- `task6-error-dialog-de.png`

Inspection result:

- update banners: English and German text rendered cleanly with version `3.26.0` preserved and no clipping
- script preview dialogs: localized titles/chrome/buttons rendered; script body preserved exactly as `Get-CsCallQueue -Identity cq-Contoso`
- destructive confirmation dialogs: localized title/body/chrome/buttons rendered; destructive script preserved exactly as `Remove-CsCallQueue -Identity cq-Contoso`
- error dialogs: localized title/wrapper rendered; inserted runtime detail preserved exactly as `Request failed with 404`
- no stale fixed English remained in the German dialog/banner states reviewed
- German long labels fit without truncation in the rendered states reviewed

Note: the existing headless path did not render Fluent `ContentDialog` overlays directly for capture, so `PhoneDesk.Tests/ScreenshotGenerator.cs` was minimally extended to render screenshot-only dialog surfaces from the same localized dialog state for visual verification.

## Deferred/native-output boundary

None beyond the required preserved-technical-value boundary:

- command text, PowerShell script text, identifiers, and runtime error/native output details remain untranslated inserted values by design

## Changed files

- `.superpowers/sdd/2026-08-21-i18n-german-plan/task-6-report.md`
- `PhoneDesk.Tests/RuntimeLocalizationTests.cs`
- `PhoneDesk.Tests/ScreenshotGenerator.cs`
- `PhoneDesk.Tests/TestSupport/ViewModelTestHarness.cs`
- `src/PhoneDesk.Presentation/Localization/UiTextKey.cs`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json`
- `src/PhoneDesk.Presentation/Services/DialogService.cs`
- `src/PhoneDesk.Presentation/Services/ErrorHandlingService.cs`
- `src/PhoneDesk.Presentation/ViewModels/AutoAttendantsViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/BulkOperationsViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/CallQueuesViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/DashboardViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/DocumentationViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/GetStartedViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/HistoryViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/HolidaysViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/M365GroupsViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/MainWindowViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/VariablesViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/ViewModelBase.cs`
- `src/PhoneDesk.Presentation/ViewModels/WelcomeViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/WizardViewModel.cs`
