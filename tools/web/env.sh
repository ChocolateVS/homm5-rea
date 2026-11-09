# Sourced by tools/web/*.sh: locates the pinned .NET 10 SDK (installed by setup.sh / setup.ps1) without touching PATH
# for the rest of the system. Override with H5_WEB_DOTNET_ROOT.
if [ -z "${H5_WEB_DOTNET_ROOT:-}" ]; then
  if [ -n "${LOCALAPPDATA:-}" ]; then
    H5_WEB_DOTNET_ROOT="$(cygpath -u "$LOCALAPPDATA" 2>/dev/null || echo "$LOCALAPPDATA")/h5web/dotnet10"
  else
    H5_WEB_DOTNET_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/h5web/dotnet10"
  fi
fi
export H5_WEB_DOTNET_ROOT
export DOTNET_ROOT="$H5_WEB_DOTNET_ROOT"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
if [ -x "$DOTNET_ROOT/dotnet.exe" ]; then DOTNET="$DOTNET_ROOT/dotnet.exe"; else DOTNET="$DOTNET_ROOT/dotnet"; fi
export DOTNET
