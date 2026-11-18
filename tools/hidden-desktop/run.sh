#!/bin/sh
# Run a command on the hidden "H5Test" desktop (README.md). Builds the launcher on first use into
# %LOCALAPPDATA%/H5Reborn/hidden-desktop/<source hash>/ (outside the repo, shared by all checkouts).
#   tools/hidden-desktop/run.sh [--desktop NAME] [--timeout SECONDS] [--] COMMAND [ARGS...]
D=$(cd "$(dirname "$0")" && pwd)
HASH=$(cat "$D/Program.cs" "$D/HiddenDesktop.csproj" | tr -d '\r' | sha1sum | cut -c1-12)
BASE="${LOCALAPPDATA:-$HOME/AppData/Local}/H5Reborn/hidden-desktop"
EXE="$BASE/$HASH/hidden-desktop.exe"
if [ ! -f "$EXE" ]; then
  TMP="$BASE/tmp-$$"
  dotnet build "$D/HiddenDesktop.csproj" -c Release -nologo -v q -o "$TMP" \
    "-p:BaseIntermediateOutputPath=$TMP/obj/" >&2 || { echo "hidden-desktop: build failed" >&2; exit 125; }
  # Another run may have finished the same build meanwhile; either copy is fine.
  mv "$TMP" "$BASE/$HASH" 2>/dev/null || rm -rf "$TMP"
fi
exec "$EXE" "$@"
