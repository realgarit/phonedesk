#!/usr/bin/env bash
# Publishes the MCP server (phonedesk-mcp) into the desktop app's publish folder.
#
# The server ships in the same folder as the app and shares its files: the .NET runtime, the
# PowerShell SDK and the bundled PowerShell modules. That is safe only when every file both
# publishes contain is identical. This script publishes the server to a separate folder, fails
# when a shared file differs, and then adds the server's own files to the app folder.
#
# Usage: scripts/publish-mcp-server.sh <runtime identifier> <app publish dir>
set -euo pipefail

RUNTIME="${1:?runtime identifier, for example win-x64}"
APP_DIR="${2:?publish folder of the desktop app}"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MCP_DIR="$(mktemp -d)"
trap 'rm -rf "$MCP_DIR"' EXIT

if [ ! -d "$APP_DIR" ]; then
  echo "App publish folder not found: $APP_DIR" >&2
  exit 1
fi

dotnet publish "$REPO_ROOT/src/PhoneDesk.Mcp/PhoneDesk.Mcp.csproj" \
  --configuration Release --runtime "$RUNTIME" --self-contained true --output "$MCP_DIR"

conflicts=0
added=0
while IFS= read -r -d '' file; do
  relative="${file#"$MCP_DIR"/}"
  target="$APP_DIR/$relative"
  if [ -e "$target" ]; then
    if ! cmp -s "$file" "$target"; then
      echo "CONFLICT: $relative differs between the app and the MCP server" >&2
      conflicts=$((conflicts + 1))
    fi
  else
    mkdir -p "$(dirname "$target")"
    cp "$file" "$target"
    added=$((added + 1))
  fi
done < <(find "$MCP_DIR" -type f -print0)

if [ "$conflicts" -gt 0 ]; then
  echo "$conflicts shared file(s) differ. Align the package versions of src/PhoneDesk.Mcp with phonedesk.csproj." >&2
  exit 1
fi

if [ -f "$APP_DIR/phonedesk-mcp" ]; then
  chmod +x "$APP_DIR/phonedesk-mcp"
fi

if [ ! -f "$APP_DIR/phonedesk-mcp" ] && [ ! -f "$APP_DIR/phonedesk-mcp.exe" ]; then
  echo "The MCP server executable is missing from $APP_DIR" >&2
  exit 1
fi

echo "MCP server added to $APP_DIR ($added files, no conflicts)."
