#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

EXPECTED_VERSION="${1:-$(tr -d '[:space:]' < version.txt)}"
if [[ ! "$EXPECTED_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "error: expected version '$EXPECTED_VERSION' is not X.Y.Z" >&2
  exit 1
fi

read_source() {
  local label="$1"
  local actual="$2"
  if [[ "$actual" != "$EXPECTED_VERSION" ]]; then
    echo "error: $label is '$actual', expected '$EXPECTED_VERSION'" >&2
    exit 1
  fi
}

read_source "version.txt" "$(tr -d '[:space:]' < version.txt)"
read_source "phonedesk.csproj Version" "$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' phonedesk.csproj | head -1)"
read_source "phonedesk.csproj AssemblyVersion" "$(sed -n 's:.*<AssemblyVersion>\([^<]*\)</AssemblyVersion>.*:\1:p' phonedesk.csproj | head -1 | sed 's/\.0$//')"
read_source "phonedesk.csproj FileVersion" "$(sed -n 's:.*<FileVersion>\([^<]*\)</FileVersion>.*:\1:p' phonedesk.csproj | head -1 | sed 's/\.0$//')"
read_source "app.manifest version" "$(grep -o 'version="[0-9.]*"' app.manifest | tail -1 | cut -d'"' -f2 | sed 's/\.0$//')"
read_source "ConstantsService Version" "$(sed -n 's:.*Version = "Version \([0-9.]*\)".*:\1:p' src/PhoneDesk.Domain/ConstantsService.cs | head -1)"

echo "Version sources are synchronized at $EXPECTED_VERSION."
