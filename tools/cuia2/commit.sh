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
  case "$p" in
    *::*)  # "path::file": take the blob from a prepared file (HEAD's version plus only our hunks)
      src="${p#*::}"; p="${p%%::*}"
      blob=$(git hash-object -w "$src")
      GIT_INDEX_FILE="$idx" git update-index --add --cacheinfo "100644,$blob,$p"
      echo "prepared $p"; continue;;
  esac
  if [ ! -e "$p" ]; then GIT_INDEX_FILE="$idx" git rm -q --cached -- "$p" || true; continue; fi
  hb=$(git rev-parse -q --verify "HEAD:$p" 2>/dev/null || true)
  ib=$(git ls-files -s -- "$p" | awk '{print $2}')
  if [ -n "$hb" ] && [ -n "$ib" ] && [ "$hb" != "$ib" ]; then
    # Another agent committed this file through a private index: the shared working copy is
    # based on the index blob, so merge our edits (index -> working) into HEAD's version.
    git cat-file -p "$ib" > "$SCR/base.tmp"; git cat-file -p "$hb" > "$SCR/head.tmp"; cp "$p" "$SCR/mine.tmp"
    if ! git merge-file -p "$SCR/head.tmp" "$SCR/base.tmp" "$SCR/mine.tmp" > "$SCR/merged.tmp"; then echo "MERGE CONFLICT in $p"; exit 1; fi
    blob=$(git hash-object -w "$SCR/merged.tmp")
    cp "$SCR/merged.tmp" "$SCR/merged.$(echo "$p" | tr '/' '_')"
    merged_paths="${merged_paths:-} $p"
    GIT_INDEX_FILE="$idx" git update-index --add --cacheinfo "100644,$blob,$p"
    echo "merged $p onto HEAD"
  else
    GIT_INDEX_FILE="$idx" git add -f -- "$p"
  fi
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
# If HEAD moved during the build/tests, re-base our blobs onto the new HEAD as long as the new commits
# don't touch our paths (then the verified content is unchanged by them); otherwise ask for a rerun.
for attempt in 1 2 3 4 5 6 7 8; do
  if git update-ref HEAD "$c" "$head" 2>/dev/null; then break; fi
  new=$(git rev-parse HEAD)
  ours=(); for a in "$@"; do ours+=("${a%%::*}"); done
  if git diff --name-only "$head" "$new" -- "${ours[@]}" | grep -q .; then echo "HEAD moved and touched our paths; rerun"; exit 1; fi
  idx2="$SCR/index2.tmp"; rm -f "$idx2"
  GIT_INDEX_FILE="$idx2" git read-tree "$new"
  for pth in "${ours[@]}"; do
    if bl=$(git rev-parse -q --verify "$c:$pth" 2>/dev/null); then GIT_INDEX_FILE="$idx2" git update-index --add --cacheinfo "100644,$bl,$pth"
    else GIT_INDEX_FILE="$idx2" git rm -q --cached -- "$pth" 2>/dev/null || true; fi
  done
  tree=$(GIT_INDEX_FILE="$idx2" git write-tree); rm -f "$idx2"
  head="$new"
  c=$(git -c user.email=office@rennacs.com -c user.name=H5Reborn commit-tree "$tree" -p "$head" -F "$SCR/msg.txt")
  echo "re-based onto $(git rev-parse --short "$head") (attempt $attempt)"
  [ $attempt = 8 ] && { echo "HEAD keeps moving; rerun"; exit 1; }
done
paths=(); for a in "$@"; do paths+=("${a%%::*}"); done
git reset -q HEAD -- "${paths[@]}"
for mp in ${merged_paths:-}; do cp "$SCR/merged.$(echo "$mp" | tr '/' '_')" "$mp"; done
git diff --stat "$head" HEAD | tail -25
echo "committed $(git rev-parse --short HEAD)"
