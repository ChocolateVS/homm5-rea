#!/bin/sh
# Commits the given paths from the working tree on top of HEAD through a private index (the shared index may hold
# other agents' staged work). Usage: commit.sh "<msg>" path...
ROOT=G:/H5Reborn; cd "$ROOT" || exit 1
MSG=$1; shift
IDX=$ROOT/tools/re/audit/out/abil_a/index.tmp
rm -f "$IDX"
GIT_INDEX_FILE=$IDX git read-tree HEAD || exit 1
GIT_INDEX_FILE=$IDX git add -- "$@" || exit 1
GIT_INDEX_FILE=$IDX git -c user.email=office@rennacs.com -c user.name=H5Reborn commit -q -m "$MSG

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>" || exit 1
rm -f "$IDX"
# Refresh the shared index entries for these paths so they don't show as staged reverts.
git reset -q -- "$@" 2>/dev/null
git log --oneline -1
