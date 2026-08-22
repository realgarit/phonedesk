# PhoneDesk English/German UI Localization Design

## Goal

Make the shipped PhoneDesk application fully localizable between English and German. English remains the default and fallback language. German is selectable from the existing Settings panel, changes apply while the application is running, and the choice is persisted for the current user.

All application-facing text must come from the localization system. The implementation must make typed translation keys the normal authoring path and must fail a build/test or repository check when a contributor adds a visible literal or an incomplete catalog.

## Scope

In scope:

- The active Avalonia UI in `src/PhoneDesk.Presentation`, including the main window, navigation, all active pages, dialogs, empty states, tooltips, watermarks, accessibility names, update notifications, progress/status text, confirmation/error text, and user-visible log messages.
- English source wording and German wording for every in-scope string.
- Runtime language selection in the existing Settings side panel.
- Per-user persistence with English as the safe default when no preference exists or a stored value is invalid.
- Catalog/key validation, placeholder validation, raw-label detection, tests, CI enforcement, and a developer hook installer.
- English and German headless screenshot states for visual review.

Out of scope:

- Translating tenant data, customer-provided names, CSV contents, PowerShell commands, Graph/API values, language IDs, time-zone IDs, UPNs, or generated configuration values.
- Changing emitted script text or behavior in `ScriptBuilders`, `MsalGraphAuthenticationService`, or `PowerShellContextService`. These surfaces are frozen by `AGENTS.md`.
- Translating archived files under `Archive/`, which are not part of the shipped build.
- Adding a third language. The design must leave room for one without requiring a new UI architecture.

## Evidence from the current repository

- The repository is an Avalonia/.NET 10 application with a singleton `ISharedStateService`, DI composition in `Program.cs`, and an existing settings side panel in `MainWindow.axaml`.
- The active UI has 13 XAML files with 634 literal values in visible-text attributes before localization. This count includes repeated labels and is a guard baseline, not the final catalog size.
- Existing tests use xUnit and the baseline suite passes before this change.
- The release workflow runs build and test on Windows, macOS, and Linux, so the guard must be cross-platform.

## User-facing language rules

The copy is operational, direct, and meaning-first:

- Use short labels and direct actions: `Open`, `Close`, `Load`, `Create`, `Delete`, `Validate`.
- Use one clear action per sentence. Avoid motivational filler, vague promises, and AI-style phrases.
- Prefer the established Microsoft German product terminology where a term has a defined product meaning. Microsoft Learn uses `Anrufwarteschleife`, `automatische Telefonzentrale`, and `Ressourcenkonto` for the relevant Teams Phone concepts.
- Keep product, protocol, API, and file-format names unchanged where they identify a product or technical value: `PhoneDesk`, `Microsoft Teams`, `Microsoft Graph`, `PowerShell`, `UPN`, `CSV`, `M365`.
- Do not translate a domain term only because its English word has a familiar German word. Preserve the operational meaning. For example, `provisioning` describes creating and preparing tenant objects; use `Einrichtung` or `Bereitstellung` according to the sentence, not an automatic literal translation.
- Use neutral German UI wording without unnecessary pronouns. Prefer `Anmeldung erforderlich` and `Erneut verbinden` over long formal or conversational sentences.
- Use the same German term consistently for the same domain concept. The translator review is the terminology authority for ambiguous cases and records exceptions.

The independent translator review also identified source-English wording that is too internal or ambiguous for operators. The catalog should correct these source labels before translating them:

- `Retrieve` becomes `Load` for data-loading actions.
- `Remove` becomes `Delete` where the operation is permanent.
- `Get Started` becomes `Readiness Check` where the page checks prerequisites and sign-in state.
- `Variables` becomes `Configuration` in operator navigation and headings.
- `Operation History` becomes `Audit Log` where the data is persistent and compliance-relevant.
- `Documentation` becomes `Tenant Report` where the action exports a report.
- `Step-by-Step Creation` becomes `Guided Setup`.
- Internal model wording such as `Call Handling Association`, `Call Target`, `AA`, and `CQ` is replaced with the operator action or full product term.
- `orphans` becomes `Unlinked objects` in English and `Nicht zugeordnete Objekte` in German; it is never translated literally.

ASD-STE100 is an English controlled-language standard, not a German translation standard. Its principles are applied as a style constraint: controlled vocabulary, short sentences, explicit actions, and one meaning per term. The German text follows the same safety and clarity goals without pretending that an English dictionary governs German grammar.

## Alternatives considered

### A. Strongly typed in-repository catalog — selected

Use a `UiTextKey` enum, JSON catalogs for `en` and `de`, and a singleton translation service with an observable typed catalog. XAML uses a `TranslateExtension` whose `Key` property is `UiTextKey`; ViewModels request formatted text through `UiTextKey` values.

Advantages:

- No runtime package dependency.
- Invalid keys in C# are compiler errors.
- JSON is easy for a human translator to review.
- A guard can compare enum keys, both catalogs, placeholders, XAML literals, and known C# UI sinks.
- Runtime language changes can notify all active bindings.

Cost:

- A small typed catalog wrapper and guard are maintained in the repository.
- XAML still needs a static check because Avalonia bindings are not fully compile-time checked.

### B. `.resx` with `ResourceManager`

This uses standard .NET resources and culture fallback. It reduces custom file loading but makes the generated strongly typed surface and XAML change notifications less transparent, and it does not by itself detect raw XAML literals or missing German entries.

### C. Third-party Avalonia localization package

This could reduce initial binding code, but it adds a dependency, moves enforcement outside the repository, and does not provide the requested typed authoring guard without another layer. It is not selected.

## Architecture

### Catalog and keys

Create a presentation-owned localization area:

- `UiTextKey` contains one member per user-facing string.
- `Strings.en.json` is the source/default catalog.
- `Strings.de.json` contains exactly the same keys.
- Values may contain named placeholders such as `{version}`, `{count}`, or `{name}`. Placeholder names and multiplicity must match between languages.
- `TranslationCatalog` exposes an enum indexer and a typed `Get(UiTextKey, IReadOnlyDictionary<string, object?>?)` method for formatted ViewModel text.
- `TranslateExtension` accepts only `UiTextKey` and returns a live binding to the observable catalog. This avoids string key paths in XAML while still updating when the language changes.
- The catalog exposes language-neutral product names separately where they are intentionally not translated.

### Runtime service

Add `ITranslationService` and a singleton implementation:

- `CurrentLanguage` is an enum with `English` and `German` values.
- `Text` is the observable catalog object used by XAML.
- Setting `CurrentLanguage` reloads the selected catalog, raises the language and catalog property notifications, and does not restart the application.
- Unknown/invalid values fall back to English.
- The service does not mutate domain configuration values.

`ViewModelBase` exposes the shared typed text object for normal page bindings. The application resource dictionary also exposes the same object for templates whose `DataContext` is a row/model rather than a page ViewModel.

### Persistence

Add a small `IUserPreferencesStore` boundary with a JSON-backed implementation in the presentation/application composition boundary. Store only the language preference in this feature unless an existing setting store is found during implementation. Use `Environment.SpecialFolder.LocalApplicationData` and the existing PhoneDesk application-data identity. Write atomically and tolerate a missing, malformed, or unsupported file.

The service loads the preference before the main window is shown. If persistence fails, the in-memory selection still works and the failure is logged without blocking startup.

### Settings UI

Add a `Language` section near `Appearance` in the existing Settings panel:

- Label: localized `Language`/`Sprache`.
- Choices: `English` and `Deutsch` as native language names so users can always identify the choice.
- The selection is two-way bound to `CurrentLanguage`.
- The Settings panel itself, navigation, page content, dialogs, status messages, and accessibility names update immediately.

### Static enforcement

Add a cross-platform `PhoneDesk.LocalizationGuard` tool under `tools/` and invoke it from:

- the presentation build before compilation;
- the test suite through dedicated guard tests;
- CI before the normal build/test gate;
- an installable Git pre-commit hook.

The guard must:

1. Load both JSON catalogs and fail on duplicate keys, missing keys, extra keys, empty values, invalid key names, invalid JSON, or placeholder mismatch.
2. Verify every `UiTextKey` enum member has exactly one value in both catalogs.
3. Scan active XAML only and fail on literal values in user-facing attributes (`Text`, `Content`, `Header`, `Title`, `ToolTip.Tip`, `Watermark`, `AutomationProperties.Name`, and equivalent visible attributes), with a narrow documented allowlist for product names, technical identifiers, binding syntax, and non-user-facing metadata.
4. Scan known C# UI sinks (`StatusMessage`, `WaitingMessage`, `ProgressText`, update-banner text, dialog arguments, user-visible status helpers, and logging calls whose output is shown in the log viewer) and fail on literal user language. Technical commands and data values remain outside the sink list.
5. Report file and line for every finding so a contributor can replace it with a typed key.

The guard is deliberately a repository tool rather than a runtime-only test: a contributor who adds a manual label gets an actionable failure before the change can pass CI. The test suite also exercises the guard using temporary fixtures so the detector itself cannot silently regress.

### Testing and visual evidence

Add tests for:

- English default when no preference exists.
- German selection and immediate observable catalog update.
- Invalid stored language fallback.
- Persisted language reload.
- Catalog parity and placeholder parity.
- Guard rejection of a raw XAML label and a raw known C# UI sink.
- Guard acceptance of product/API identifiers and binding expressions.
- Representative formatted strings in both languages.

Extend the existing headless screenshot generator to render at least:

- the main shell and Settings panel in English;
- the same shell and Settings panel in German;
- one populated page and one dialog in both languages;
- a long German message/state to catch clipping and wrapping regressions.

Inspect the generated PNGs for overflow, clipping, alignment, and readable action labels before the PR is opened.

## Acceptance criteria

- A fresh install starts in English.
- A user can select Deutsch in Settings and the complete active UI changes without restarting.
- Relaunching preserves the selected language; corrupt or unknown preference data safely returns to English.
- No active user-facing label remains hard-coded outside the catalog/approved technical allowlist.
- English and German catalogs have identical keys and matching placeholders.
- The static guard runs in local tests, build/CI, and the documented pre-commit hook path.
- German wording has been reviewed by the independent translator agent and corrected for meaning, domain terminology, and concise operational style.
- All existing tests remain green, the new tests pass, the cross-platform build passes, and affected UI screenshots were visually inspected.
- Frozen script builders and auth behavior are byte-for-byte behaviorally unchanged.
