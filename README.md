<p align="center">
  <img src="assets/banner.svg" width="800" alt="PhoneDesk"/>
</p>

<p align="center">
  <a href="https://github.com/realgarit/phonedesk/releases/latest"><img src="https://img.shields.io/github/v/release/realgarit/phonedesk?label=release&color=6A6FDD" alt="Latest release"/></a>
  <a href="https://github.com/realgarit/phonedesk/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/realgarit/phonedesk/build.yml?branch=main&label=build" alt="Build status"/></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/realgarit/phonedesk?color=blue" alt="License"/></a>
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-6A6FDD" alt="Platform support"/>
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10"/>
</p>

<p align="center">
  <b>Manage Microsoft Teams telephony — auto attendants, call queues, and phone numbers — from one desktop app.</b>
</p>

PhoneDesk is a self-contained .NET desktop app for administering Microsoft
Teams Phone without writing PowerShell.

## Features

- Auto attendants, call queues, holidays, resource accounts, and Microsoft 365 Groups
- Guided setup, bulk operations, and tenant documentation
- Native Windows, macOS, and Linux builds with dark and light themes

## Screenshots

<p align="center">
  <img src="docs/screenshots/welcome.png" width="800" alt="Welcome screen (dark theme)"/>
</p>

<details>
<summary><b>More screenshots</b> — setup wizard, call queues, auto attendants, holidays, bulk operations, and more</summary>
<br/>

| | |
|:---:|:---:|
| <img src="docs/screenshots/setup-wizard.png" alt="Setup wizard"/> Setup wizard | <img src="docs/screenshots/get-started.png" alt="Get started"/> Get started |
| <img src="docs/screenshots/call-queues.png" alt="Call queues"/> Call queues | <img src="docs/screenshots/auto-attendants.png" alt="Auto attendants"/> Auto attendants |
| <img src="docs/screenshots/holidays.png" alt="Holidays"/> Holidays | <img src="docs/screenshots/bulk-operations.png" alt="Bulk operations"/> Bulk operations |
| <img src="docs/screenshots/m365-groups.png" alt="Microsoft 365 Groups"/> Microsoft 365 Groups | <img src="docs/screenshots/variables.png" alt="Variables"/> Variables |
| <img src="docs/screenshots/documentation.png" alt="Documentation"/> Documentation | <img src="docs/screenshots/welcome-light.png" alt="Welcome screen (light theme)"/> Light theme |

</details>

## Installation

Download the latest build from [GitHub Releases](https://github.com/realgarit/phonedesk/releases/latest).
PowerShell 7 and the required Microsoft modules are included.

### Windows

Run `phonedesk-win-x64-setup.exe`. It supports per-user and all-users
installation and upgrades in place. Until code signing is enabled, Windows may show a
SmartScreen warning.

Portable alternative: download `phonedesk-win-x64.zip`, extract, run
`phonedesk.exe`.

#### Enterprise deployment

For Intune, Configuration Manager, or an RMM:

```powershell
.\phonedesk-win-x64-setup.exe /ALLUSERS /DIR="C:\Program Files\PhoneDesk" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Use `/CURRENTUSER` for per-user deployment and `/DIR="C:\Path"` for a managed install
location. Keep the same scope and path when upgrading.

### macOS

```bash
brew tap realgarit/tap
brew trust realgarit/tap
brew install --cask phonedesk
```

Update with `brew upgrade --cask phonedesk`. Manual Apple Silicon and Intel
downloads are also available on the releases page.

### Linux

Download `phonedesk-linux-x64.zip`, extract, then:

```bash
chmod +x phonedesk
./phonedesk
```

## Updates

The app checks GitHub Releases at startup and shows a non-blocking update indicator.
On Windows, updates can be downloaded, SHA-256 verified, and installed in the app.
macOS and Linux updates continue through Homebrew or GitHub Releases.

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet restore
dotnet build
dotnet test PhoneDesk.Tests/PhoneDesk.Tests.csproj
dotnet run
```

Built with Avalonia, CommunityToolkit.Mvvm, Microsoft.PowerShell.SDK, and MSAL.

### Localization

English is the default language. Users can select German in **Settings**.

When adding user-visible text:

1. Add a key to [`UiTextKey.cs`](src/PhoneDesk.Presentation/Localization/UiTextKey.cs).
2. Add the same key to both [`Strings.en.json`](src/PhoneDesk.Presentation/Resources/Localization/Strings.en.json) and [`Strings.de.json`](src/PhoneDesk.Presentation/Resources/Localization/Strings.de.json).
3. Use `{loc:Translate Key=...}` in AXAML and `GetText` or `LogLocalized` in view models.
4. Keep placeholders identical in both languages. Use clear operator language and preserve technical commands, identifiers, and native error details.

The localization guard runs during the presentation build and in CI. Run it directly with:

```powershell
pwsh -NoProfile -File scripts/check-localization.ps1
```

An optional pre-commit hook checks affected localization files before commit. Install it explicitly with
`pwsh -NoProfile -File scripts/install-git-hooks.ps1`.

Releases use Conventional Commit PR titles. Release Please opens the version PR;
merging it creates the tag, release notes, platform packages, and Homebrew update.

### Bundled PowerShell modules

The app ships with pinned versions of MicrosoftTeams and the Microsoft.Graph.* modules it
needs, declared in [`Scripts/module-versions.json`](Scripts/module-versions.json). CI reads
this file at build time (`Scripts/download-modules.ps1` / `.sh`) to download exactly those
versions into `Modules/`, which are bundled into every published build. The pinned versions
are also surfaced in the app itself, in **Settings → About**, alongside the bundled
PowerShell SDK version.

The [`module-compatibility.yml`](.github/workflows/module-compatibility.yml) workflow runs
weekly (and on manual dispatch) to check the PowerShell Gallery for newer releases of the
pinned modules. When a newer version is found, it bumps `module-versions.json`, builds and
tests against the update, and opens a pull request if that succeeds (or an issue with the
failure details if it doesn't) — the pins are never bumped automatically without a passing
build.

To bump a pinned version manually: edit `Scripts/module-versions.json` and either run
`Scripts/download-modules.ps1` / `.sh` locally to re-download `Modules/`, or just push the
change — the next CI build downloads the updated versions automatically.

## License

MIT © 2026 Patrik Lleshaj. See [LICENSE](LICENSE).
