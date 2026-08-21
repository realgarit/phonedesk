# Task 2 report — resource loading, live XAML binding, and DI wiring

Date: 2026-08-21
Branch: `codex/german-localization`
Base commit confirmed: `b68ab6e946da02255f56198544c2c753f370bc90`

## Scope completed

- Added `TranslationCatalogLoader` to load UTF-8 JSON catalogs from Avalonia `avares://` resources into `UiTextKey`-keyed dictionaries and reject unknown keys or malformed JSON with source context.
- Added `TranslateExtension` with typed `UiTextKey Key` and a live one-way binding to the singleton catalog stored in `Application.Resources["TranslationCatalog"]`.
- Added minimal shipped catalogs:
  - `Strings.en.json`
  - `Strings.de.json`
- Extended `ViewModelBase` with:
  - `public TranslationCatalog Text`
  - optional final `ITranslationService? translationService = null` constructor parameter
  - protected `_translationService` field for typed runtime access
- Registered `IUserPreferencesStore` and `ITranslationService` as singletons in `Program.ConfigureServices`.
- Loaded the singleton catalog into app resources in `App.OnFrameworkInitializationCompleted` before constructing `MainWindow`.
- Wired `MainWindowViewModel` to pass the optional translation service through to `ViewModelBase` and use the typed `UiTextKey.UpdateAvailable` message for the update banner.
- Added focused tests in `PhoneDesk.Tests/TranslationCatalogLoaderTests.cs`.
- Extended `PhoneDesk.Tests/TestSupport/ViewModelTestHarness.cs` with a default translation service fixture for base ViewModel testing.

## TDD evidence

### RED

Added the failing tests first, then ran:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~TranslationCatalogLoaderTests
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~ViewModelBaseTests
```

Observed expected compile-time RED failures before implementation:

- `TranslationCatalogLoader` did not exist.
- `ViewModelBase` had no `Text` surface.
- `ViewModelBase` had no optional `translationService` constructor parameter.

### GREEN

After implementation, reran focused checks:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~TranslationCatalogLoaderTests
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~ViewModelBaseTests
```

Results:

- `TranslationCatalogLoaderTests`: 4 passed
- `ViewModelBaseTests`: 9 passed

## Verification

Fresh verification on the final tree:

```text
dotnet build src/PhoneDesk.Presentation/PhoneDesk.Presentation.csproj --no-restore
dotnet test PhoneDesk.slnx --no-restore
```

Results:

- Presentation build succeeded with `0 Warning(s)` and `0 Error(s)`.
- Full suite passed: `565` passed, `0` failed, `0` skipped.

## Interface / write-set notes

- No Task 1 interface mismatch was found; the existing `TranslationService` and `IUserPreferencesStore` APIs were sufficient.
- `PhoneDesk.Presentation.csproj` did not require a change because the existing `AvaloniaResource Include="Resources\\**"` already embeds `Resources/Localization/*.json` in the presentation assembly.
- `App.axaml` did not require a change for Task 2 because the live catalog resource is injected in `App.OnFrameworkInitializationCompleted`, and no page XAML localization conversion is allowed yet in this task.
- No page XAML files were modified.
- `AGENTS.md` was not modified, per the task brief.

## Files changed

- `src/PhoneDesk.Presentation/Localization/TranslationCatalogLoader.cs`
- `src/PhoneDesk.Presentation/Localization/TranslateExtension.cs`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json`
- `src/PhoneDesk.Presentation/ViewModels/ViewModelBase.cs`
- `src/PhoneDesk.Presentation/ViewModels/MainWindowViewModel.cs`
- `Program.cs`
- `src/PhoneDesk.Presentation/App.xaml.cs`
- `PhoneDesk.Tests/TranslationCatalogLoaderTests.cs`
- `PhoneDesk.Tests/TestSupport/ViewModelTestHarness.cs`

## Fix Round 1 — update banner localization refresh and recovery

Scope:

- Replaced the remaining raw-English update-available recovery assignments in `MainWindowViewModel` cancel/failure paths with the typed translation flow.
- Centralized update banner text generation behind one helper that tracks the current banner state and recomputes from `UiTextKey` plus the latest version/progress state.
- Subscribed `MainWindowViewModel` to `ITranslationService.PropertyChanged`, refreshed visible banner text when `CurrentLanguage` changes, and detached the handler in `Dispose`.
- Added focused regression tests covering:
  - visible update banner refresh after switching to German
  - cancel recovery staying localized
  - failure recovery staying localized

### Fix Round 1 RED

Command:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~MainWindowViewModelTests
```

Observed output:

```text
Failed PhoneDesk.Tests.MainWindowViewModelTests.InstallUpdateCommand_CancelRecovery_UsesCurrentLanguage
Expected: "Version 2.0.0 ist verfügbar."
Actual:   "Version 2.0.0 is available."

Failed PhoneDesk.Tests.MainWindowViewModelTests.InstallUpdateCommand_FailureRecovery_UsesCurrentLanguage
Expected: "Version 2.0.0 ist verfügbar."
Actual:   "Version 2.0.0 is available."

Failed PhoneDesk.Tests.MainWindowViewModelTests.VisibleUpdateBanner_RefreshesWhenLanguageChangesToGerman
Expected: "Version 2.0.0 ist verfügbar."
Actual:   "Version 2.0.0 is available."

Failed!  - Failed:     3, Passed:    17, Skipped:     0, Total:    20, Duration: 192 ms - PhoneDesk.Tests.dll (net10.0)
```

### Fix Round 1 GREEN

Command:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~MainWindowViewModelTests
```

Observed output:

```text
Passed!  - Failed:     0, Passed:    20, Skipped:     0, Total:    20, Duration: 234 ms - PhoneDesk.Tests.dll (net10.0)
```

### Fix Round 1 full-suite verification

Command:

```text
dotnet test PhoneDesk.slnx --no-restore
```

Observed output:

```text
Passed!  - Failed:     0, Passed:   568, Skipped:     0, Total:   568, Duration: 3 s - PhoneDesk.Tests.dll (net10.0)
```
