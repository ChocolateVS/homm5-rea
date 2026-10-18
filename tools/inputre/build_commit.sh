#!/bin/sh
# Builds (and with --test, tests) a commit in a clean worktree. Usage: build_commit.sh <commit> [--test]
WT=G:/H5Reborn-input-check
cd G:/H5Reborn || exit 1
git worktree remove --force "$WT" >/dev/null 2>&1
git worktree add -f --detach "$WT" "$1" >/dev/null 2>&1 || { echo "worktree failed"; exit 1; }
mkdir -p "$WT/tools"; cmd //c mklink /J "G:\H5Reborn-input-check\tools\godot" "G:\H5Reborn\tools\godot" >/dev/null 2>&1
cd "$WT" || exit 1
R=$(dotnet build game/H5Reborn.csproj -v q -nodeReuse:false 2>&1 | grep -E " error |Build succeeded" | sort -u | head -5)
echo "$R"
STATUS=0; case "$R" in *error*) STATUS=1 ;; esac
if [ $STATUS -eq 0 ] && [ "$2" = "--test" ]; then
  OUT=$(dotnet test 2>&1 | grep -E "Passed!|Failed!| error ")
  echo "$OUT"
  case "$OUT" in *Failed!*|*" error "*) STATUS=1 ;; esac
fi
cd G:/H5Reborn; git worktree remove --force "$WT" >/dev/null 2>&1
exit $STATUS
