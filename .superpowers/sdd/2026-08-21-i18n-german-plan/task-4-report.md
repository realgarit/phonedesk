# Task 4 report

## Status

Completed with Fix Round 1 applied on top of `951ca13`.

## Scope executed

Implemented Task 4 exactly within the approved write set:

- `PhoneDesk.Tests/LocalizationCoverageTests.cs`
- `PhoneDesk.Tests/ScreenshotGenerator.cs` (minimal test-only render coverage extension)
- `src/PhoneDesk.Presentation/Localization/UiTextKey.cs`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json`
- `src/PhoneDesk.Presentation/Views/WelcomeView.axaml`
- `src/PhoneDesk.Presentation/Views/GetStartedView.axaml`
- `src/PhoneDesk.Presentation/Views/WizardView.axaml`
- `src/PhoneDesk.Presentation/Views/VariablesView.axaml`
- `src/PhoneDesk.Presentation/Views/DashboardView.axaml`
- `src/PhoneDesk.Presentation/Views/DocumentationView.axaml`
- `src/PhoneDesk.Presentation/Views/HistoryView.axaml`

No frozen infrastructure/auth/script-builder files were modified.

## Test-first localization coverage

Added a new RED-first coverage test:

- `PhoneDesk.Tests/LocalizationCoverageTests.cs`

Purpose:

- scan the seven Task 4 views for fixed visible literals in the requested attributes
- fail when a visible XAML literal is not a typed translation binding or another explicitly allowed non-localized value

Covered attributes:

- `Text`
- `Content`
- `Header`
- `Title`
- `ToolTip.Tip`
- `Watermark`
- `AutomationProperties.Name`
- `OnContent`
- `OffContent`

Allowed cases:

- `{loc:Translate ...}`
- `{Binding ...}`
- `{DynamicResource ...}`
- `{x:Static ...}`
- bindings whose `StringFormat=` resolves to placeholder-only formatting or a nested non-literal markup expression
- raw `PhoneDesk`
- raw wizard step numerals `1` to `4`

### RED run

Command:

- `dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~LocalizationCoverageTests`

Result:

- failed as expected before implementation

Findings included raw visible literals across all seven scoped pages, including:

- Welcome: hero subtitle, chips, workflow labels, action buttons
- Get Started: page title/subtitle, guidance header, step text, disconnect and ready-state labels
- Wizard: sidebar labels, review snapshot labels, preview/export tooltips, navigation buttons
- Variables: page header, tab headers, examples, watermarks, dialog labels and buttons
- Dashboard: page header, toolbar labels, search watermark, orphan banner, relationship labels, empty state, footer
- Documentation: page header, action labels, export watermark, footer
- History: page header, filter labels, outcome labels, toggle labels, column headers, empty state, footer

### GREEN run

Same command after localization:

- `dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~LocalizationCoverageTests`

Result:

- passed

## Localization implementation

### Typed keys

Expanded `UiTextKey` for the Task 4 surface so the seven views could use typed `TranslateExtension` bindings.

Reviewer-corrected parity for the original Task 4 shared enum/catalog surface at commit `951ca13`:

- shared `UiTextKey` + English catalog + German catalog entries: `281 / 281 / 281`

Verification method:

- computed the normalized intersection of enum entries and both catalog key sets instead of relying on the stale handwritten statement

Fix Round 1 follow-up:

- added the six missing German catalog entries required by the Variables/Dashboard bindings exposed by the strengthened coverage test
- post-fix full-file parity is now `287 / 287 / 287`

### Catalogs

Added semantic keys to both:

- `src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json`

Terminology applied per brief and design:

- concise operational wording
- reviewed German product terminology
- semantic, page-oriented keys instead of inline literals

### Views localized

All fixed, active, visible XAML literals in the scoped pages were converted to typed `TranslateExtension` bindings:

- `WelcomeView.axaml`
- `GetStartedView.axaml`
- `WizardView.axaml`
- `VariablesView.axaml`
- `DashboardView.axaml`
- `DocumentationView.axaml`
- `HistoryView.axaml`

Preserved as required:

- tags
- commands
- bindings
- converters
- technical/product values
- tenant/API values
- page behavior

Not localized by design in this task:

- runtime ViewModel strings
- dialog/runtime error strings
- log strings

These remain Task 6 unless the literal was fixed directly in page XAML.

## Screenshot rendering and visual inspection

### Minimal test-only harness change

`PhoneDesk.Tests/ScreenshotGenerator.cs` was extended only to render the additional required German states for the Task 4 pages.

Behavioral scope of the test-only change:

- add German screenshot scenarios
- reuse the same failed Wizard setup for `failed-de` that already existed for `failed`
- switch locale to German when scenario name ends with `-de`

No production behavior was changed.

### Render command

- `$env:GENERATE_SCREENSHOTS='1'`
- `$env:SCREENSHOT_FRAMELESS='1'`
- `$env:SCREENSHOT_OUT='C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task4'`
- `dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~ScreenshotGenerator`

Result:

- passed

### Rendered and inspected outputs

Directory:

- `C:\Users\realgar\.codex\visualizations\2026\08\21\01a025ff-a85b-7640-8a96-77b1e07c8882\task4`

Inspected images:

- `shell-settings-de.png`
- `get-started-ready-de.png`
- `variables-de.png`
- `setup-wizard-ready-de.png`
- `setup-wizard-failed-de.png`
- `dashboard-de.png`
- `documentation-de.png`
- `history-de.png`

Inspection result:

- all rendered successfully
- localized fixed page labels/buttons/headers were present
- `variables-de.png` stayed visually clean on the German general-configuration surface; the greeting/call-flow wording corrections themselves live deeper in Variables dialogs and were verified from the updated XAML plus coverage checks
- `setup-wizard-failed-de.png` now shows the failed Wizard state in German instead of the ready-state content
- no blocking clipping, overlap, or alignment defects found in the inspected screenshots

Observed but out of scope for Task 4:

- some runtime ViewModel text in wizard and variables screenshots remains English
- this matches the brief boundary for Task 6 and does not come from fixed page XAML literals

## Verification

### Targeted tests

- `dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~LocalizationCoverageTests`
- `dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~ScreenshotGenerator`

Results:

- both passed after implementation

### Full solution suite

- `dotnet test PhoneDesk.slnx --no-restore`

Result:

- passed
- `Failed: 0, Passed: 577, Skipped: 0, Total: 577`

## Fix Round 1 changed files

- `PhoneDesk.Tests/LocalizationCoverageTests.cs`
- `PhoneDesk.Tests/ScreenshotGenerator.cs`
- `src/PhoneDesk.Presentation/Localization/UiTextKey.cs`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json`
- `src/PhoneDesk.Presentation/Views/VariablesView.axaml`
- `src/PhoneDesk.Presentation/Views/DashboardView.axaml`

## Commit

- Fix Round 1 commit message:

- `fix: address Task 4 localization review`
