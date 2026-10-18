#!/bin/sh
# AI workstream: run a Ghidra script headlessly against a private copy of the analysed project.
# Usage: tools/aire/ghidra.sh <Script.java> [script args...]
# Shared scripts are synced from tools/re/scripts and tools/advre/scripts (scriptPath takes one directory).
ROOT=G:/H5Reborn
LOG=$ROOT/tools/aire/out/last_run.log
SCRIPT=$1; shift
cp -n "$ROOT"/tools/re/scripts/*.java "$ROOT"/tools/advre/scripts/*.java "$ROOT/tools/aire/scripts/" 2>/dev/null
MAXMEM=6G "$ROOT/tools/ghidra_12.1.4_PUBLIC/support/analyzeHeadless.bat" "$ROOT/tools/ghidra-projects/ai" H5 \
  -process H5_Game.exe -noanalysis -readOnly -scriptPath "$ROOT/tools/aire/scripts" -postScript "$SCRIPT" "$@" \
  </dev/null >"$LOG" 2>&1
grep -E "ERROR|Exception" "$LOG" | head -20
