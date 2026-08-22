# Task 1 report

Date: 2026-08-21
Branch: `codex/german-localization`

## Scope completed

Implemented only Task 1 of the German localization plan:

- Added typed localization core in `src/PhoneDesk.Presentation/Localization/`
- Added preference persistence boundary and JSON store in `src/PhoneDesk.Presentation/Services/`
- Added focused tests in `PhoneDesk.Tests/LocalizationServiceTests.cs` and `PhoneDesk.Tests/UserPreferencesStoreTests.cs`

No XAML, `Program.cs`, `App.xaml.cs`, `ViewModelBase`, guard code, or frozen infrastructure/auth/script builder code was changed.

## TDD evidence

### RED

Added the two new focused test files first, then ran:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~LocalizationServiceTests -nodeReuse:false -p:UseSharedCompilation=false -m:1
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~UserPreferencesStoreTests -nodeReuse:false -p:UseSharedCompilation=false -m:1
```

Observed expected missing-type compile failures for `PhoneDesk.Localization`, `TranslationService`, `AppLanguage`, `IUserPreferencesStore`, and `UserPreferencesStore`.

Note: an earlier parallel attempt hit MSBuild/Avalonia file locks on Windows and did not provide a valid RED signal. After root-cause check, sequential focused runs were used for the actual RED/GREEN evidence.

### GREEN

Implemented the minimum production code required for the tests:

- `AppLanguage` enum with `English` and `German`
- initial semantic `UiTextKey` members: `SettingsTitle`, `UpdateAvailable`
- `ITranslationService`
- observable `TranslationCatalog`
- `TranslationService` with English default/fallback, persisted language load/save, placeholder replacement, and binding notifications
- `IUserPreferencesStore`
- `UserPreferencesStore` with explicit-directory test constructor, production `%LocalAppData%/PhoneDesk/settings.json` constructor, safe malformed/missing reads, and atomic temp-file writes

Then ran the focused tests sequentially:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~LocalizationServiceTests -nodeReuse:false -p:UseSharedCompilation=false -m:1
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~UserPreferencesStoreTests -nodeReuse:false -p:UseSharedCompilation=false -m:1
```

Both passed:

- `LocalizationServiceTests`: 5/5
- `UserPreferencesStoreTests`: 5/5

## Full verification

Ran the required full suite once:

```text
dotnet test PhoneDesk.slnx --no-restore
```

Result:

- Passed: 561
- Failed: 0
- Skipped: 0

## Behavior delivered

- English is the default when no preference exists.
- Invalid stored enum values fall back to English.
- Changing `CurrentLanguage` updates the observable catalog, raises `CurrentLanguage`, raises `Text`, and raises catalog refresh notifications for `Item[]` and full refresh.
- Named placeholders such as `{version}` are replaced from provided values without changing inserted technical values.
- Missing translations still throw a clear `KeyNotFoundException` at the catalog layer when neither the selected language nor English provides the key.
- Preferences persist as JSON under `PhoneDesk/settings.json` with lowercase `language`.
- Missing, malformed, or unknown preference values are handled safely.
- Saves write through `settings.json.tmp` and replace the target, with temp-file cleanup after completion.

## Known limits left for later tasks

- No resource loading, DI wiring, or live XAML binding yet. Task 2 owns that work.
- `UiTextKey` currently contains only the semantic keys required for Task 1 tests and is ready to extend in later tasks.

## Fix Round 1

Date: 2026-08-21

Applied only the two review findings:

- Removed the Task 1-added `AGENTS.md` working-note entry so Task 1 no longer changes that file.
- Corrected the German test fixture value from `verfugbar` to `verfügbar`.

### Diff verification against `306aee4`

Command:

```text
git -c safe.directory=C:/Git/phonedesk diff 306aee4 -- AGENTS.md PhoneDesk.Tests/LocalizationServiceTests.cs ".superpowers/sdd/2026-08-21-i18n-german-plan/task-1-report.md"
```

Result:

- `AGENTS.md` no longer appears in the diff from `306aee4`.
- The diff still contains the intended Task 1 test fixture and report changes.

### Focused reruns

Command:

```text
dotnet build-server shutdown
```

Output:

```text
Shutting down MSBuild server...
Shutting down VB/C# compiler server...
VB/C# compiler server shut down successfully.
MSBuild server shut down successfully.
```

Command:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~LocalizationServiceTests -nodeReuse:false -p:UseSharedCompilation=false -m:1
```

Output:

```text
  PhoneDesk.Domain -> C:\Git\phonedesk\src\PhoneDesk.Domain\bin\Debug\net10.0\PhoneDesk.Domain.dll
  PhoneDesk.Application -> C:\Git\phonedesk\src\PhoneDesk.Application\bin\Debug\net10.0\PhoneDesk.Application.dll
  PhoneDesk.Infrastructure -> C:\Git\phonedesk\src\PhoneDesk.Infrastructure\bin\Debug\net10.0\PhoneDesk.Infrastructure.dll
  PhoneDesk.Presentation -> C:\Git\phonedesk\src\PhoneDesk.Presentation\bin\Debug\net10.0\PhoneDesk.Presentation.dll
  phonedesk -> C:\Git\phonedesk\bin\Debug\net10.0\phonedesk.dll
  PhoneDesk.Tests -> C:\Git\phonedesk\PhoneDesk.Tests\bin\Debug\net10.0\PhoneDesk.Tests.dll
Test run for C:\Git\phonedesk\PhoneDesk.Tests\bin\Debug\net10.0\PhoneDesk.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 34 ms - PhoneDesk.Tests.dll (net10.0)
```

Command:

```text
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~UserPreferencesStoreTests -nodeReuse:false -p:UseSharedCompilation=false -m:1
```

Output:

```text
  PhoneDesk.Domain -> C:\Git\phonedesk\src\PhoneDesk.Domain\bin\Debug\net10.0\PhoneDesk.Domain.dll
  PhoneDesk.Application -> C:\Git\phonedesk\src\PhoneDesk.Application\bin\Debug\net10.0\PhoneDesk.Application.dll
  PhoneDesk.Infrastructure -> C:\Git\phonedesk\src\PhoneDesk.Infrastructure\bin\Debug\net10.0\PhoneDesk.Infrastructure.dll
  PhoneDesk.Presentation -> C:\Git\phonedesk\src\PhoneDesk.Presentation\bin\Debug\net10.0\PhoneDesk.Presentation.dll
  phonedesk -> C:\Git\phonedesk\bin\Debug\net10.0\phonedesk.dll
  PhoneDesk.Tests -> C:\Git\phonedesk\PhoneDesk.Tests\bin\Debug\net10.0\PhoneDesk.Tests.dll
Test run for C:\Git\phonedesk\PhoneDesk.Tests\bin\Debug\net10.0\PhoneDesk.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 82 ms - PhoneDesk.Tests.dll (net10.0)
```

### Full suite rerun

Command:

```text
dotnet test PhoneDesk.slnx --no-restore
```

Output:

```text
  PhoneDesk.Domain -> C:\Git\phonedesk\src\PhoneDesk.Domain\bin\Debug\net10.0\PhoneDesk.Domain.dll
  PhoneDesk.Application -> C:\Git\phonedesk\src\PhoneDesk.Application\bin\Debug\net10.0\PhoneDesk.Application.dll
  PhoneDesk.Presentation -> C:\Git\phonedesk\src\PhoneDesk.Presentation\bin\Debug\net10.0\PhoneDesk.Presentation.dll
  PhoneDesk.Infrastructure -> C:\Git\phonedesk\src\PhoneDesk.Infrastructure\bin\Debug\net10.0\PhoneDesk.Infrastructure.dll
  phonedesk -> C:\Git\phonedesk\bin\Debug\net10.0\phonedesk.dll
  PhoneDesk.Tests -> C:\Git\phonedesk\PhoneDesk.Tests\bin\Debug\net10.0\PhoneDesk.Tests.dll
Test run for C:\Git\phonedesk\PhoneDesk.Tests\bin\Debug\net10.0\PhoneDesk.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:   561, Skipped:     0, Total:   561, Duration: 4 s - PhoneDesk.Tests.dll (net10.0)
```
