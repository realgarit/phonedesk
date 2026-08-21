# First-run onboarding and Setup Wizard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make PhoneDesk safe and welcoming for a non-specialist operator completing a first Teams Phone project.

**Architecture:** Reuse the existing Avalonia Presentation views and transient MVVM view models. Get Started will expose explicit readiness copy, while WizardViewModel will make validation and prerequisite checks authoritative before invoking the existing execution helper. No infrastructure, authentication, or script-builder behavior changes are allowed.

**Tech Stack:** .NET 10, Avalonia 11, CommunityToolkit.Mvvm, xUnit, Moq, Avalonia.Headless, Avalonia.Skia.

**Spec:** `docs/superpowers/specs/2026-08-21-first-run-onboarding-design.md`

## Global Constraints

- Preserve the frozen Graph/PowerShell script builders and auth flow byte-for-byte in behavior and emitted text.
- Use the existing PhoneDesk brand resources, dark/light themes, and FluentIcons conventions.
- Keep command guards authoritative in the ViewModel; UI disabled state is only a presentation of the same rule.
- Write a failing test before each production behavior change.
- Render and inspect all affected first-run/Wizard states before claiming completion.

---

### Task 1: Make Wizard readiness authoritative

**Files:**
- Modify: `PhoneDesk.Tests/WizardViewModelTests.cs`
- Modify: `src/PhoneDesk.Presentation/ViewModels/WizardViewModel.cs`

**Interfaces:**
- Consumes: existing `IValidationService.ValidateVariables(IPhoneManagerVariables)` and `ValidatePrerequisites()`.
- Produces: public bindable `StepNumberText`, `ConfigurationReady`, `PrerequisitesReady`, `ReadinessMessage`, `ValidationSummary`, and guarded `CanGoNext`/`CanExecuteStep` behavior.

- [ ] **Step 1: Write failing tests**

Add tests that prove:

```csharp
[Fact]
public void InvalidConfiguration_CannotCompleteReviewOrAdvance()
{
    var harness = new ViewModelTestHarness();
    var validation = new ValidationResult();
    validation.AddError("Customer name is required.");
    harness.ValidationService
        .Setup(v => v.ValidateVariables(It.IsAny<IPhoneManagerVariables>()))
        .Returns(validation);

    var vm = CreateViewModel(harness);

    Assert.False(vm.ConfigurationReady);
    Assert.False(vm.CanExecuteStep);
    Assert.False(vm.CanGoNext);
    Assert.Contains("Customer name is required", vm.ValidationSummary);
}
```

Add a test that sets a valid variable result but an invalid prerequisite result, advances only through test setup as needed, and proves provisioning cannot execute or advance. Add a valid-readiness test proving `StepNumberText` is `Step 1 of 10`, review can complete, and an execution step remains allowed only when both validations are valid. Add a test that invokes the review command while invalid and proves no step completion occurs.

Also prove that `Next` cannot jump over an uncompleted provisioning step and that the final summary refuses to claim setup complete after a provisioning step was skipped.

- [ ] **Step 2: Run the focused tests and verify they fail for the missing readiness behavior**

Run:

```powershell
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --filter FullyQualifiedName~WizardViewModelTests --no-restore
```

Expected: compile or assertion failures for the new properties/guards, not an infrastructure restore failure.

- [ ] **Step 3: Implement the minimal readiness model**

In `WizardViewModel`:

- Add computed bindable-facing properties for `StepNumberText`, `ConfigurationReady`, `PrerequisitesReady`, `ReadinessMessage`, and `ValidationSummary`.
- Re-evaluate the two existing validation services when the current step is refreshed and before review/provisioning actions.
- Let step zero review require valid variables; let steps one through eight require both valid variables and valid prerequisites; keep the final summary non-mutating.
- Do not allow `Next` to jump over an uncompleted step. A skipped step remains visible as skipped, and the final summary must refuse to report successful setup when any provisioning step was skipped.
- When a guard blocks an action, set `StatusMessage` to a plain next action and leave the current step unchanged.
- Keep all existing script-building calls and `PreviewAndExecuteAsync` behavior unchanged.
- Notify command can-execute state and computed properties whenever the step changes or a guard re-evaluates.
- Replace box-drawing separators in the review string with ASCII separators and retain all existing field values.

- [ ] **Step 4: Run focused and full unit tests**

Run the focused filter first, then:

```powershell
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --verbosity minimal
```

Expected: zero failures. If existing tests assert old unguarded behavior, update only those assertions to the approved safety contract and keep their original intent.

- [ ] **Step 5: Commit the readiness change**

```powershell
git add PhoneDesk.Tests/WizardViewModelTests.cs src/PhoneDesk.Presentation/ViewModels/WizardViewModel.cs
git commit -m "feat: guard setup wizard progression"
```

### Task 2: Give Get Started explicit readiness and recovery copy

**Files:**
- Modify: `PhoneDesk.Tests/GetStartedViewModelTests.cs`
- Modify: `src/PhoneDesk.Presentation/ViewModels/GetStartedViewModel.cs`
- Modify: `src/PhoneDesk.Presentation/Views/GetStartedView.axaml`

**Interfaces:**
- Consumes: existing module, Teams, Graph commands and session state.
- Produces: bindable step-state labels and a concise inline `SetupGuidance`/`SetupGuidanceTone` message that explains the next action without changing connection behavior.

- [ ] **Step 1: Write failing tests**

Add tests proving the initial guidance says to check modules first, a successful module check says Teams can be connected, a failed check tells the operator to review the module result and retry, and `CanProceed` exposes a ready-to-configure message only after all three checks pass.

- [ ] **Step 2: Run the focused tests and verify they fail**

```powershell
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --filter FullyQualifiedName~GetStartedViewModelTests --no-restore
```

- [ ] **Step 3: Implement the minimal copy/state changes**

Add computed status text and a concise guidance property. Set it at construction and after each module/connection attempt, including exception and authentication-failure paths. Do not alter command text, MSAL calls, token handling, or session updates.

Update the view with a small “Before you start” orientation line, inline status text under the relevant card, explicit disabled button labels such as “Check modules first,” and a ready card that leads to configuration. Keep the detailed global log available.

- [ ] **Step 4: Run focused and full tests**

```powershell
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --verbosity minimal
```

- [ ] **Step 5: Commit the Get Started change**

```powershell
git add PhoneDesk.Tests/GetStartedViewModelTests.cs src/PhoneDesk.Presentation/ViewModels/GetStartedViewModel.cs src/PhoneDesk.Presentation/Views/GetStartedView.axaml
git commit -m "feat: explain setup readiness"
```

### Task 3: Reframe Welcome and Wizard visual hierarchy

**Files:**
- Modify: `src/PhoneDesk.Presentation/Views/WelcomeView.axaml`
- Modify: `src/PhoneDesk.Presentation/Views/WizardView.axaml`
- Modify: `src/PhoneDesk.Presentation/ViewModels/WizardViewModel.cs`

**Interfaces:**
- Consumes: Task 1’s readiness properties and existing `Steps`, `Plan`, progress, and result state.
- Produces: readable first-run copy, a non-code review surface, a clear step counter, local safety guidance, and correctly scoped configuration navigation.

- [ ] **Step 1: Add headless acceptance assertions for the new labels/state**

Extend the screenshot harness’s shot list and/or view-level test helpers so the rendered states contain the approved labels: `Start setup`, `No PowerShell knowledge required`, `Step 1 of 10`, `Complete the required configuration`, and `Preview only — no tenant changes`.

- [ ] **Step 2: Run the focused screenshot test before implementation**

```powershell
$env:GENERATE_SCREENSHOTS = "1"
$env:SCREENSHOT_FRAMELESS = "1"
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --filter FullyQualifiedName~ScreenshotGenerator --no-restore
```

Expected: the new assertions or expected labels fail against the current views.

- [ ] **Step 3: Implement the visual changes**

Welcome:

- Change the hero copy to describe the operator’s journey and make the primary action “Start setup.”
- Add three compact reassurance cues using existing card/badge resources.
- Keep documentation secondary and avoid a modal tour.

Wizard:

- Bind the header to `StepNumberText` and add the readiness message near the action area.
- Render step zero as a wrapped summary using regular UI text rather than a code editor; keep script preview for execution steps.
- Add a visible safe-preview label beside Dry-Run Plan.
- Show Edit configuration only when `CurrentStep == 0` via a dedicated `IsReviewStep` property.
- Keep the existing busy overlay, progress, cancel, retry, skip, and export commands.
- Use plain ASCII review separators and readable missing-value placeholders.

- [ ] **Step 4: Render affected screenshots**

Run:

```powershell
$env:GENERATE_SCREENSHOTS = "1"
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --filter FullyQualifiedName~ScreenshotGenerator --no-restore
```

Inspect `docs/screenshots/welcome.png`, `docs/screenshots/get-started.png`, and `docs/screenshots/setup-wizard.png` with the image viewer. Check for clipping, overflow, empty-state clarity, button hierarchy, and garbled text.

- [ ] **Step 5: Commit the visual change**

```powershell
git add src/PhoneDesk.Presentation/Views/WelcomeView.axaml src/PhoneDesk.Presentation/Views/WizardView.axaml src/PhoneDesk.Presentation/ViewModels/WizardViewModel.cs PhoneDesk.Tests/ScreenshotGenerator.cs docs/screenshots/welcome.png docs/screenshots/get-started.png docs/screenshots/setup-wizard.png
git commit -m "feat: guide first-time setup visually"
```

### Task 4: Verify the complete first-run journey and handoff

**Files:**
- Modify: `AGENTS.md`
- Modify: `docs/superpowers/specs/2026-08-21-first-run-onboarding-design.md` only if verification discovers a necessary wording correction.

- [ ] **Step 1: Run the complete test suite and build**

```powershell
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --no-restore --verbosity minimal
dotnet build PhoneDesk.slnx --no-restore --configuration Release --verbosity minimal
```

- [ ] **Step 2: Run the real desktop smoke walkthrough**

Launch the built app, inspect Welcome, Start setup, Get Started, Setup Wizard, and Variables using the Windows desktop viewer. Do not sign in, run module installation, execute provisioning, or transmit tenant data. Capture the sign-in boundary as a remaining manual step.

- [ ] **Step 3: Inspect final screenshots**

Verify the six required states from the spec and retain only rendered assets that pass the existing screenshot validator.

- [ ] **Step 4: Append durable working notes**

Add a dated note under `AGENTS.md` Working notes describing the readiness guard, the no-sign-in verification boundary, screenshot command, and any remaining known onboarding gap.

- [ ] **Step 5: Commit notes, bump version, and verify branch state**

Use the repository’s required version-bump script for a patch release, run the version consistency checks, commit the notes and bump, and confirm the branch has no unintended files before pushing.
