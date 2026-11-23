# Accepted Avalonia compatibility limit

Operator decision: 2026-10-12. Tracking record:
https://github.com/realgarit/phonedesk/issues/203

Avalonia 11 is the supported application stack. PackageReference ranges have an
exclusive 12.0.0 upper bound, so a transitive dependency cannot silently select
Avalonia 12. Dependabot excludes the incompatible framework and theme versions;
compatible patches remain eligible for review.

The proposed icons 1.3.316 targets pre-0.11 Avalonia, while icons 2.1.344 requires Avalonia 12. Keep icons exactly 1.1.223 and FluentAvaloniaUI below 3. The Avalonia 12 proposal also fails on removed clipboard and obsolete placeholder APIs.

Reopen the record for a deliberate migration that aligns the framework, theme,
icons and test packages, replaces removed APIs, passes clean builds and tests,
and verifies representative native UI behavior. Remove the limits together in
that migration PR. This closes the queue item as an accepted limit rather than
claiming Avalonia 12 support.

## What enforces the limit

Three things, and the last one is the one that holds:

1. The PackageReference ranges with the exclusive 12.0.0 upper bound.
2. The `ignore` entries in `.github/dependabot.yml`. They cover every project,
   because the single `nuget` entry with `directory: /` discovers all of them.
3. `PhoneDesk.Tests/DependencyLimitTests.cs`. It reads every project file and
   fails when an Avalonia package leaves `[11.x.y,12.0.0)`, when the Avalonia
   packages are not all on one version, or when the theme or icon package
   leaves its pinned line.

The test exists because of PR 243 (2026-11-23). The Dependabot job had loaded
all three ignore conditions and picked 11.3.23 as the target. While it applied
the Avalonia.Skia update to the presentation project, its NuGet updater logged
"Requested dependency resolution to include Avalonia/11.3.23 but it was instead
resolved to 12.0.0" and wrote Avalonia 12.0.0 and FluentIcons.Avalonia 2.1.345
into the project files. So the ignore rules were right and applied, and the
updater's own dependency resolution stepped past them. A Dependabot PR that
does this again now fails the test with the project and package named.
