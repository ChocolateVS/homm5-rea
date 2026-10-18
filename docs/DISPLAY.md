# Display: resolution, UI scale, ultrawide

## Defaults
- Full screen at **1920x1080** (the desktop size on smaller monitors). The original also started full screen
  (`gfx_fullscreen=1`). `project.godot` uses a 1920x1080 window and no stretch: the UI is laid out in real pixels.
- Settings live in `user://video.cfg` (`game/src/Settings/VideoSettings.cs`) and are edited in the stock Options
  screen (`UI/GameOptions`, Video & Audio category), applied immediately.

## Resolution and window mode
- Modes: full screen, windowed, borderless window (covers the monitor).
- Godot cannot switch the monitor's video mode. A full-screen resolution below the desktop size renders the
  game at that size and upscales it (`ContentScaleMode.Viewport`), which is what a mode switch looked like on
  LCDs. Screens call `VideoSettings.ApplyScaling()` instead of setting content scaling themselves.

## UI layout (original rules)
- Screens are authored for 1024x768. Each `WindowScreen` places that canvas per axis (`HorAlign`/`VerAlign`,
  set by the 3.1 archive): `ALIGN_SCREEN` stretches it to the screen and children re-anchor by their own
  alignments (panels in corners/edges, centred windows centred); `ALIGN_CENTER` centres the 1024x768 canvas;
  `ALIGN_LOW_END`/`ALIGN_HIGH_END` pin it. Nothing is stretched. See `docs/formats/ui.md`.

## UI scale (extension, user-requested)
- The original UI is pixel-sized and would be tiny at 1440p/4K. `UiScreen` lays the window tree out at
  `screen / scale` with the original rules and scales the laid-out tree uniformly; input hit-testing follows
  the transform.
- Auto (default): the largest step not above `height / 1080` from {1, 1.25, 1.33, 1.5, 1.75, 2, 2.5, 3, 4}:
  1080p → 1.0, 1440p → 1.33, 2160p → 2.0. Below 1080p the scale stays 1.0 (the original's native size).
  Override in Options ("Interface scale") or with `--ui-scale=`.
- Integer scales draw the bitmap fonts and textures with nearest filtering (pixel-exact); fractional scales use
  linear filtering.

## 3D cameras and wide screens
- Godot cameras keep the vertical FOV (`KeepAspect.Height`, Hor+): wider screens show more at the sides.
- Combat follows the exe's projection (`00cf7c30`): horizontal FOV up to 4:3-or-wider, 4:3 vertical extent on
  taller screens. Extension: above 16:9 it keeps the 16:9 vertical extent (Hor+), so 4:3 to 16:9 match the
  original and 21:9/32:9 add view at the sides.
- Full-screen pictures and movies are letterboxed/pillarboxed, never stretched (`Boot`, `MoviePlayer`).

## Testing
- `--window-size=WxH` opens an exact borderless window (not clamped to the monitor) for screenshots;
  `--resolution`/`--position` keep the window as given and skip the saved window mode.
