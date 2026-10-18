#!/bin/sh
# Run a Ghidra script headlessly against the analysed H5_Game.exe project (read-only, no GUI).
# Usage: tools/re/ghidra.sh <Script.java> [script args...]
ROOT=G:/H5Reborn
LOG=$ROOT/tools/re/out/last_run.log
SCRIPT=$1; shift
MAXMEM=6G "$ROOT/tools/ghidra_12.1.4_PUBLIC/support/analyzeHeadless.bat" "$ROOT/tools/ghidra_projects" H5 \
  -process H5_Game.exe -noanalysis -readOnly -scriptPath "$ROOT/tools/re/scripts" -postScript "$SCRIPT" "$@" \
  </dev/null >"$LOG" 2>&1
grep -E "ERROR|Exception" "$LOG" | head -20
