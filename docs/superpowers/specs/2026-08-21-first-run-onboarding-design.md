# First-run onboarding and Setup Wizard design

## Goal

Make PhoneDesk understandable and safe for a normal operator who has never worked with Teams Phone before, while preserving the existing provisioning behavior and allowing the owner to test the complete journey without a real tenant sign-in.

## Audience and product job

PhoneDesk is a calm control tower for a consultant or customer operator who needs to prepare one Teams Phone project. The user should always know what PhoneDesk is waiting for, what will happen next, and whether an action is only a preview or will change the tenant.

## Evidence from the first-user walkthrough

- Welcome currently says “Configure and deploy Microsoft Teams Phone infrastructure” but does not tell a new operator how to begin or what knowledge is required.
- Get Started presents disabled Teams and Graph buttons without showing that module verification is the dependency or explaining that Graph opens a browser sign-in.
- Setup Wizard opens at step `0` with an empty configuration preview, computed names such as `ttgrp--`, and garbled Unicode separator characters.
- The Wizard can complete its review step and advance before variables or connection prerequisites are valid; its execution command is not guarded by a current variable/prerequisite validation.
- The Wizard shows “Edit Variables” on every step, and failure guidance is primarily surfaced through the global log.

## Approved experience

### Welcome: orient the operator

Use plain language from the operator’s perspective. The primary action is “Start setup”; the supporting copy explains that PhoneDesk checks the local tools, guides configuration, previews changes, and then performs the setup one step at a time. Keep documentation secondary.

Add three quiet reassurance cues:

1. No PowerShell knowledge required.
2. Preview the plan before making changes.
3. Pause and return between steps.

The current PhoneDesk brand, dark/light themes, iconography, and palette remain the visual foundation.

### Get Started: a readiness runway

Keep the three existing setup steps, but make each one answer three questions: what this does, why it is needed, and what the operator should do now.

- Module check: say that PhoneDesk uses bundled PowerShell tooling and will verify the required modules. Surface a concise inline result and a retry action when verification fails.
- Teams connection: explain that this connects to the target tenant and is enabled only after modules are ready.
- Graph connection: explain that a Microsoft sign-in window opens, and that this is required for groups and resource accounts.

Expose a short readiness message such as “Check modules first” or “Ready for configuration” instead of leaving the operator to infer why a button is disabled. Existing detailed logs remain available for troubleshooting.

### Setup Wizard: safe progression

The Wizard remains a ten-step flow, but its state becomes explicit:

- Show `Step 1 of 10` in the header.
- Treat step 1 as a readable configuration review, not a raw script preview. Show the customer, group, tenant domain, language/time zone, phone number, and generated names in a wrapped summary. Use plain ASCII separators so screenshots and copied text remain reliable.
- Require valid variables before the review step can be completed.
- Require module, Teams, and Graph prerequisites before provisioning steps can execute or the operator can advance from review.
- Do not let `Next` jump over an uncompleted provisioning step; the operator must complete or explicitly skip each step. The final summary must not claim setup succeeded when required steps were skipped.
- Keep dry-run plan generation available as a safe, read-only action and explain that it makes no tenant changes.
- Keep execution previews for provisioning steps, but make the safety boundary visible in the action area.
- Show “Edit configuration” only on the review step.
- Preserve retry, skip, cancellation, and progress behavior, but give failure states a local explanation and next action.

The command guard is authoritative even if a button is invoked through automation or keyboard access: it must revalidate the current variables and prerequisites before any provisioning command is sent to the existing execution path.

## Visual direction

- Palette: existing midnight background, raised indigo surfaces, PhoneDesk purple accent, success green, warning amber, and critical red.
- Typography: existing Inter/body and display font resources; sentence case and active verbs.
- Layout: a clear “next action” strip at the bottom of setup pages, with status information close to the step it explains.
- Signature: a quiet readiness ribbon that changes from “Complete these checks” to “Ready to configure” rather than interrupting the operator with an onboarding modal.

## Scope boundaries

- Do not change Graph/PowerShell script builder output.
- Do not change MSAL authentication behavior or sign-in automation.
- Do not add a demo tenant or simulated provisioning mode in this pass.
- Do not hide existing advanced navigation; instead, make the primary path obvious and make operation-level validation authoritative.

## Verification requirements

- Unit tests cover disabled progression with invalid variables, disabled provisioning without prerequisites, valid readiness, and failed-step recovery.
- Unit tests cover Get Started’s user-facing readiness/error text without weakening existing authentication behavior.
- Headless screenshots cover Welcome, Get Started initial state, Get Started ready state, Wizard invalid review, Wizard ready review, and a failed execution state.
- The resulting PNGs are visually inspected for clipping, overflow, garbled text, readable hierarchy, and clear primary actions.
- A real desktop walkthrough is repeated without signing in or executing a tenant mutation; the remaining sign-in-only boundary is reported.
