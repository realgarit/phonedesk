# PowerShell Integration Harness Design

## Objective

Close GitHub issue #76 with deterministic, cross-platform integration coverage around PhoneDesk's real in-process PowerShell runspace and the MSAL orchestration boundary. The harness must require no tenant, credentials, browser, network access, or changes to existing ScriptBuilder output or user-visible authentication behavior.

## Current State

`PhoneDesk.Tests` already contains four real-runspace tests covering cancellation, progress forwarding, runspace reuse, and basic concurrent serialization. Pure unit tests separately cover `PowerShellOperationResultMapper` and `TenantTopologyAssembler`. The remaining gaps are a dedicated integration-test boundary, test cmdlets/modules, end-to-end execution-to-parser coverage, structured error cases, bounded deadlock detection, and direct testing of the silent-to-interactive MSAL orchestration without opening a browser.

## Chosen Approach

Create `PhoneDesk.IntegrationTests`, an xUnit project that references only the Application and Infrastructure projects. Its own assembly will act as a binary PowerShell module: public test-only `PSCmdlet` classes will be imported into each real `PowerShellContextService` runspace. This exercises the exact hosted PowerShell path while keeping all tenant and Microsoft service calls stubbed in process.

The existing four real-runspace tests will move out of `PhoneDesk.Tests` into the integration project. New tests will execute the binary stub cmdlets and pass the returned `PowerShellExecutionResult` through the production `PowerShellOperationResultMapper` and `TenantTopologyAssembler`. A marker or pipe-field contract change will therefore fail an integration test at the same boundary used by Presentation ViewModels.

For MSAL, add an internal `IMsalPublicClient` adapter in Infrastructure. `MsalGraphAuthenticationService` will retain orchestration and logging; the default adapter will retain the existing `PublicClientApplicationBuilder` settings and exact MSAL calls. An internal constructor will accept a fake adapter for tests. No public interface, scope, client ID, redirect URI, parent-window behavior, result tuple, log message, or exception mapping will change.

## Components

### Integration test project

`PhoneDesk.IntegrationTests/PhoneDesk.IntegrationTests.csproj` will target the repository's inherited `net10.0` settings and reference xUnit, the .NET test SDK, coverlet, Application, and Infrastructure. The project will be added to `PhoneDesk.slnx`.

`PowerShellContextServiceIntegrationTests.cs` will cover:

- binary stub-module import into a real persistent runspace;
- success, information, warning, and normal output marshaling;
- `Write-Error` structured records and thrown cmdlet exceptions;
- marker and pipe-delimited topology parsing end to end;
- malformed rows without parser crashes or invalid objects;
- empty successful reads as valid empty results;
- empty output accompanied by an error record as a typed failure;
- environment-variable cleanup after success and failure;
- cooperative cancellation followed by runspace reuse;
- progress forwarding; and
- two simultaneous commands completing serially within a hard timeout.

`StubCmdlets.cs` will contain only deterministic test cmdlets. They will never load Teams or Graph modules. A static concurrency probe will track the maximum number of active cmdlets so the test proves serialization directly instead of inferring it only from output ordering.

### MSAL seam

`IMsalPublicClient.cs` will define internal operations for listing and removing cached accounts, attempting silent token acquisition, and interactive token acquisition. A small internal token result will carry only access-token and username strings. Silent acquisition will explicitly report whether interaction is required so tests need not manufacture MSAL exception objects.

`MsalPublicClient.cs` will wrap `IPublicClientApplication`. It will build the application with the current client ID, Azure public/common authority, and `http://localhost` redirect URI. It will translate only `MsalUiRequiredException` into the internal interaction-required result; all other MSAL exceptions will continue to reach `MsalGraphAuthenticationService` unchanged.

`MsalGraphAuthenticationServiceIntegrationTests.cs` will use an in-memory fake to verify:

- a cached account uses silent acquisition and never opens interactive auth when a token is returned;
- a missing cached account uses interactive acquisition;
- an interaction-required silent result falls back to interactive acquisition;
- the parent window handle is passed through unchanged;
- empty tokens return the existing handled failure tuple; and
- cached-account detection and sign-out enumerate and remove the same accounts as today; and
- no test opens a browser or contacts Microsoft.

Infrastructure will expose internal types only to `PhoneDesk.IntegrationTests` through `InternalsVisibleTo`; no public API will expand.

## Output and Error Semantics

The harness will preserve the current distinction between an empty successful read and an error. Some read cmdlets legitimately return zero rows, so globally converting empty output into a failure would be a product regression. Tests will instead prove both contracts:

- empty output with `HadErrors == false` maps to a valid empty result and consumer parsers return empty collections;
- empty normal output with a PowerShell error record maps to a handled typed failure with structured error details.

Malformed pipe rows will be ignored by `TenantTopologyAssembler`, as today, while valid rows in the same output remain available. Cmdlet exceptions and `Write-Error` records will be surfaced through `PowerShellExecutionResult.Errors` and `PowerShellOperationResultMapper` rather than escaping into the test host.

## CI and Verification

Both the release build matrix and `pr-validate` matrix will run the dedicated integration project explicitly after the unit tests. This covers Windows x64, Linux x64, macOS x64, and macOS arm64. Tests will use bounded waits and cancellation tokens so a semaphore regression fails rather than hanging a CI worker indefinitely.

Verification will include:

- the dedicated integration suite;
- all existing `PhoneDesk.Tests` tests;
- the localization guard;
- a zero-warning Release build of `PhoneDesk.slnx`;
- existing ScriptBuilder snapshot tests to prove byte-identical output; and
- the complete GitHub PR matrix and independent code review.

No UI changes are planned, so screenshot regeneration is not required.

## Alternatives Rejected

Keeping all tests in `PhoneDesk.Tests` was rejected because it hides the integration boundary and makes CI coverage less explicit. Launching an external `pwsh` process was rejected because it would not exercise `PowerShellContextService`'s persistent in-process runspace, streams, semaphore, or cancellation implementation. Using live Microsoft modules was rejected because it would require credentials and make tests slow, mutable, and non-deterministic.

## Acceptance Mapping

- Real runspace with stub cmdlets/modules: binary test module imported into `PowerShellContextService`.
- ViewModel-consumed output parsing: actual execution output passed through the production marker and topology parsers.
- Serialized concurrency without deadlock: direct active-count probe plus bounded `Task.WhenAll`.
- Error paths: thrown cmdlet, `Write-Error`, malformed rows, empty success, and empty-with-error cases.
- All-platform PR execution: explicit integration-test step in every matrix leg.
- Frozen output/auth behavior: no ScriptBuilder changes, unchanged MSAL configuration and public results, full regression suite.
