# phonedesk — Agent instructions

> Canonical instructions for all coding agents (Claude Code, Codex, GitHub Copilot). Claude loads this via the CLAUDE.md stub.

## Architecture (Clean Architecture)

4 layers under `src/`:
- `PhoneDesk.Domain` — framework-free (rules, value objects, constants, holiday computus, `LogLevel`, `IPhoneManagerVariables`/`IHolidayEntry`/`IDaySchedule` contracts)
- `PhoneDesk.Application` — ports (interfaces) + use cases (`ValidationService`)
- `PhoneDesk.Infrastructure` — PowerShell/MSAL adapters (only layer with `System.Management.Automation` and `Microsoft.Identity.Client`)
- `PhoneDesk.Presentation` — Avalonia/MVVM UI (depends only on Domain + Application)
- `phonedesk` (repo root exe) — composition root (DI, app.manifest, version, Modules content)

**Dependency Rule**: enforced by `DependencyRuleTests` — forbidden inward deps fail the build.
**ObservableObject models** = Presentation concern. **VM-first ViewLocator** (no service locator).

## Frozen / Do Not Touch

### Graph/PowerShell script builders & auth
The `ScriptBuilders` in `src/PhoneDesk.Infrastructure/ScriptBuilders/` and the auth flow (`MsalGraphAuthenticationService`, `PowerShellContextService`) are **off-limits** — do NOT change behavior or emitted text without asking. Structural changes (relocating, interfaces) OK if output stays byte-identical.

### macOS signing cert
The self-signed cert "Teams Phone Manager Self-Signed" (stable designated requirement `identifier "ch.realgar.teams-phonemanager" and certificate leaf = H"965282c9..."`) keeps MSAL keychain items / TCC grants across upgrades. **NEVER regenerate** — p12 at `~/.phonedesk-signing/signing.p12`, also in repo secrets.

## UI / Icons

At 16px, dense FluentIcons glyphs (e.g. `PeopleTeam`, 3 figures) look thicker than sparse outlines. **Prefer simple outline glyphs** (1-2 subjects) for nav/buttons. Filled icons are OK on tinted badge circles (`.icon-badge` style), empty states, brand marks — solid glyphs can't have stroke-weight mismatches.

**Brand**: T-Pad monogram (dial-pad dots, "T" at full white, side dots 38% opacity, diagonal gradient **#7B80EE → #45478F** + top sheen). Canonical source `assets/icon.svg`; all derived icons regenerate via `assets/generate-icons.py`; `BrandGradientBrush` in App.axaml.

README screenshots at `docs/screenshots/` (2560×1496 = 1280×720 @2x + synthetic macOS title bar) regenerate via `ScreenshotGenerator.cs` (xunit `[Fact]`, only runs with `GENERATE_SCREENSHOTS=1`, uses Avalonia.Headless + real Skia, stubs services, validates frames before overwriting).

## Release Workflow

- **Release-please-action**: Must use **v5** (SHA: `45996ed1f6d02564a971a2fa1b5860e934307cf7`), NOT v4. v4 is deprecated (Node 20) and causes issues.
- **Config file**: `release-please-config.json`
- **Manifest file**: `.release-please-manifest.json`
- **Workflow file**: `.github/workflows/build.yml`
- **Version source**: `phonedesk.csproj` `<Version>` — also in `app.manifest` and `ConstantsService.cs`. Bump via `Scripts/bump-version.sh minor|patch|major`.

### Phantom v4.0.0 PR — root fix & prevention

Release-please repeatedly created v4.0.0 PRs because old squashed commit `0406e3d` contained `BREAKING CHANGE: ViewModel constructors now require ISharedStateService and IDialogService parameters`.

**Permanent fix**: `last-release-sha` in `release-please-config.json` anchors release-please to only scan commits after the specified SHA. The value points to the v3.21.5 release commit — any BREAKING CHANGE footers before it are ignored.

**Do NOT reintroduce `BREAKING CHANGE:` footers unless you actually intend a major bump.**

### macOS signing & Homebrew delivery

- Sign publish output BEFORE .app assembly (`Scripts/package-macos.sh`); bundle is never sealed (codesign rejects .NET managed-dll layout as nested code).
- Homebrew cask (`realgarit/homebrew-tap`) needs `postflight` stripping `com.apple.quarantine` — Homebrew quarantines cask downloads and Gatekeeper SIGKILLs (exit 137) quarantined ad-hoc/self-signed binaries.
- `bump-homebrew-cask` job pushes via `PAT_TOKEN`.

## CI

- Build runs on Windows, macOS (x64 + arm64), Linux
- Windows: Inno Setup installer; macOS: ad-hoc signed .app bundle; Linux: zip
- **PR checkout**: `github.event.pull_request.head.sha` (not merge ref) to avoid race where merge ref is deleted before runner picks up the job
- `dotnet test` skips screenshot gen unless `GENERATE_SCREENSHOTS=1`

## Workflow Convention (PR workflow)

Always: **branch → commit → push + open PR → CI green → merge (merge commit, not squash) → clean up ALL branches** (remote delete, local branches, stale worktrees). Always end a working session with a version bump (the bump IS the release trigger). Do NOT create tags manually — build.yml auto-creates them.

## Project memory (distilled)

<!-- Curated snapshot of prior agent session knowledge (2026-07-17). Claude's private memory remains canonical; update via Working notes. -->

- **Visual verification required for UI changes**: never merge new/changed pages, dialogs, or panels on unit tests alone. Render every new/affected page in its key states (empty, populated, long text, filters open) via the headless harness (`ScreenshotGenerator.cs` pattern, `GENERATE_SCREENSHOTS=1`), then actually inspect the resulting PNGs for overflow, clipping, alignment, and centering before opening the PR. This was learned the hard way — UI shipped with unit tests only has twice gone out with text overflow / mis-centered layouts (History page, Dashboard, dry-run preview).
- Icon and screenshot/brand conventions, the Clean Architecture layering, the frozen Graph/PowerShell + auth surface, the macOS signing/delivery pipeline, and the branch→PR→CI→merge→cleanup workflow are covered above under their own sections — treat those as the durable ground truth alongside this one.
- Homebrew cask auto-bump (`PAT_TOKEN` → `realgarit/homebrew-tap`) has tap write access and is confirmed working end-to-end; no manual cask bumps needed.

## Cross-agent conventions

- This file (`AGENTS.md`) is the single source of truth for agent instructions in this repo. `CLAUDE.md` and `.github/copilot-instructions.md` are pointers to it — never edit them, never duplicate content into them.
- Reusable skills live in `.claude/skills/` (one folder per skill with a `SKILL.md`). GitHub Copilot reads that directory natively; Codex sees it via the `.agents/skills` symlink. New skills always go in `.claude/skills/`.
- Claude-specific subagent definitions live in `.claude/agents/`. If you are not Claude Code, you may read them as role/process guidance.
- Session continuity across tools: before ending substantial work in ANY tool (Claude Code, Codex, Copilot), record durable context — decisions made, gotchas discovered, in-progress state worth resuming — in the "Working notes" section below, or fold it into the relevant section above. This is the shared memory between agents.

## Working notes

<!-- Any agent: append short dated notes here (YYYY-MM-DD — note). Prune notes when stale or once folded into the sections above. -->

- 2026-07-21 — **Rebranded to PhoneDesk** (Store name "PhoneDesk") after MS Store cert failure 10.1.1.1 (name led with "Teams" trademark). Repo is now `realgarit/phonedesk` (git/release URLs redirect; GitHub Pages URLs did NOT — Store package URL updated). FROZEN despite rebrand: macOS bundle id `ch.realgar.teams-phonemanager`, signing identity `Teams Phone Manager Self-Signed` (cert CN), Inno `AppId` GUID, ScriptBuilders output. **Legacy updater bridge**: pre-rebrand in-app updaters only accept a release asset named exactly `teams-phonemanager-win-x64-setup.exe` — CI attaches the installer under both names + both SHA256SUMS entries; keep until old installs are negligible.
- 2026-07-21 — **Microsoft Store**: EXE-app product `53a8f446-bbf3-4c01-9156-3ef5b44aaf57` in Partner Center. Package URLs must not redirect → installers copied to `gh-pages` branch `store/<version>/`, served from `https://realgarit.github.io/phonedesk/...`; listing screenshots generated via `GENERATE_SCREENSHOTS=1 SCREENSHOT_FRAMELESS=1`. Store's automated silent-install/ARP checks can't see per-user (HKCU) installs — those warnings are expected; verify manually. Pending: automate gh-pages copy + Store submission API in build.yml; code-signing cert (Azure Trusted/Artifact Signing unavailable to Swiss individuals — classic CA needed, SSL.com eSigner favored); Kaspersky Allowlist submission after signing.
- 2026-07-21 — Open PR #148 (module-compatibility bot) predates the rebrand and needs a rebase onto renamed paths before merge.
- 2026-07-23 — MS Store cert 10.1.1.1 also rejected the "PhoneDesk for Microsoft Teams" Store-name form: the Store product-name field may not contain any Microsoft product name at all. Listing changed to bare "PhoneDesk" and resubmitted; "for Microsoft Teams" remains only in the Store description/keywords and in-app branding.
- 2026-08-10 — **Bundled-module update #161 and version bump #162 merged**: `scripts/module-versions.json` now pins the five Microsoft.Graph modules to `2.39.0`; app version sources are `3.24.2`. Both PRs passed the full platform/review gates and the local suite ran 541 tests. Live release verification found no `v3.24.2`: release-please ignored the merged `chore:` commits as non-user-facing, and `version.txt` remains `3.24.0`; repair the release trigger/version-file alignment before claiming delivery.
- 2026-08-10 — **GitHub Pages Store delivery**: the dedicated `publish-store-package.yml` workflow reacts to published releases and has a manual tag recovery path. It verifies the GitHub release installer digest, writes only `store/<version>/phonedesk-win-x64-setup.exe` on `gh-pages`, and polls the public non-redirecting URL for matching bytes. This closes the gap found after v3.24.2 and preserves older Store package paths/listing assets.
- 2026-08-21 — **First-run onboarding hardening**: a non-specialist simulation found generic Welcome/Get Started copy, disabled connection actions without reasons, raw Wizard review output, and unsafe-looking step progression. Welcome, Get Started, Configuration, and Setup Wizard now use operator-first guidance; Wizard progression requires valid configuration and connection readiness, cannot jump over incomplete steps, refuses a false success after skipped provisioning, and exposes Retry as the only execution path after a failure. Headless screenshots cover empty, ready, and failed states and were visually inspected; a live desktop smoke run stopped before module installation, Teams sign-in, Graph sign-in, or tenant mutation. Graph/PowerShell/auth behavior and emitted scripts remain frozen. Release version is 3.24.3; a real customer pilot is still needed for module installation and both sign-in paths.
- 2026-08-21 — **v3.25.0 release recovery**: release validation caught that the valid, separately annotated `app.manifest` version was not updated by release-please's generic updater. The config now uses a targeted XML XPath plus a regression test. Build and Release has a guarded `release_tag` dispatch path to rebuild and publish an existing tagged draft after a downstream failure. Recovery published `v3.25.0` with all platform assets, the legacy Windows updater asset, Homebrew cask update, and verified non-redirecting Store package; the real pilot boundary remains module installation and both sign-in paths.
- 2026-08-22 — **German localization and guard**: UI text now uses typed English/German catalogs with English as the default and a Settings toggle. The catalogs cover the full presentation/runtime surface with ASD-STE100-oriented operator wording; commands, identifiers, and native technical errors remain unchanged. `PhoneDesk.LocalizationGuard` runs in the presentation build and CI, and an opt-in pre-commit hook is available. Bilingual headless screenshots were regenerated and visually inspected, including the corrected German header wrapping; the real pilot boundary remains module installation and both sign-in paths.
- 2026-08-22 — **Localization guard/report review**: the guard also scans implicit XAML content, direct C# UI sinks, file-picker labels, and `StringBuilder` report output; it rejects partial staging in the hook and validates placeholder multiplicity plus trailing JSON. The documentation report is catalog-backed in both languages. Microsoft Teams terminology review corrected the German report to use `Klingelzeit für Agenten`, full resource-account labels, and `Anrufwege-Struktur`; the frozen technical command output is unchanged.
- 2026-08-22 — **Final terminology pass**: the translator review also replaced ambiguous `Routing`/timeout wording, clarified weekly business hours and the session-expiry message, and aligned documentation copy with `Anrufweiterleitung` and `Anrufwege-Struktur`. The final translator re-review returned PASS.
- 2026-08-22 — **Catalog completion audit**: the final translator pass also replaced remaining user-visible `Update` wording with `Aktualisierung`, clarified guided-setup titles and the History actor column, and confirmed PASS. The Wizard's generated technical script preview remains English and byte-stable by design; surrounding dialog and status text are localized.
- 2026-08-22 — **Case-sensitive CI fix**: `LocalizationServiceTests` must locate the solution as `PhoneDesk.slnx` with exact casing; Windows masked the previous lowercase path, while Linux CI correctly exposed it. Keep repository-root discovery case-correct for cross-platform tests.
- 2026-08-22 — **Review follow-up**: localization placeholder rendering now substitutes from the original template in one pass, so user values containing `{...}` are not reprocessed. Main-window navigation disposes transient page ViewModels, including Dashboard/Wizard/Get Started translation subscriptions; holiday preview contexts use localized catalog text while retaining the existing operation flow.
- 2026-08-22 — **Technical preview preservation**: licensing script comments keep their resource-specific English technical labels through a catalog placeholder (`call queue resource account` / `automatic attendant resource account`), including when German is selected. Catalog language switches use only aggregate indexer notifications; the focused regression suite verifies both behaviors.
- 2026-08-22 — **Toggle-state completion**: Avalonia's default `On`/`Off` labels in Settings are explicitly catalog-backed as `Ein`/`Aus` for German and `On`/`Off` for English; the final Settings screenshot was regenerated and visually inspected.
- 2026-08-22 — **Release manifest preservation**: release-please's XML updater writes a three-part SemVer value and must not manage `app.manifest`; the Windows four-part `assemblyIdentity` version remains maintained by the bump scripts and protected by `ReleaseConfigurationTests` plus `validate-version-sync.sh`.
- 2026-08-22 — **Compact Wizard readiness banner**: the invalid setup state now shows a two-line readiness ribbon with a typed issue count; full validation issues are collapsed by default and available in a bounded scroll area. English and German invalid/expanded/ready screenshots were rendered and visually inspected.
- 2026-08-22 — **Review-contract hardening**: the AI review runner persists the provider exit code and rejects session-limit, runtime-failure, non-exact-clean, and finding output as non-clean. Semgrep scanner errors or missing output now fail instead of becoming an empty result; both validator boundaries have regression tests.
- 2026-08-22 — **Validation message localization**: validation results now carry stable `ValidationErrorCode` values alongside legacy English messages. Presentation validation dialogs and Wizard readiness details map those codes through the English/German catalog, including re-localization after a language switch.
- 2026-08-22 — **Independent review follow-up**: the post-change audit found and fixed weak Semgrep JSON-shape/error validation, provider stderr session-limit detection, changed-path argument quoting, and missing German expanded screenshot coverage. Main remains unprotected on GitHub; the workflow fails inconclusive reviews but branch protection was not changed.
- 2026-08-30 — **Issue #76 PowerShell integration harness**: real-runspace coverage lives in `PhoneDesk.IntegrationTests`, whose binary stub cmdlets exercise output/error marshaling, production marker/topology parsing, environment cleanup, cancellation, progress, and semaphore serialization without tenant access. MSAL orchestration is tested through the internal `IMsalPublicClient` adapter; client ID, scopes, authority, redirect URI, browser behavior, public results, logging, and sign-out behavior remain unchanged. Both test projects run in every platform CI leg, and existing ScriptBuilder text remains byte-identical.
- 2026-08-30 — **Issue #71 configuration portability and drift**: tenant-as-code exports use explicit schema-v1 documents (`phonedesk.configuration` and `phonedesk.topology`) with strict unknown-field/version/kind validation and deterministic ordering. Configuration import is preview-only until Apply and preserves intentionally empty values; topology export/comparison uses only the Dashboard cache and never triggers a tenant query. English/German import and drift states are covered by the headless renderer and were visually inspected. Version is 3.27.0; ScriptBuilders and authentication remain unchanged.

- 2026-09-05 — **Issue #71 continuation and test strategy**: recovered PR #177 in `C:/Git/phonedesk-codex-september` because the original checkout's Git metadata is unreadable. Import failures now retain the validation reason in both catalogs; report snapshot actions require cached topology and explain the Dashboard prerequisite. Report header, actions, status and footer wrap. Pester 5.7.1 tests execute the real release bump script in disposable fixtures and check every version source; CI runs them alongside xUnit. Keep real-runspace application coverage in `PhoneDesk.IntegrationTests`. Local verification: 631 unit tests and 22 integration tests pass; release/PR completion still requires current CI and published-asset verification.

- 2026-09-05 — **Configuration and wizard readability**: General configuration fields now retain their labels after values are entered; compact tab typography keeps the German tab names together. The configuration subtitle wraps within the header, and wizard step names wrap instead of truncating creation, licensing, and association steps to identical prefixes. English/German empty, populated, import-preview, and wizard readiness/failure renders are the visual acceptance evidence. Version sources advance to 3.27.1.
