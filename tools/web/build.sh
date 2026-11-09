#!/usr/bin/env bash
# Reproducible web build of H5Reborn (docs/WEB_BUILD.md). Works on Linux and on Windows (Git Bash).
#   tools/web/build.sh [--out DIR] [--interp]  -> DIR (default web/dist): a static site (index.html, godot.wasm, godot.pck, _framework/...)
#   The C# is AOT-compiled to wasm (about twice as fast as the Mono interpreter; the build takes ~12 minutes on a desktop and
#   godot.wasm grows to ~86 MB / 20 MB brotli). --interp: interpreter only (a 4-6 minute build, for quick checks).
# Toolchain: tools/web/setup.sh (or setup.ps1) installs the pinned .NET 10 SDK + wasm-tools workload.
# Never puts game assets in the output: the page asks the player for their own install.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="$REPO/web/dist"
AOT=true
while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="$(mkdir -p "$2" && cd "$2" && pwd)"; shift 2 ;;
    --interp) AOT=false; shift ;;
    -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
STAGE="$REPO/web/.build/game"

# Native path for dotnet (a Windows exe under Git Bash).
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi; }

# --- toolchain -------------------------------------------------------------------------------------------------
. "$REPO/tools/web/env.sh"
SDK_WANT=10.0.402
if ! "$DOTNET" --list-sdks 2>/dev/null | grep -q "^10\.0\.4"; then
  echo "error: .NET SDK $SDK_WANT not found ($DOTNET). Run tools/web/setup.sh (Linux / Git Bash) or tools/web/setup.ps1." >&2
  exit 1
fi

# --- stage: the game's Godot project (game/) + the web overlay (web/game/) -----------------------------------------
# obj/bin/.godot are kept between builds (incremental); sources are replaced so deleted files disappear.
mkdir -p "$STAGE"
rm -rf "$STAGE/src" "$STAGE/scenes" "$STAGE/H5Reborn.web/wwwroot"
cp -R "$REPO/game/src" "$REPO/game/scenes" "$STAGE/"
cp "$REPO/game/project.godot" "$STAGE/project.godot"
cp -R "$REPO/web/game/." "$STAGE/"
# Web renders with the Compatibility renderer (WebGL 2); the desktop project uses Forward+.
sed -i.bak -e 's/^renderer\/rendering_method=.*/renderer\/rendering_method="gl_compatibility"\nrenderer\/rendering_method.mobile="gl_compatibility"/' \
           -e 's/"Forward Plus"/"GL Compatibility"/' "$STAGE/project.godot"
# Web-only autoloads (web/game/src/Web/): WebProbe observes (screen markers for the smoke test, periodic stats);
# WebStorage keeps user:// in the browser's IndexedDB after System.IO writes too.
printf '\n[autoload]\n\nWebProbe="*res://src/Web/WebProbe.cs"\nWebStorage="*res://src/Web/WebStorage.cs"\n' >> "$STAGE/project.godot"
# Audio: mix every player in Godot's own mixer ("Stream", as on the desktop) instead of the web default "Sample"
# (Web Audio buffer sources), which stayed silent for the game's music players (docs/WEB_BUILD.md, Audio).
printf '\n[audio]\n\ngeneral/default_playback_type.web=0\n' >> "$STAGE/project.godot"
rm -f "$STAGE/project.godot.bak"
grep -q 'gl_compatibility' "$STAGE/project.godot" || { echo "error: could not switch project.godot to gl_compatibility" >&2; exit 1; }

# --- publish ---------------------------------------------------------------------------------------------------
cd "$STAGE"
AOT_ARGS=()
if [ "$AOT" = true ]; then
  # Mono AOT (RunAOTCompilation). Its cross compiler cannot open files past Windows' 260-character path limit, so
  # its intermediates go to a short folder (H5_WEB_AOT_OBJ, default web/.aot/ in the repository).
  AOT_OBJ="${H5_WEB_AOT_OBJ:-$REPO/web/.aot}"
  mkdir -p "$AOT_OBJ"
  AOT_ARGS=(-p:RunAOTCompilation=true "-p:IntermediateOutputPath=$(native "$AOT_OBJ")/")
fi
"$DOTNET" publish "$(native "$STAGE/H5Reborn.web/H5Reborn.web.csproj")" -c Release \
  "-p:H5RepoRoot=$(native "$REPO")/" "${AOT_ARGS[@]}" -nologo -clp:NoSummary
BUNDLE="$STAGE/H5Reborn.web/AppBundle"
[ -f "$BUNDLE/index.html" ] && [ -f "$BUNDLE/godot.wasm" ] && [ -f "$BUNDLE/godot.pck" ] \
  || { echo "error: publish did not produce $BUNDLE/{index.html,godot.wasm,godot.pck}" >&2; exit 1; }

# --- output ----------------------------------------------------------------------------------------------------
rm -rf "$OUT"
mkdir -p "$OUT"
cp -R "$BUNDLE/." "$OUT/"
rm -f "$OUT/.stamp"
COMMIT="$(git -C "$REPO" rev-parse --short HEAD 2>/dev/null || echo unknown)"
DIRTY="$(git -C "$REPO" status --porcelain -- game src web 2>/dev/null | head -1)"
cat > "$OUT/build-info.json" <<JSON
{ "commit": "$COMMIT", "dirty": $([ -n "$DIRTY" ] && echo true || echo false), "built": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "aot": $AOT, "dotnetSdk": "$("$DOTNET" --version)", "twoDog": "4.7.2.124", "twoDogNatives": "4.7.2.28", "godot": "4.7.2" }
JSON
echo "web build: $OUT ($(du -sh "$OUT" | cut -f1)); serve it with tools/web/harness/serve.mjs or any static server (docs/WEB_BUILD.md)"
