#!/bin/sh
# Audit workstream: build + test HEAD plus MY working-tree files (given as paths) in a private clean
# worktree, without touching the shared index (other agents commit concurrently).
# Usage: verify.sh "<path> <path> ..." [--test | <xunit filter>]
ROOT=G:/H5Reborn
WT=G:/H5Reborn-audit-check
cd "$ROOT" || exit 1
git worktree remove --force "$WT" >/dev/null 2>&1; rm -rf "$WT"
git worktree add -f --detach "$WT" HEAD >/dev/null 2>&1 || { echo "worktree failed"; exit 1; }
for p in $1; do mkdir -p "$WT/$(dirname "$p")"; cp "$ROOT/$p" "$WT/$p"; done
cd "$WT"
B=$(dotnet build game/H5Reborn.csproj -nodeReuse:false 2>&1 | grep -E " error |Build succeeded" | sort -u | head -8); echo "$B"
S=0; case "$B" in *error*) S=1 ;; esac
if [ $S -eq 0 ] && [ "$2" = "--test" ]; then
  T=$(dotnet test -nodeReuse:false 2>&1 | grep -E "Passed!|Failed!| error |\[FAIL\]"); echo "$T"
  case "$T" in *Failed!*|*" error "*) S=1 ;; esac
elif [ $S -eq 0 ] && [ -n "$2" ]; then
  T=$(dotnet test tests/H5.Combat.Tests -nodeReuse:false --filter "$2" 2>&1 | grep -E "Passed!|Failed!| error |\[FAIL\]|Error Message|Assert"); echo "$T"
  case "$T" in *Failed!*) S=1 ;; esac
fi
cd "$ROOT"; git worktree remove --force "$WT" >/dev/null 2>&1
exit $S
