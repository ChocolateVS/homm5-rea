#!/bin/sh
# Builds HEAD + the currently staged changes in a clean worktree (and runs tests with --test).
ROOT=G:/H5Reborn
WT=G:/H5Reborn-audit-check
TMP=$ROOT/tools/re/audit/out/staged.diff
mkdir -p "$ROOT/tools/re/audit/out"
cd "$ROOT" || exit 1
git diff --cached --binary > "$TMP"
git worktree remove --force "$WT" >/dev/null 2>&1; rm -rf "$WT"
git worktree add -f --detach "$WT" HEAD >/dev/null 2>&1 || { echo "worktree failed"; exit 1; }
cd "$WT" && { [ ! -s "$TMP" ] || git apply "$TMP"; } || { echo "apply failed"; exit 1; }
R=$(dotnet build game/H5Reborn.csproj -v q -nodeReuse:false 2>&1 | grep -E " error |Build succeeded" | sort -u | head -5)
echo "$R"
STATUS=0; case "$R" in *error*) STATUS=1 ;; esac
if [ $STATUS -eq 0 ] && [ "$1" = "--test" ]; then
  OUT=$(dotnet test 2>&1 | grep -E "Passed!|Failed!| error ")
  echo "$OUT"
  case "$OUT" in *Failed!*|*" error "*) STATUS=1 ;; esac
fi
cd "$ROOT"; git worktree remove --force "$WT" >/dev/null 2>&1
exit $STATUS
