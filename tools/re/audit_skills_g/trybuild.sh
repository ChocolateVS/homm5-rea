#!/bin/sh
# Skills audit G: build HEAD + my new files + my patches in a scratch worktree; optional xunit filter or --test.
ROOT=G:/H5Reborn
WT=C:/Users/rghth/AppData/Local/Temp/claude/G--HoMM-5---Tribes-of-the-East/e85be553-54d1-4853-aa98-82c8dd346b0f/scratchpad/skg-try
cd "$ROOT" || exit 1
git worktree remove --force "$WT" >/dev/null 2>&1; rm -rf "$WT"
git worktree add -f --detach "$WT" HEAD >/dev/null 2>&1 || { echo "worktree failed"; exit 1; }
for p in src/H5.Combat/Battle.SkillAuditG.cs tests/H5.Combat.Tests/AuditSkillTestsG.cs; do cp "$ROOT/$p" "$WT/$p"; done
for pf in stats:Battle.Stats spells:Battle.Spells abil:Battle.Abilities skillaudit:Battle.SkillAudit attack:Battle.Attack; do
  s=${pf%%:*}; f=${pf#*:}; python -I "$ROOT/tools/re/audit_skills_g/patch_$s.py" "$WT/src/H5.Combat/$f.cs" || { echo "patch $s failed"; exit 1; }
done
cd "$WT"
B=$(dotnet build game/H5Reborn.csproj -nodeReuse:false 2>&1 | grep -E " error |Build succeeded" | sort -u | head -10); echo "$B"
case "$B" in *error*) S=1 ;; *) S=0 ;; esac
if [ $S -eq 0 ] && [ "$1" = "--test" ]; then dotnet test -nodeReuse:false 2>&1 | grep -E "Passed!|Failed!| error |\[FAIL\]"
elif [ $S -eq 0 ] && [ -n "$1" ]; then dotnet test tests/H5.Combat.Tests -nodeReuse:false --filter "$1" 2>&1 | grep -E "Passed!|Failed!| error |\[FAIL\]|Error Message|Assert|Expected|Actual" | head -40; fi
cd "$ROOT"; git worktree remove --force "$WT" >/dev/null 2>&1
