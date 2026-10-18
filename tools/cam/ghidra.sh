#!/bin/sh
# Run a Ghidra script headlessly against the camera workstream's private copy of the analysed project.
# Usage: tools/cam/ghidra.sh <Script.java> [script args...]; output in tools/cam/out/last_run.log
ROOT=G:/H5Reborn
LOG=$ROOT/tools/cam/out/last_run.log
SCRIPT=$1; shift
MAXMEM=6G "$ROOT/tools/ghidra_12.1.4_PUBLIC/support/analyzeHeadless.bat" "$ROOT/tools/ghidra-projects/camera" H5 \
  -process H5_Game.exe -noanalysis -readOnly -scriptPath "$ROOT/tools/re/scripts" -postScript "$SCRIPT" "$@" \
  </dev/null >"$LOG" 2>&1
grep -E "ERROR|Exception" "$LOG" | head -20
