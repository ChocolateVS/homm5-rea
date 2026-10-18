#!/bin/sh
# Verifies and commits a hunk-selected patch on top of HEAD without touching the shared index.
# Usage: check_commit.sh "<commit message>" <spec>...   (spec as in mypatch.py: path | path:1,3)
# Steps: build the patch, apply it in a clean scratch worktree of HEAD, build the game, run the FULL test suite,
# then apply it to a private index built from HEAD and commit.
ROOT=G:/H5Reborn
OUT=$ROOT/tools/re/audit/out/abilb
WT=C:/Users/rghth/AppData/Local/Temp/claude/G--HoMM-5---Tribes-of-the-East/e85be553-54d1-4853-aa98-82c8dd346b0f/scratchpad/abilb-check-wt
MSG=$1; shift
cd "$ROOT" || exit 1
PATCH=$OUT/my.patch
python -I tools/re/audit/abilb/mypatch.py "$PATCH" "$@" || exit 1
BASE=$(git rev-parse HEAD)
git worktree remove --force "$WT" >/dev/null 2>&1; rm -rf "$WT"
git worktree add -f --detach "$WT" "$BASE" >/dev/null 2>&1 || { echo "worktree failed"; exit 1; }
( cd "$WT" && git apply --whitespace=nowarn "$PATCH" ) || { echo "patch does not apply"; exit 1; }
cd "$WT"
B=$(dotnet build game/H5Reborn.csproj -nodeReuse:false 2>&1 | grep -E " error |Build succeeded" | sort -u | head -8); echo "$B"
case "$B" in *error*) cd "$ROOT"; git worktree remove --force "$WT" >/dev/null 2>&1; exit 1 ;; esac
T=$(dotnet test -nodeReuse:false 2>&1 | grep -E "Passed!|Failed!| error |\[FAIL\]"); echo "$T"
cd "$ROOT"; git worktree remove --force "$WT" >/dev/null 2>&1
case "$T" in *Failed!*|*" error "*) echo "tests failed"; exit 1 ;; esac
[ "$(git rev-parse HEAD)" = "$BASE" ] || echo "note: HEAD moved during the check; committing on the new HEAD"
IDX=$OUT/index.tmp
rm -f "$IDX"
GIT_INDEX_FILE=$IDX git read-tree HEAD || exit 1
GIT_INDEX_FILE=$IDX git apply --cached --whitespace=nowarn "$PATCH" || { echo "patch no longer applies to HEAD"; rm -f "$IDX"; exit 1; }
GIT_INDEX_FILE=$IDX git -c user.email=office@rennacs.com -c user.name=H5Reborn commit -q -m "$MSG

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>" || exit 1
rm -f "$IDX"
git log --oneline -1
