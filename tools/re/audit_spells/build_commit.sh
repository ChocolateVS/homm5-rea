#!/bin/sh
# Builds (and with --test, runs the full test suite on) a commit in a clean worktree under the scratchpad.
# Usage: build_commit.sh <commit> [--test]
WT=C:/Users/rghth/AppData/Local/Temp/claude/G--HoMM-5---Tribes-of-the-East/e85be553-54d1-4853-aa98-82c8dd346b0f/scratchpad/spellaudit-wt
cd G:/H5Reborn || exit 1
git worktree remove --force "$WT" >/dev/null 2>&1; rm -rf "$WT"
git worktree add -f --detach "$WT" "$1" >/dev/null 2>&1 || { echo "worktree failed"; exit 1; }
cd "$WT" || exit 1
R=$(dotnet build game/H5Reborn.csproj -v q -nodeReuse:false 2>&1 | grep -E " error |Build succeeded" | sort -u | head -5)
echo "$R"
STATUS=0; case "$R" in *error*|"") STATUS=1 ;; esac
if [ $STATUS -eq 0 ] && [ "$2" = "--test" ]; then
  OUT=$(dotnet test -nodeReuse:false 2>&1 | grep -E "Passed!|Failed!| error |\[FAIL\]")
  echo "$OUT"
  case "$OUT" in *Failed!*|*" error "*) STATUS=1 ;; esac
fi
cd G:/H5Reborn; git worktree remove --force "$WT" >/dev/null 2>&1
exit $STATUS
