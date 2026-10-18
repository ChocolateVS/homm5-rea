#!/bin/sh
# Commits exactly HEAD + the given files (taken from <srcdir>, default the working tree) using a
# temporary index, so other agents' staged or unstaged hunks never enter the commit.
# Usage: tools/boatsre/commit_isolated.sh <msgfile> <srcdir|-> <path>...
ROOT=G:/H5Reborn
MSG=$1; SRC=$2; shift 2
cd "$ROOT" || exit 1
OLD=$(git rev-parse HEAD)
export GIT_INDEX_FILE="$ROOT/.git/boats_index"
rm -f "$GIT_INDEX_FILE"
git read-tree HEAD || exit 1
for p in "$@"; do
  f="$ROOT/$p"; [ "$SRC" != "-" ] && [ -e "$SRC/$p" ] && f="$SRC/$p"
  if [ -e "$f" ]; then
    b=$(git hash-object -w "$f") && git update-index --add --cacheinfo 100644,"$b","$p" || exit 1
  else
    git update-index --force-remove "$p"
  fi
done
TREE=$(git write-tree)
NEW=$(git -c user.email=office@rennacs.com -c user.name=H5Reborn commit-tree "$TREE" -p "$OLD" -F "$MSG") || exit 1
unset GIT_INDEX_FILE
rm -f "$ROOT/.git/boats_index"
git update-ref refs/heads/main "$NEW" "$OLD" || { echo "HEAD moved, retry"; exit 1; }
git reset -q "$NEW" -- "$@"
echo "committed $NEW"
