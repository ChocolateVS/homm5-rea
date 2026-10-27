# Rules for helper agents (coordinator-maintained)

Project: H5Reborn, a faithful Godot 4.7 .NET (C#) rewrite of Heroes of Might and Magic V: Tribes of the East.
Target: the stock 3.1 engine (G:\HoMM 5 - Tribes of the East, read-only) + the user's MMH 5.5 install
(G:\HoMM 5 - Tribes of the East (5.5): MMH55 data + Lua scripts + the 16-byte PEST patch; the MMH55 DLL
patches and Quantomas AI are out of scope). Exactness comes from the decompiled exe (Ghidra 12.1.4 headless
under tools/; copy an existing project from tools/ghidra-projects/ as your own) and the original data.
Never invent behaviour: anything not traced is a GAP row in the relevant ledger (docs/rules/AUDIT_*.md).

## Git
- Commit small and often, via a private index built from the CURRENT HEAD at commit time:
  `GIT_INDEX_FILE=<scratch>/idx git read-tree HEAD; GIT_INDEX_FILE=<scratch>/idx git add <paths>; GIT_INDEX_FILE=<scratch>/idx git commit`.
- Move main ONLY with a compare-and-swap, never with plain `git commit` on a tree read earlier:
  `OLD=$(git rev-parse main)`; build the tree from `$OLD` + your files (`GIT_INDEX_FILE=… git read-tree $OLD; … git add <paths>; T=$(… git write-tree)`);
  `NEW=$(git commit-tree $T -p $OLD -F msg)`; `git update-ref refs/heads/main $NEW $OLD`. If update-ref fails (main moved),
  redo from the new main. Then `git diff --stat $OLD $NEW` must list only your files — anything else means you reverted someone.
- Explicit paths only; never `git add -A`; never stage in the main index; never rewrite or reset other commits.
- If HEAD moved since your test run, re-apply your patch on the new HEAD (3-way) and re-check. Verify with
  `git show --stat` that the commit only touches your files. Other helpers' WIP may be in the tree — never commit it.
- Before each code commit: a clean checkout (worktree under your scratch dir, no junctions, removed afterwards)
  builds the solution and game/H5Reborn.csproj, and `dotnet test H5Reborn.sln` passes.
- Do not push (the coordinator pushes) unless your task says so.
- Commit messages end with:
  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>

- Known, harmless: `git fsck`/auto-gc report tree 575ae451 / object 16faaf32 ("scripts" blob vs tree). That object is only in
  old pre-repair commits kept by the local reflog; main and origin are clean. Don't report it; use `git -c gc.auto=0` if gc is in the way.

## Machine
- The user uses this PC. Godot only off-screen: `--position -32000,-32000 --window-size=1920x1080`.
- Never launch H5_Game.exe or any mod exe; never capture the user's screen.
- Kill only processes you started (no `taskkill /IM`).
- Portable code (Linux too): use the InstallPath helper for game-folder files, no backslash paths.

## Budget
Be economical with context. Commit each verified step promptly. If told to stop, commit the current step and stop.
Final report: commits, ledger counts, remaining gaps — concise.
