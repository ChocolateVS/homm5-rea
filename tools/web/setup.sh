#!/usr/bin/env bash
# One-time toolchain setup for the web build (docs/WEB_BUILD.md), Linux or Windows (Git Bash).
# Installs, per user and without touching the system dotnet or PATH:
#   .NET SDK 10.0.402  ->  $H5_WEB_DOTNET_ROOT (default ~/.local/share/h5web/dotnet10, or %LOCALAPPDATA%\h5web\dotnet10)
#   wasm-tools workload, manifests pinned by tools/web/workload-rollback.json (brings Emscripten 3.1.56)
#   the harness's playwright-core 1.56.0 and its headless Chromium (only with --harness)
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/env.sh"
SDK=10.0.402

if ! "$DOTNET" --list-sdks 2>/dev/null | grep -q "^$SDK "; then
  mkdir -p "$H5_WEB_DOTNET_ROOT"
  if [ -n "${LOCALAPPDATA:-}" ] && command -v powershell.exe >/dev/null 2>&1; then
    curl -fsSL https://dot.net/v1/dotnet-install.ps1 -o "$H5_WEB_DOTNET_ROOT/dotnet-install.ps1"
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$H5_WEB_DOTNET_ROOT/dotnet-install.ps1")" \
      -Version "$SDK" -InstallDir "$(cygpath -w "$H5_WEB_DOTNET_ROOT")" -NoPath
  else
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$H5_WEB_DOTNET_ROOT/dotnet-install.sh"
    bash "$H5_WEB_DOTNET_ROOT/dotnet-install.sh" --version "$SDK" --install-dir "$H5_WEB_DOTNET_ROOT" --no-path
  fi
fi
. "$HERE/env.sh"
"$DOTNET" --list-sdks

ROLLBACK="$HERE/workload-rollback.json"
if command -v cygpath >/dev/null 2>&1; then ROLLBACK="$(cygpath -m "$ROLLBACK")"; fi
"$DOTNET" workload install wasm-tools --from-rollback-file "$ROLLBACK"
"$DOTNET" workload list

if [ "${1:-}" = "--harness" ]; then
  (cd "$HERE/harness" && npm install --no-audit --no-fund && npx playwright-core install chromium)
fi
echo "web toolchain ready: $DOTNET ($("$DOTNET" --version)). Build with tools/web/build.sh"
