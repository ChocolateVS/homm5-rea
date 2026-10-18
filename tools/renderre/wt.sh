#!/bin/sh
# Private test worktree: HEAD + this workstream's changed files, so other agents' unfinished
# files can't break the build. Usage: tools/renderre/wt.sh  (prints build result)
ROOT=G:/H5Reborn
WT=G:/H5Reborn-render-wt
cd "$ROOT" || exit 1
if [ ! -d "$WT" ]; then git worktree add -f --detach "$WT" HEAD >/dev/null 2>&1; fi
cd "$WT" && git checkout -q --detach -f "$(git -C $ROOT rev-parse HEAD)" 2>/dev/null
for p in $(cat "$ROOT/tools/renderre/paths.txt"); do
  if [ -d "$ROOT/$p" ]; then mkdir -p "$WT/$p"; cp -r "$ROOT/$p/." "$WT/$p/"; elif [ -e "$ROOT/$p" ]; then mkdir -p "$WT/$(dirname "$p")"; cp "$ROOT/$p" "$WT/$p"; fi
done
[ -e "$WT/tools/godot" ] || { mkdir -p "$WT/tools"; powershell -NoProfile -Command "New-Item -ItemType Junction -Path 'G:\H5Reborn-render-wt\tools\godot' -Target 'G:\H5Reborn\tools\godot'" >/dev/null; }
git -C "$WT" checkout -q -- game/src/Adventure/AdvWorld.cs; python -I "$ROOT/tools/renderre/patch_advworld.py" "$WT/game/src/Adventure/AdvWorld.cs"
dotnet build "$WT/game/H5Reborn.csproj" -v q -nodeReuse:false 2>&1 | grep -E " error |Build succeeded" | sort -u | head -5
