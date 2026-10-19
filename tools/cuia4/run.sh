#!/bin/bash
# Runs the private run worktree's game off-screen at 1920x1080. Usage: run.sh <timeout-s> <args...>
R="C:/Users/rghth/AppData/Local/Temp/claude/G--HoMM-5---Tribes-of-the-East/e85be553-54d1-4853-aa98-82c8dd346b0f/scratchpad/cui/run"
G=G:/H5Reborn/tools/godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe
t=$1; shift
timeout -k 5 "$t" "$G" --path "$R/game" --position -32000,-32000 -- --window-size=1920x1080 "$@" > G:/H5Reborn/tools/cuia4/out/run.log 2>&1
echo "exit $?"; grep -E "\[H5\]|ERROR|Exception" G:/H5Reborn/tools/cuia4/out/run.log | grep -v "^\s*at " | head -40
