#!/usr/bin/env bash
# Rebuild the private run tree from HEAD, then overlay the given working files (or path::file prepared blobs).
set -e
cd "$(git rev-parse --show-toplevel)"
R="C:/Users/rghth/AppData/Local/Temp/claude/G--HoMM-5---Tribes-of-the-East/e85be553-54d1-4853-aa98-82c8dd346b0f/scratchpad/cui4/run"
rm -rf "$R/src" "$R/game/src" "$R/tests"
git archive --format=tar HEAD -- src game tests | tar -x -C "$R"
for p in "$@"; do
  case "$p" in *::*) cp "${p#*::}" "$R/${p%%::*}";; *) mkdir -p "$R/$(dirname "$p")"; cp "$p" "$R/$p";; esac
done
cd "$R" && dotnet build game/H5Reborn.csproj -v q -nologo 2>&1 | grep -E " error |Build succeeded" | sort -u | head -5
