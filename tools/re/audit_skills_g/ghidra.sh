#!/bin/sh
# Audit workstream: run a Ghidra script headlessly against a private copy of the analysed project.
# Usage: tools/re/audit/ghidra.sh <Script.java> [script args...]
ROOT=G:/H5Reborn
LOG=$ROOT/tools/re/audit_skills_g/out/last_run.log
SCRIPT=$1; shift
MAXMEM=6G "$ROOT/tools/ghidra_12.1.4_PUBLIC/support/analyzeHeadless.bat" "$ROOT/tools/ghidra-projects/audit_skills_g" H5 \
  -process H5_Game.exe -noanalysis -readOnly -scriptPath "$ROOT/tools/re/scripts" -postScript "$SCRIPT" "$@" \
  </dev/null >"$LOG" 2>&1
grep -E "ERROR|Exception" "$LOG" | head -20
