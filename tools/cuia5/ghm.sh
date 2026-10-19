#!/bin/sh
# Like ghidra.sh but passes raw args, so several "-postScript S args..." can run in one session.
ROOT=G:/H5Reborn
LOG=$ROOT/tools/cuia5/out/last_run.log
MAXMEM=6G "$ROOT/tools/ghidra_12.1.4_PUBLIC/support/analyzeHeadless.bat" "$ROOT/tools/ghidra-projects/combatui5" H5 \
  -process H5_Game.exe -noanalysis -readOnly -scriptPath "$ROOT/tools/cuia5/scripts" "$@" </dev/null >"$LOG" 2>&1
grep -E "ERROR|Exception" "$LOG" | head -20
