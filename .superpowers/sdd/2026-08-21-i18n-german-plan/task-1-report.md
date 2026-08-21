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
