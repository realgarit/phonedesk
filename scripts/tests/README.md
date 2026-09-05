# PowerShell tests

Install the CI-pinned Pester version once:

```powershell
Install-Module Pester -RequiredVersion 5.7.1 -Scope CurrentUser
```

Run from the repository root:

```powershell
Import-Module Pester -RequiredVersion 5.7.1
Invoke-Pester -Path scripts/tests -CI
```

The release tests execute the real bump script in disposable Pester TestDrive fixtures and verify all version sources for major, minor, and patch increments. They never bump the working repository. CI runs them on each supported runner alongside the .NET tests.

Use xUnit in PhoneDesk.IntegrationTests for the application's embedded PowerShell runspace, streams, cancellation, serialization, and authentication orchestration. Pester complements that coverage for standalone PowerShell tooling; no tenant credentials or live tenant writes are needed.
