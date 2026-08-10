# Release Flow Repair Design

## Status

Approved design for the `v3.24.2` recovery and future bundled PowerShell module
updates.

## Context

PR #161 updated five bundled Microsoft Graph PowerShell module pins to `2.39.0`.
PR #162 then aligned the application sources to `3.24.2`. Both pull requests
were merged and passed the platform validation gates, but no `v3.24.2` release
was created.

The release workflow log showed the cause: both merged commits used the
`chore:` type, which release-please correctly treated as non-user-facing. The
application sources now contain `3.24.2`, while `version.txt` and the
release-please manifest still describe `3.24.0`. The repository documentation
also still describes the old release-please v4 pin even though the workflow is
pinned to v5.

## Goals

1. Publish the already merged `3.24.2` application and module update through
   the existing release-please and build pipeline.
2. Ensure future bundled-module compatibility PRs create a patch release
   candidate automatically.
3. Keep the application version files synchronized when either version-bump
   script is used.
4. Fail CI early when application version sources drift.
5. Preserve merge-commit releases, release-please tag creation, and the existing
   multi-platform packaging and Homebrew delivery flow.

## Non-goals

- Replacing release-please or introducing a custom tagging workflow.
- Creating or moving tags manually.
- Changing the bundled PowerShell module contents, Graph script builders, or
  authentication behavior.
- Changing the release cadence or the versioning policy.

## Design

### 1. One-time release recovery

Create a small `fix(release): recover pending 3.24.2 release` commit that:

- changes `version.txt` from `3.24.0` to `3.24.2`, matching the already merged
  application sources; and
- includes the release-please footer `Release-As: 3.24.2`.

The footer is a one-time recovery instruction. It avoids a persistent
`release-as` configuration override and lets release-please create its normal
release pull request. That release pull request will update the manifest and
changelog, then the normal merge-triggered build workflow will create the tag,
publish the draft release, upload all platform assets, and update the Homebrew
cask.

### 2. Releaseable module-update commits

Change `.github/workflows/module-compatibility.yml` so the generated commit
and pull request use:

```text
fix(deps): bump bundled PowerShell module versions
```

`fix(deps)` is a patch-level, user-visible dependency update and is recognized
by release-please. The workflow's branch naming, build gate, changelog links,
and failure issue behavior remain unchanged.

### 3. Synchronize both version-bump scripts

Update `scripts/bump-version.sh` and `scripts/bump-version.ps1` to write the
three-part `NEW_VERSION_SHORT`/`$displayVersion` to `version.txt` in addition to
the existing csproj, manifest, and `ConstantsService` updates. The scripts
remain responsible only for source-file synchronization; release-please remains
responsible for the release manifest, changelog, tag, and release publication.

### 4. Add a reusable source-version guard

Add `scripts/validate-version-sync.sh`, accepting an optional expected
three-part version. It will validate:

- `version.txt`;
- `<Version>`, `<AssemblyVersion>`, and `<FileVersion>` in
  `phonedesk.csproj`;
- the four-part version in `app.manifest`; and
- the display version in `src/PhoneDesk.Domain/ConstantsService.cs`.

The guard will run in the cross-platform `pr-validate` matrix and from
`validate-release` with the release tag's version as the expected value. The
release-only job will continue to validate the release-please manifest
separately, because that file intentionally trails application source changes
until release-please creates its release pull request.

### 5. Correct the release documentation

Update `docs/release-please-setup.md` to document the current v5 SHA, the
releaseable commit convention, the one-time `Release-As` recovery mechanism,
the distinction between application version sources and the release manifest,
and the new validation guard.

## Files affected

- `.github/workflows/module-compatibility.yml`
- `.github/workflows/build.yml`
- `scripts/bump-version.sh`
- `scripts/bump-version.ps1`
- `scripts/validate-version-sync.sh` (new)
- `version.txt`
- `docs/release-please-setup.md`
- `docs/superpowers/specs/2026-08-10-release-flow-repair-design.md` (this
  document)

## Verification

Before publishing the repair pull request:

1. Run the version guard against the repaired tree.
2. Exercise both bump scripts in temporary copies or equivalent non-destructive
   checks and confirm `version.txt` is updated.
3. Run `dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj --configuration
   Release`.
4. Run YAML/shell formatting and `git diff --check` checks.

After publishing:

1. Confirm all four platform validation jobs and automated review pass.
2. Merge the repair pull request with a merge commit.
3. Confirm release-please opens a `3.24.2` release pull request.
4. Merge that release pull request with a merge commit.
5. Confirm the build, asset upload, and Homebrew jobs pass.
6. Verify the published `v3.24.2` tag, release assets, checksums, and release
   version before cleaning local and remote branches.

## Alternatives considered

### One-time recovery only

This would restore `v3.24.2` quickly with a `Release-As` footer, but future
module PRs would remain `chore:` commits and the bump scripts could recreate
the version-file drift. It does not meet the durable repair goal.

### Replace release-please

A custom release workflow could control every detail, but it would duplicate
release-please's manifest, changelog, tag, and release behavior and expand the
change surface around signing and multi-platform publishing. It is unnecessary
for this failure mode.
