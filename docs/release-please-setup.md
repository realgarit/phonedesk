# Release Please Configuration

This project uses [release-please](https://github.com/googleapis/release-please) (via the
`googleapis/release-please-action` GitHub Action) to automate versioning and release
management. Every push to `main` triggers release-please to evaluate Conventional Commit
messages since the last release and propose a release PR.

## Version pinning

The workflow pins `googleapis/release-please-action` to the v5.0.0 commit SHA:

```yaml
uses: googleapis/release-please-action@45996ed1f6d02564a971a2fa1b5860e934307cf7 # v5.0.0
```

The explicit SHA prevents an action-tag update from changing release behavior
unexpectedly. The root-level `last-release-sha` in `release-please-config.json` anchors
release-please after the v3.21.5 release commit so old breaking-change footers cannot
recreate the retired phantom v4.0.0 release.

## Commit types and one-time recovery

Release-please treats user-facing Conventional Commits such as `fix`, `feat`, and
`deps` as releaseable. Routine `chore` commits are not releaseable. The scheduled module
compatibility workflow therefore generates:

```
fix(deps): bump bundled PowerShell module versions
```

For a one-time recovery, a commit body may contain the exact footer:

```
Release-As: 3.24.2
```

Use that footer only to recover a known pending release. Do not add a persistent
`release-as` override to `release-please-config.json`; it would lock normal version
calculation.

## Application version sources

The application-facing sources must agree on the same three-part version:

- `version.txt`
- `phonedesk.csproj` `<Version>`
- `app.manifest`'s four-part `assemblyIdentity` version
- `src/PhoneDesk.Domain/ConstantsService.cs`

`phonedesk.csproj` keeps four-part `<AssemblyVersion>` and `<FileVersion>` values for
Windows compatibility. Run `bash scripts/validate-version-sync.sh` to check these
sources. Both `scripts/bump-version.sh` and `scripts/bump-version.ps1` update
`version.txt` along with the other application sources.

`.release-please-manifest.json` is release-please-managed state. It may trail the
application sources while a release is waiting to be proposed, but
`validate-release` requires it to match the release tag.

The configured `simple` release type manages `version.txt` in the release PR. The
additional `extra-files` entries update the project, manifest, and display-version
sources listed above; the version-sync guard checks all of them before packaging.

## Key files

| File | Purpose |
|------|---------|
| `.github/workflows/build.yml` | CI/CD pipeline: release-please, validation, build, publish, Homebrew |
| `.github/workflows/publish-store-package.yml` | Published-release workflow for the direct Microsoft Store installer URL on `gh-pages` |
| `.github/workflows/module-compatibility.yml` | Weekly module pin check and releaseable dependency PR |
| `release-please-config.json` | Release-please config: release type, anchor, extra files, draft mode |
| `.release-please-manifest.json` | Release-please-managed package version |
| `version.txt` | Application version source |
| `scripts/validate-version-sync.sh` | Cross-platform application version-source guard |
| `phonedesk.csproj` | Project `<Version>`, `<AssemblyVersion>`, `<FileVersion>` |

## What happens on a push to `main`

1. **`release-please` job**: release-please evaluates commits after
   `last-release-sha` and creates or updates a release PR
   (branch: `release-please--branches--main`).
2. **PR validation**: the `pr-validate` job checks version-source alignment, then
   builds and tests the PR across Windows, macOS Intel, macOS ARM, and Linux.
3. **When the release PR is merged**: release-please creates the Git tag and draft
   GitHub release, then `build` → `upload-release-assets` → `bump-homebrew-cask` runs.
4. **When the GitHub release is published**: `publish-store-package.yml` copies the
   Windows installer to `gh-pages/store/<version>/` and verifies the public URL. The
   workflow can also be dispatched manually with an existing tag to recover a failed
   Pages publication.

## Troubleshooting

### Release-please does not create a release PR

- Check that the merged change uses a releaseable type such as `fix`, `feat`, or
  `fix(deps)`, rather than an ordinary `chore`.
- For a known pending version, use a one-time `Release-As: X.Y.Z` footer.
- Verify `release-please-config.json` has the root-level `last-release-sha` pointing to
  the intended release anchor.
- Delete stale release-please branches (`release-please--branches--main-*`) only after
  inspecting their PR and branch state.

### Validate-release fails with a version mismatch

- Ensure the release tag matches `vX.Y.Z` format with no pre-release suffix or fourth
  digit.
- Run `bash scripts/validate-version-sync.sh X.Y.Z` locally.
- Check that `.release-please-manifest.json` contains the same three-part version as the
  release tag.

### Windows builds fail on version mismatch

- `<AssemblyVersion>` and `<FileVersion>` must be four-part values such as `3.21.3.0`.
- `<Version>` remains three-part and must match the release tag exactly.
