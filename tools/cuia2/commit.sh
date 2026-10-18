#!/usr/bin/env bash
# Commit only the given working-tree files on top of HEAD without touching the shared index
# (others keep files staged there), verify a clean checkout builds, then advance HEAD.
# Usage: bash tools/cuia2/commit.sh "<message>" [--test] path...
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
msg="$1"; shift
runtests=0; if [ "${1:-}" = "--test" ]; then runtests=1; shift; fi
SCR="C:/Users/rghth/AppData/Local/Temp/claude/G--HoMM-5---Tribes-of-the-East/e85be553-54d1-4853-aa98-82c8dd346b0f/scratchpad/cui2"
mkdir -p "$SCR"
idx="$SCR/index.tmp"; rm -f "$idx"
head=$(git rev-parse HEAD)
GIT_INDEX_FILE="$idx" git read-tree HEAD
for p in "$@"; do
  if [ -e "$p" ]; then GIT_INDEX_FILE="$idx" git add -f -- "$p"; else GIT_INDEX_FILE="$idx" git rm -q --cached -- "$p" || true; fi
done
tree=$(GIT_INDEX_FILE="$idx" git write-tree); rm -f "$idx"
printf '%s\n\nCo-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>\n' "$msg" > "$SCR/msg.txt"
c=$(git -c user.email=office@rennacs.com -c user.name=H5Reborn commit-tree "$tree" -p "$head" -F "$SCR/msg.txt")
wt="$SCR/wt"
git worktree remove --force "$wt" 2>/dev/null || rm -rf "$wt"
git worktree add -q --detach "$wt" "$c"
( cd "$wt" && dotnet build game/H5Reborn.csproj -v q -nologo 2>&1 | grep -E " error |Build succeeded" | sort -u | head -5 ) | tee "$SCR/build.log"
grep -q "Build succeeded" "$SCR/build.log" || { echo "BUILD FAILED; HEAD not moved"; git worktree remove --force "$wt"; exit 1; }
if [ $runtests = 1 ]; then
  ( cd "$wt" && dotnet test -v q -nologo 2>&1 | grep -E "Passed!|Failed!" ) | tee "$SCR/test.log"
  grep -q "Failed!" "$SCR/test.log" && { echo "TESTS FAILED; HEAD not moved"; git worktree remove --force "$wt"; exit 1; }
fi
git worktree remove --force "$wt"
git update-ref HEAD "$c" "$head" || { echo "HEAD moved meanwhile; rerun"; exit 1; }
git reset -q HEAD -- "$@"
echo "committed $(git rev-parse --short HEAD)"
