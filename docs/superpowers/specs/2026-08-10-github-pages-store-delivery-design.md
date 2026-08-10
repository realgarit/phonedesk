# GitHub Pages Store Package Delivery Design

## Problem

The Microsoft Store requires a direct, versioned installer URL that does not redirect. The `gh-pages` branch currently contains the manually copied installer for 3.24.0 but no 3.24.2 package, while the normal release workflow publishes only GitHub release assets and the Homebrew cask.

## Decision

Add a dedicated `.github/workflows/publish-store-package.yml` workflow triggered by `release: published`, with a `workflow_dispatch` tag input for recovery. Keeping this path independent from the release-please job means a failed Pages push can be rerun without release-please needing to create a second release. The workflow will:

1. Derive `VERSION` and `TAG` from the existing `validate-release` outputs.
2. Check out `gh-pages` into a separate job directory with contents write access.
3. Download the canonical `phonedesk-win-x64-setup.exe` from the published GitHub release.
4. Compare its SHA-256 digest with the release asset metadata before staging it.
5. Copy it to `store/<version>/phonedesk-win-x64-setup.exe`, preserving all existing listing assets and older version paths.
6. Commit only when the target content changes and push explicitly to `gh-pages`.
7. Poll the public versioned URL and compare the served bytes to the release digest, failing the workflow if Pages serves a redirect, an error, or different content.

The workflow is idempotent for reruns of the same release and version-driven for future releases. It uses the existing `PAT_TOKEN` fallback pattern, with `GITHUB_TOKEN` as the default same-repository credential, and does not print secret values. The manual tag input provides a controlled recovery path for an already-published release such as 3.24.2.

## Error handling and safety

- The release-triggered job runs only after GitHub has published a release; the manual path accepts an explicit existing release tag.
- A missing asset, malformed tag/version, digest mismatch, failed `gh-pages` push, redirect, or stale public content fails the job.
- Public Pages propagation is handled by bounded polling rather than a fixed one-shot request.
- No files are deleted from `gh-pages`; existing versions and listing assets remain available.

## Verification

- Validate the workflow syntax and shell script syntax locally.
- Run the existing test suite and version-sync guard.
- Dispatch the recovery path for the existing v3.24.2 release after the workflow is merged, then verify the new live URL and `gh-pages` commit.
