#!/bin/sh
# Audio workstream: run a Ghidra script headlessly against a private copy of the analysed project.
# Usage: tools/audiore/ghidra.sh <Script.java> [script args...]
ROOT=G:/H5Reborn
LOG=$ROOT/tools/audiore/out/last_run.log
SCRIPT=$1; shift
MAXMEM=6G "$ROOT/tools/ghidra_12.1.4_PUBLIC/support/analyzeHeadless.bat" "$ROOT/tools/ghidra-projects/audio" H5 \
  -process H5_Game.exe -noanalysis -readOnly -scriptPath "$ROOT/tools/audiore/scripts" -postScript "$SCRIPT" "$@" \
  </dev/null >"$LOG" 2>&1
grep -E "ERROR|Exception" "$LOG" | head -20
