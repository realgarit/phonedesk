#!/bin/bash
# Version bump script for semantic versioning
# Usage: ./Scripts/bump-version.sh [major|minor|patch] [commit-message] [--push] [--branch=dev]

set -e

# Default values
BUMP_TYPE=""


# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        major|minor|patch)
            BUMP_TYPE="$1"
            shift
            ;;
        --help|-h)
            echo "Usage: $0 [major|minor|patch]"
            echo ""
            echo "Examples:"
            echo "  $0 patch"
            echo "  $0 minor"
            echo "  $0 major"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

# Validate bump type
if [ -z "$BUMP_TYPE" ]; then
    echo "Error: Bump type (major|minor|patch) is required"
    echo "Usage: $0 [major|minor|patch]"
    exit 1
fi

# Get repo root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

# Read current version from csproj
CSPROJ_PATH="$REPO_ROOT/phonedesk.csproj"
if [ ! -f "$CSPROJ_PATH" ]; then
    echo "Error: phonedesk.csproj not found"
    exit 1
fi

CURRENT_VERSION=$(grep '<Version>' "$CSPROJ_PATH" | sed -n 's/.*<Version>\([0-9.]*\)<\/Version>.*/\1/p' | head -1)

if [ -z "$CURRENT_VERSION" ]; then
    echo "Error: Could not find version in csproj file"
    exit 1
fi

echo -e "\033[36mCurrent version: $CURRENT_VERSION\033[0m"

# Parse version (support both 3-part and 4-part versions)
IFS='.' read -ra VERSION_PARTS <<< "$CURRENT_VERSION"
MAJOR=${VERSION_PARTS[0]}
MINOR=${VERSION_PARTS[1]}
PATCH=${VERSION_PARTS[2]}
REVISION=${VERSION_PARTS[3]:-0}  # Default to 0 if 4th part doesn't exist

# Bump version
case $BUMP_TYPE in
    major)
        MAJOR=$((MAJOR + 1))
        MINOR=0
        PATCH=0
        REVISION=0
        ;;
    minor)
        MINOR=$((MINOR + 1))
        PATCH=0
        REVISION=0
        ;;
    patch)
        PATCH=$((PATCH + 1))
        REVISION=0
        ;;
esac

NEW_VERSION="$MAJOR.$MINOR.$PATCH.$REVISION"
NEW_VERSION_SHORT="$MAJOR.$MINOR.$PATCH"
echo -e "\033[32mNew version: $NEW_VERSION_SHORT (Win: $NEW_VERSION)\033[0m"

# Update csproj file
sed -i.bak "s/<Version>$CURRENT_VERSION<\/Version>/<Version>$NEW_VERSION_SHORT<\/Version>/g" "$CSPROJ_PATH"
sed -i.bak "s/<AssemblyVersion>[0-9.]*<\/AssemblyVersion>/<AssemblyVersion>$NEW_VERSION<\/AssemblyVersion>/g" "$CSPROJ_PATH"
sed -i.bak "s/<FileVersion>[0-9.]*<\/FileVersion>/<FileVersion>$NEW_VERSION<\/FileVersion>/g" "$CSPROJ_PATH"
rm -f "$CSPROJ_PATH.bak"
echo -e "\033[32m✓ Updated phonedesk.csproj\033[0m"

# Update app.manifest (Windows requires 4-part version)
MANIFEST_PATH="$REPO_ROOT/app.manifest"
sed -i.bak "s/    version=\"[0-9.]*\"/    version=\"$NEW_VERSION\"/g" "$MANIFEST_PATH"
rm -f "$MANIFEST_PATH.bak"
echo -e "\033[32m✓ Updated app.manifest\033[0m"

# Update ConstantsService.cs (use 3-part version for display)
CONSTANTS_PATH="$REPO_ROOT/src/PhoneDesk.Domain/ConstantsService.cs"
sed -i.bak "s/public const string Version = \"Version [0-9.]*\";/public const string Version = \"Version $NEW_VERSION_SHORT\";/g" "$CONSTANTS_PATH"
rm -f "$CONSTANTS_PATH.bak"
echo -e "\033[32m✓ Updated ConstantsService.cs\033[0m"

# Update version.txt (release-please's simple package version source)
VERSION_FILE="$REPO_ROOT/version.txt"
printf '%s\n' "$NEW_VERSION_SHORT" > "$VERSION_FILE"
echo -e "\033[32m✓ Updated version.txt\033[0m"

echo ""
echo -e "\033[32mVersion bumped from $CURRENT_VERSION to $NEW_VERSION\033[0m"
echo -e "\033[33mNext steps:\033[0m"
echo -e "\033[37m  1. Review your changes\033[0m"
echo -e "\033[37m  2. Commit and push manually\033[0m"
