#!/bin/sh
# Verifies that HEAD plus the given working-tree files builds in a clean worktree.
# Usage: tools/advdone/cleanbuild.sh [--test] <path>...
ROOT=G:/H5Reborn
WT=G:/H5Reborn-advdone-check
TEST=0
if [ "$1" = "--test" ]; then TEST=1; shift; fi
cd "$ROOT" || exit 1
git worktree remove --force "$WT" >/dev/null 2>&1
rm -rf "$WT"
git worktree add -f --detach "$WT" HEAD >/dev/null 2>&1 || { echo "worktree failed"; exit 1; }
for p in "$@"; do
  if [ -e "$ROOT/$p" ]; then mkdir -p "$WT/$(dirname "$p")"; cp -r "$ROOT/$p" "$WT/$p"; else rm -rf "$WT/$p"; fi
done
mkdir -p "$WT/tools"
cp -r "$ROOT/game/nuget.config" "$WT/game/nuget.config" 2>/dev/null
cmd //c mklink /J "G:\\H5Reborn-advdone-check\\tools\\godot" "G:\\H5Reborn\\tools\\godot" >/dev/null 2>&1
cd "$WT" || exit 1
R=$(dotnet build game/H5Reborn.csproj -v q -nodeReuse:false 2>&1 | grep -E " error |Build succeeded" | sort -u | head -5)
echo "$R"
case "$R" in *error*) STATUS=1 ;; *) STATUS=0 ;; esac
if [ $STATUS -eq 0 ] && [ $TEST -eq 1 ]; then
  dotnet test 2>&1 | grep -E "Passed!|Failed!| error " | head -20
  dotnet test 2>&1 | grep -q "Failed!" && STATUS=1
fi
cd "$ROOT"
git worktree remove --force "$WT" >/dev/null 2>&1
exit $STATUS
