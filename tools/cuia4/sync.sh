#!/usr/bin/env bash
# Copy our working-tree files into the private run worktree (HEAD + only our changes).
set -e
cd "$(git rev-parse --show-toplevel)"
R="C:/Users/rghth/AppData/Local/Temp/claude/G--HoMM-5---Tribes-of-the-East/e85be553-54d1-4853-aa98-82c8dd346b0f/scratchpad/cui/run"
for p in "$@"; do mkdir -p "$R/$(dirname "$p")"; cp "$p" "$R/$p"; done
cd "$R" && dotnet build game/H5Reborn.csproj -v q -nologo 2>&1 | grep -E " error |Build succeeded" | sort -u | head -5
