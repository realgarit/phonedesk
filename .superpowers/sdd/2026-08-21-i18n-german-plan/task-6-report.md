## Task 6 report — Cluster B1

Date: 2026-08-22

### Scope in this cluster

- Localized the remaining fixed runtime sinks in:
  - `CallQueuesViewModel`
  - `GetStartedViewModel`
  - `M365GroupsViewModel`
- Finished the incomplete Call Queues runtime coverage from the earlier Task 6 pass.
- Added typed `UiTextKey` entries and English/German catalog parity for:
  - fixed load/create/delete/status messages
  - fixed confirmation/preview context labels
  - fixed setup guidance and step text in Get Started
  - fixed displayed log wrappers
- Preserved inserted technical/runtime values exactly:
  - M365 group IDs, group names, UPNs, usage locations, raw PowerShell output, error details, tenant/account values, and command results
- Kept frozen infrastructure/auth/script builders, Domain/API contracts, XAML, and unrelated production files untouched.

### RED

Added the new focused runtime-localization coverage first, then ran:

`dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~RuntimeLocalizationTests`

Initial RED evidence:

- `SwitchingLanguage_LocalizesCallQueueGroupIdStatus_AndPreservesGroupId` failed because Call Queues still emitted the old fixed English `retrieved` text instead of the required localized `Load` wording.
- `SwitchingLanguage_LocalizesM365GroupCreateStatus_AndPreservesName` initially exposed the create-status auto-refresh overwrite; the test was tightened to disable auto-refresh, then remained red until the create status/log wrappers were localized.

### GREEN

Focused runtime localization tests:

`dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter FullyQualifiedName~RuntimeLocalizationTests`

Result: Passed 12/12

Focused affected ViewModel/runtime slice:

`dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeLocalizationTests|FullyQualifiedName~CallQueuesViewModelTests|FullyQualifiedName~GetStartedViewModelTests|FullyQualifiedName~M365GroupsViewModelTests"`

Result: Passed 98/98

Full solution:

`dotnet test PhoneDesk.slnx --no-restore`

Result: Passed 589/589

### Runtime sink coverage in this cluster

- `CallQueuesViewModel`
  - page-load log
  - load resource-account status/log/success/no-output wrappers
  - load call-queue status/log/success/no-output wrappers
  - assign-license error log wrapper
  - load/save M365 group ID status/success/error/parse wrappers
  - create resource-account validation/context/success/error/log wrappers
  - update usage-location validation/context/success/error/log wrappers
  - create call-queue validation/context/success/error/log wrappers
  - associate resource-account validation/context/success/error/log wrappers
  - delete call-queue context/success/error/log wrappers
  - delete resource-account context/confirmation/success/error/log wrappers
- `GetStartedViewModel`
  - page-load and navigation-blocked logs
  - module/Teams/Graph connect/disconnect log wrappers
  - setup guidance and detail text
  - step status text
  - action button text
  - live language refresh for the visible computed text on the current ViewModel instance
- `M365GroupsViewModel`
  - page-load log
  - load-groups status/log/success/no-output wrappers
  - create-group validation/context/success/already-exists/error/log wrappers
  - delete-group validation/context/confirmation/success/error/log wrappers
  - check-group configuration-missing/log/parse/status wrappers

### Runtime localization tests added in this cluster

- `SwitchingLanguage_LocalizesCallQueueGroupIdStatus_AndPreservesGroupId`
- `SwitchingLanguage_LocalizesM365GroupCreateStatus_AndPreservesName`

### Files changed in this cluster

- `.superpowers/sdd/2026-08-21-i18n-german-plan/task-6-report.md`
- `PhoneDesk.Tests/CallQueuesViewModelTests.cs`
- `PhoneDesk.Tests/M365GroupsViewModelTests.cs`
- `PhoneDesk.Tests/RuntimeLocalizationTests.cs`
- `PhoneDesk.Tests/TestSupport/ViewModelTestHarness.cs`
- `src/PhoneDesk.Presentation/Localization/UiTextKey.cs`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json`
- `src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json`
- `src/PhoneDesk.Presentation/ViewModels/CallQueuesViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/GetStartedViewModel.cs`
- `src/PhoneDesk.Presentation/ViewModels/M365GroupsViewModel.cs`
