#!/usr/bin/env bash
#
# deploy-to-game.sh: build (Release) + deploy ModSettingsTool.dll into your game's BepInEx
# plugins folder, then md5-VERIFY the deployed bytes match the build output.
#
# WHY THIS EXISTS (process rule):
#   A smoke test against a STALE DLL is worse than no test; it produces a FALSE signal. Whenever a
#   smoke test is the obvious next step, build + deploy + verify FIRST, proactively.
#
# Usage:
#   scripts/deploy-to-game.sh            # build Release, then deploy + verify
#   scripts/deploy-to-game.sh --no-build # deploy the EXISTING build/ModSettingsTool.dll
#
# Deploy target is resolved from (in order):
#   1. $MODSETTINGSTOOL_REF_DIR (env), or
#   2. <ModSettingsToolRefDir> in the gitignored local.props
# pointing at the game's .../Supermarket Simulator/BepInEx dir. The DLL lands at
#   $REFDIR/plugins/ModSettingsTool/ModSettingsTool.dll
#
# Exit codes: 0 deployed+verified / 1 build or verify error / 3 no local game install (headless).
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
CSPROJ="$REPO_ROOT/src/ModSettingsTool/ModSettingsTool.csproj"
BUILD_DIR="$REPO_ROOT/build"
DLL="ModSettingsTool.dll"

# Resolve the game BepInEx dir (machine-local; never hard-coded)
REFDIR="${MODSETTINGSTOOL_REF_DIR:-}"
if [ -z "$REFDIR" ] && [ -f "$REPO_ROOT/local.props" ]; then
  REFDIR="$(sed -n 's:.*<ModSettingsToolRefDir>\(.*\)</ModSettingsToolRefDir>.*:\1:p' "$REPO_ROOT/local.props" | head -n1)"
fi
if [ -z "$REFDIR" ] || [ ! -d "$REFDIR" ]; then
  echo "[deploy] No game install here (no MODSETTINGSTOOL_REF_DIR / local.props ModSettingsToolRefDir, or the dir is absent)." >&2
  echo "[deploy] Skipping deploy; this looks like a headless/CI run, not a smoke box." >&2
  exit 3
fi

PLUGIN_DIR="$REFDIR/plugins/ModSettingsTool"

# 1. Build Release (unless --no-build). Never deploy a broken build.
if [ "${1:-}" != "--no-build" ]; then
  echo "[deploy] Building Release..."
  if ! "$DOTNET" build -c Release "$CSPROJ" >/tmp/modsettingstool-deploy-build.log 2>&1; then
    echo "[deploy] BUILD FAILED, see /tmp/modsettingstool-deploy-build.log (NOT deploying):" >&2
    tail -8 /tmp/modsettingstool-deploy-build.log >&2
    exit 1
  fi
fi
if [ ! -f "$BUILD_DIR/$DLL" ]; then
  echo "[deploy] No build output at $BUILD_DIR/$DLL. Run without --no-build to build first." >&2
  exit 1
fi

# 2. Copy (back up the prior DLL once) + 3. VERIFY the deployed bytes match the build output.
mkdir -p "$PLUGIN_DIR"
dest="$PLUGIN_DIR/$DLL"
[ -f "$dest" ] && cp -f "$dest" "$dest.bak"
cp -f "$BUILD_DIR/$DLL" "$dest"
if [ "$(md5sum < "$dest")" != "$(md5sum < "$BUILD_DIR/$DLL")" ]; then
  echo "[deploy] VERIFY FAILED: deployed $DLL does not match the fresh build output." >&2
  exit 1
fi
echo "[deploy]   md5 verified: $DLL"

SHA="$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo '?')"
echo "[deploy] OK: deployed commit $SHA -> $dest"

# 4. If the game is already running it has the OLD DLL mapped; it must be restarted.
if ps -eo comm 2>/dev/null | grep -qiE 'Supermarket|wine-preloader'; then
  echo "[deploy] NOTE: a game/proton process appears to be running. RESTART the game to load commit $SHA."
fi
