#!/bin/sh
# Combat UI audit pass 2: run a Ghidra script headless against our private project copy.
# Usage: tools/cuia4/ghidra.sh <Script.java> [args...]
ROOT=G:/H5Reborn
mkdir -p $ROOT/tools/cuia4/out
LOG=$ROOT/tools/cuia4/out/last_run.log
SCRIPT=$1; shift
MAXMEM=6G "$ROOT/tools/ghidra_12.1.4_PUBLIC/support/analyzeHeadless.bat" "$ROOT/tools/ghidra-projects/combatui4" H5 \
  -process H5_Game.exe -noanalysis -readOnly -scriptPath "$ROOT/tools/cuia4/scripts" -postScript "$SCRIPT" "$@" \
  </dev/null >"$LOG" 2>&1
grep -E "ERROR|Exception" "$LOG" | head -20
