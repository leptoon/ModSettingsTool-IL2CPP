#!/usr/bin/env bash
#
# package-release.sh: build (Release) + assemble the player-facing distribution zip under dist/.
#
# Produces dist/ModSettingsTool-v<version>.zip laid out so a player extracts it into their
# Supermarket Simulator game folder (the one that contains BepInEx/):
#
#   BepInEx/plugins/ModSettingsTool/
#     ModSettingsTool.dll   the built Release plugin
#     LICENSE.txt           Apache 2.0, copied from ./LICENSE
#     README.txt            player install/usage notes, from ./packaging/README.txt
#
# Version is read from the csproj <Version> (single source of truth). This script removes ONLY this
# release's own staging dir + zip under dist/; any other dist/ files (e.g. the Nexus copy) are left
# untouched.
#
# Usage:
#   scripts/package-release.sh            # build Release, then assemble + zip
#   scripts/package-release.sh --no-build # package the EXISTING build/ModSettingsTool.dll
#
# Exit codes: 0 ok / 1 build, input, or zip error.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
CSPROJ="$REPO_ROOT/src/ModSettingsTool/ModSettingsTool.csproj"
BUILD_DIR="$REPO_ROOT/build"
DLL="ModSettingsTool.dll"
LICENSE_SRC="$REPO_ROOT/LICENSE"
README_SRC="$REPO_ROOT/packaging/README.txt"

# Version from the csproj <Version>.
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$CSPROJ" | head -n1)"
if [ -z "$VERSION" ]; then
  echo "[package] Could not read <Version> from $CSPROJ" >&2
  exit 1
fi

NAME="ModSettingsTool-v$VERSION"
DIST_DIR="$REPO_ROOT/dist"
STAGE="$DIST_DIR/$NAME"
PLUGIN_DIR="$STAGE/BepInEx/plugins/ModSettingsTool"
ZIP="$DIST_DIR/$NAME.zip"

if ! command -v zip >/dev/null 2>&1; then
  echo "[package] 'zip' is not installed; cannot build the archive." >&2
  exit 1
fi

# 1. Build Release (unless --no-build). Never package a broken build.
if [ "${1:-}" != "--no-build" ]; then
  echo "[package] Building Release..."
  if ! "$DOTNET" build -c Release "$CSPROJ" >/tmp/modsettingstool-package-build.log 2>&1; then
    echo "[package] BUILD FAILED, see /tmp/modsettingstool-package-build.log (NOT packaging):" >&2
    tail -8 /tmp/modsettingstool-package-build.log >&2
    exit 1
  fi
fi
for f in "$BUILD_DIR/$DLL" "$LICENSE_SRC" "$README_SRC"; do
  if [ ! -f "$f" ]; then echo "[package] Missing input: $f" >&2; exit 1; fi
done

# 2. Stage the player layout fresh (remove only THIS release's prior outputs).
rm -rf "$STAGE" "$ZIP"
mkdir -p "$PLUGIN_DIR"
cp -f "$BUILD_DIR/$DLL" "$PLUGIN_DIR/$DLL"
cp -f "$LICENSE_SRC" "$PLUGIN_DIR/LICENSE.txt"
cp -f "$README_SRC" "$PLUGIN_DIR/README.txt"

# 3. Zip with the archive root = BepInEx/ (so players extract into the game folder).
( cd "$STAGE" && zip -r -X "$ZIP" BepInEx >/dev/null ) || { echo "[package] zip failed" >&2; exit 1; }

# 4. Report.
echo "[package]   DLL md5: $(md5sum < "$BUILD_DIR/$DLL" | cut -d' ' -f1)"
echo "[package] OK: $ZIP (v$VERSION)"
unzip -l "$ZIP"
