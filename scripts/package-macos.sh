#!/usr/bin/env bash
# Assemble an ad-hoc-signed "PhoneDesk.app" from a dotnet publish output.
#
# Usage: ./Scripts/package-macos.sh <publish-dir> <version> <output-dir>
#   e.g. ./Scripts/package-macos.sh ./publish/osx-arm64 3.17.0 ./artifacts
#
# Produces: <output-dir>/PhoneDesk.app  (signed, ready to ditto-zip)

set -euo pipefail

PUBLISH_DIR="${1:?publish dir required}"
VERSION="${2:?version required}"
OUTPUT_DIR="${3:?output dir required}"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP_NAME="PhoneDesk"
BUNDLE_ID="ch.realgar.teams-phonemanager"
APP="$OUTPUT_DIR/$APP_NAME.app"

# Optional stable signing identity (tertius pattern): when SIGN_IDENTITY is
# set (self-signed cert CN imported via Scripts/import-cert.sh), every Mach-O
# is signed with it. Reusing the same certificate on every release keeps the
# codesign designated requirement stable, so macOS treats upgrades as the same
# app and keychain items (MSAL token cache) and TCC grants survive
# `brew upgrade`. Without it, binaries keep/receive ad-hoc signatures.
#
# Signing happens on the PUBLISH output, BEFORE bundle assembly: codesign
# refuses to sign a bundle's main executable in place (it would attempt to
# seal the bundle and reject the .NET managed-dll layout as nested code).
SIGN_IDENTITY="${SIGN_IDENTITY:-}"
chmod +x "$PUBLISH_DIR/phonedesk"
find "$PUBLISH_DIR" -type f | while read -r f; do
  if file "$f" | grep -q "Mach-O"; then
    if [ -n "$SIGN_IDENTITY" ]; then
      if [ "$(basename "$f")" = "phonedesk" ]; then
        codesign --force --sign "$SIGN_IDENTITY" --identifier "$BUNDLE_ID" \
          ${KEYCHAIN:+--keychain "$KEYCHAIN"} "$f"
      else
        codesign --force --sign "$SIGN_IDENTITY" \
          ${KEYCHAIN:+--keychain "$KEYCHAIN"} "$f"
      fi
    elif ! codesign --display "$f" >/dev/null 2>&1; then
      codesign --force --sign - "$f"
    fi
  fi
done

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -R "$PUBLISH_DIR"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/phonedesk"
# The MCP server is a second executable in the bundle; AI clients start it by its full path.
if [ -f "$APP/Contents/MacOS/phonedesk-mcp" ]; then
  chmod +x "$APP/Contents/MacOS/phonedesk-mcp"
fi
cp "$REPO_ROOT/assets/icon.icns" "$APP/Contents/Resources/icon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleExecutable</key>
    <string>phonedesk</string>
    <key>CFBundleIconFile</key>
    <string>icon.icns</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>15.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSHumanReadableCopyright</key>
    <string>Copyright © 2026 Patrik Lleshaj. MIT License.</string>
</dict>
</plist>
PLIST

# Assert every Mach-O carries a signature (signing happened pre-assembly on
# the publish output — codesign refuses to re-sign a bundle main executable
# in place, and the bundle is never sealed because codesign rejects the .NET
# managed-dll layout as unsigned "nested code").
find "$APP/Contents/MacOS" -type f | while read -r f; do
  if file "$f" | grep -q "Mach-O"; then
    codesign --display "$f" >/dev/null 2>&1 || { echo "UNSIGNED: $f" >&2; exit 1; }
  fi
done

if [ -n "$SIGN_IDENTITY" ]; then
  echo "Signed with identity: $SIGN_IDENTITY"
  codesign -d --requirements - "$APP/Contents/MacOS/phonedesk" 2>&1 | sed -n 's/^designated => /DR: /p'
fi
echo "Packaged: $APP"
