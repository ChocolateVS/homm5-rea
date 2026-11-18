# hidden-desktop

Runs a command on a separate, never-shown Win32 desktop (`WinSta0\H5Test`) of the current session, so test
windows cannot flash, cover the screen, take focus or appear in the taskbar while someone uses the PC.
The GPU works there: Godot's Vulkan renderer and headless Chromium's ANGLE/WebGL 2 run normally, and a Godot
screenshot is byte-identical to the same run on the visible desktop.

```sh
tools/hidden-desktop/run.sh [--desktop NAME] [--timeout SECONDS] [--] COMMAND [ARGS...]
```

`run.sh` builds the launcher (net8.0, Windows only) on first use into
`%LOCALAPPDATA%/H5Reborn/hidden-desktop/<source hash>/` (outside the repo, shared by every checkout) and then runs
`hidden-desktop.exe` with the same arguments; from PowerShell call that exe directly.

- The desktop is opened if it exists, otherwise created; it is never switched to and lives while a run holds it.
- stdin/stdout/stderr are passed through; the exit code is the command's (124 on `--timeout`, 130 on Ctrl+C,
  125 on launcher errors such as a command that cannot be started).
- The command and everything it starts run in a kill-on-close job: Ctrl+C, the timeout, the command exiting, or
  the launcher being killed terminates the whole tree.
- `hidden-desktop.exe --list-windows [NAME]` lists the visible top-level windows of a desktop (`Default` is the
  user's) plus the foreground window, to check that nothing leaks.

Godot run (agent rules, Machine):

```sh
tools/hidden-desktop/run.sh "$GODOT" --path game --position -32000,-32000 --window-size=1920x1080 \
  --audio-driver Dummy -- --combat-vs-ai=0,3 --screenshot=out.png --screenshot-after=10
```

Headless Chromium (tools/web/harness) shows no window on its own; it can run through the launcher too
(`run.sh node smoke.mjs …`), GPU mode included.

Git Bash converts arguments that look like POSIX paths (`/c` becomes `C:/`) before they reach any Windows exe,
this one included; write `//c` or set `MSYS_NO_PATHCONV=1` as you would without the launcher.
