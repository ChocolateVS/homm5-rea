#!/bin/sh
# Private test worktree under the scratchpad: HEAD + this workstream's changed files, so other
# agents' unfinished files can't break the build. Run Godot with an explicit exe path:
#   G:/H5Reborn/tools/godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe --path "$WT/game"
ROOT=G:/H5Reborn
WT=${H5_RENDER_WT:-C:/Users/rghth/AppData/Local/Temp/claude/G--HoMM-5---Tribes-of-the-East/e85be553-54d1-4853-aa98-82c8dd346b0f/scratchpad/render/wt}
cd "$ROOT" || exit 1
if [ ! -d "$WT/.git" ] && [ ! -f "$WT/.git" ]; then git worktree add -f --detach "$WT" HEAD >/dev/null 2>&1; fi
cd "$WT" && git checkout -q --detach -f "$(git -C $ROOT rev-parse HEAD)" 2>/dev/null
for p in $(cat "$ROOT/tools/renderre/paths.txt"); do
  if [ -d "$ROOT/$p" ]; then mkdir -p "$WT/$p"; cp -r "$ROOT/$p/." "$WT/$p/"; elif [ -e "$ROOT/$p" ]; then mkdir -p "$WT/$(dirname "$p")"; cp "$ROOT/$p" "$WT/$p"; fi
done
git -C "$WT" checkout -q -- game/src/Adventure/AdvWorld.cs; python -I "$ROOT/tools/renderre/patch_advworld.py" "$WT/game/src/Adventure/AdvWorld.cs"
# The game's nuget.config points at ../tools/godot for the Godot SDK packages; use the real path.
sed -i 's#\.\./tools/godot#G:/H5Reborn/tools/godot#' "$WT/game/nuget.config"
dotnet build "$WT/game/H5Reborn.csproj" -v q -nodeReuse:false 2>&1 | grep -E " error |Build succeeded" | sort -u | head -5
