#!/bin/sh
# Audit workstream: build + test HEAD plus the currently staged changes in a private clean worktree.
ROOT=G:/H5Reborn
WT=G:/H5Reborn-audit-check
cd "$ROOT" || exit 1
git diff --cached --binary > "$ROOT/tools/re/audit/out/staged.diff"
git worktree remove --force "$WT" >/dev/null 2>&1; rm -rf "$WT"
git worktree add -f --detach "$WT" HEAD >/dev/null 2>&1 || { echo "worktree failed"; exit 1; }
cd "$WT" && { [ ! -s "$ROOT/tools/re/audit/out/staged.diff" ] || git apply "$ROOT/tools/re/audit/out/staged.diff"; } || { echo "apply failed"; exit 1; }
B=$(dotnet build game/H5Reborn.csproj -nodeReuse:false 2>&1 | grep -E " error |Build succeeded" | sort -u | head -8); echo "$B"
case "$B" in *error*) cd "$ROOT"; git worktree remove --force "$WT" >/dev/null 2>&1; exit 1 ;; esac
if [ "$1" = "--test" ]; then
  T=$(dotnet test -nodeReuse:false 2>&1 | grep -E "Passed!|Failed!| error |\[FAIL\]"); echo "$T"
  case "$T" in *Failed!*|*" error "*) cd "$ROOT"; git worktree remove --force "$WT" >/dev/null 2>&1; exit 1 ;; esac
elif [ -n "$1" ]; then
  T=$(dotnet test tests/H5.Combat.Tests -nodeReuse:false --filter "$1" 2>&1 | grep -E "Passed!|Failed!| error |\[FAIL\]|Assert|Expected|Actual"); echo "$T"
fi
cd "$ROOT"; git worktree remove --force "$WT" >/dev/null 2>&1
