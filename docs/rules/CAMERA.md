# Combat camera, track cameras and combat feel — H5_Game.exe findings

All addresses are H5_Game.exe (TotE 3.1, GOG), analysed headless in Ghidra 12.1.4
(private project `tools/ghidra-projects/camera`; runner `tools/cam/ghidra.sh`; helper scripts
`tools/re/scripts/RttiVtables.java`, `ReadData.java`). Implementation: `game/src/Combat/CombatCamera.cs`,
`game/src/Combat/CinematicCameras.cs`.

## Classes

RTTI: `CCamera` (vtable 00e44224), `CCameraOperator` (00e4493c, update 007d4ee0), `CArenaCameraConfigurator`
(00e446b0: [7] 007d3730 limits, [8] 007d3990 default pose), `CTrackCameraOperator`, `CCombatCameraAntiRepeater`,
`CAnimatedCamera`, `CDirectCameraOperator`, `CWatchCameraOperator`.

## Camera state and projection

`SCameraPos` = 10 floats: Rod, Pitch, Yaw, Roll (radians), FOV, near, far, Anchor xyz.
* Orientation: q = qZ(yaw)·qX(pitch)·qY(roll), half angles (DAT_00e0ab70 = 0.5), axes X/Y/Z (DAT_00f3fc28..)
  (007cd600 / 007cd120 / 007cd4f0). Forward = rotated +Y; yaw 0 looks north (+Y), positive yaw turns
  counter-clockwise seen from above; negative pitch looks down.
* Eye = Anchor − Rod·forward (007cd770).
* Projection 00cf7c30: t = tan(FOV·π/360); with H/W ≤ 0.75 (DAT_00e34c5c) the FOV is **horizontal**;
  taller screens keep tan(v/2) = 0.75·t (the vertical extent of 4:3).

## Combat camera setup

Combat screen init `00544100` (only caller: combat screen constructor 0054c2f0) creates the CCamera
(007d0330), the operator (007d6ee0) and a `CArenaCameraConfigurator` with **limits on** (DL = 1), applies the
default pose (`+0x20` → 007d3990) and the limits (`+0x1c` → 007d3730). Nothing re-poses the camera for the
tactics phase or at combat start (`OnJog-StartCombat` setup 005464a0 has no camera calls; no ±π/2 or π
immediate is written in the combat screen code). Other configurator users are the creature tutorial (flyby)
screen 006a5450 and the replay screen 007aae60. **The tactics phase therefore uses the same default camera**;
the manual's "behind the army" tactics screenshot is a player-rotated view at the yaw limit.

`combat_camera_limits on/off` (console, 00543e50 → 00543430) toggles the limits.

### Default pose (007d3990)

| field | value |
|---|---|
| Anchor | arena centre (arena `+0x74`) with z − 2.0 (DAT_00e44608) |
| Pitch / Yaw / Roll | −π/4 (0xbf490fdb) / 0 / 0 |
| FOV / near / far | 54 / 1 / 500 |
| Rod | 007d34c0 (below) |

The generic default 35 / 1 / 1000 (DAT_00f33900, also in the combat screen constructor 0054ba40 and operator
ctor 007d6990) is overwritten by this pose.

### Rod tables (007d34c0 default, 007d3650 max, 007d35a0 min)

Switch on the arena's internal grid width W (cases 11..24, byte table at 7d3584): value in cells, ×2 (2 world
units per tile).

| W | default | max | min |
|---|---|---|---|
| 11 | 11 | 11 | 6.6 |
| 14 | 15 | 17.5 | 7 |
| 16 (normal field) | 21 | 21 | 5 |
| 18, 20 (siege) | 24 | 28 | 14.4 |
| other | 30 | 30 | 18 |

Default and max are multiplied by √(H/W·4/3) when that exceeds 1 (screen from FUN_00a3f7e0, DAT_00e44660 = 4/3).
So the battle starts **fully zoomed out**.

### Limits (007d3730, limits on)

* Rod [min, max] as above; Pitch [−60°, −35°] (0xbf860a92, 0xbf1c61aa); Yaw [−90°, +90°] (±π/2).
* Pan rectangle for the anchor: x0 − 1 … x0 + 2(W−4) + 1 and y0 − 2(H−1) − 1 … y0 − 1 (origin from the arena,
  i.e. the field ±1 unit); anchor z: centre.z − 2 … centre.z + W.
* Limits off: rod 0..1000, pitch ±90°, yaw unlimited.
* Soft and hard limits are the same struct (`+0x3c` and `+0x40` with one argument), so the soft-limit
  spring-back of 007d4340 never acts in combat.

### Extension: "Extended combat camera" (H5Reborn option, off by default)

User-requested, not in the original. Game options → Display → "Extended combat camera" (profile var
`h5r_combat_extended_camera`, default 0; dev switch `--combat-extended-camera`). Off = the stock limits above.
On: yaw unlimited (wrapped to ±π), pitch −85°..−10°, rod 0.5 × min .. 1.6 × max of the table above, pan
rectangle grown by 8 units on every side. Speeds, glide and the default pose are unchanged.

## Hand camera input (CCameraOperator::Update 007d4ee0)

* Frame step capped at 100 ms.
* Inputs: camera_rotate(+_mouse), camera_pitch(+_mouse), camera_forward(+_mouse), camera_strafe(+_mouse),
  camera_zoom(+_mouse), camera_lift (registered in ctor 007d5990). Sign table 007dd490: rotate +1, pitch −1,
  forward/strafe +1 (stock, non-inverted).
* Mouse axis lock: pitch is dropped when |pitch|·10 < |rotate|, and vice versa.
* Zoom input clamped to ±5 and ignored above 100.
* Speed factor f = 1 + (rod − min)/(max − min) (007d49e0): ×1 zoomed in, ×2 zoomed out.
* Pitch += input · 1; yaw −= input · f (clamped); rod += (|rod|·5 + 1)·0.1·input; anchor z += lift·15
  (DAT_00f3fe3c); pan: anchor += ground-forward·(input·10)·f, ground-right likewise (007d45a0, 007d4ae0,
  DAT_00e22820 = 10). Speeds come from SCameraLimits defaults DAT_00f33900 (1, 4, 1.2, 1, 5, 0.1, 1, 1, 1).
* The rendered pose glides to the target (007cccb0, FUN_007ccc10: target + (current − target)·base^(dt)):
  rod base 1/300 per second (stop 0.02), pitch/yaw/roll base 1/9000 (stops 0.01 / 1e−5; yaw wrapped to ±π),
  anchor base 1.111e−4.

Bindings (profiles/default_profile/input_a2.cfg): arrows forward/strafe, PG_DOWN/PG_UP zoom, CTRL+arrows
pitch/rotate, wheel zoom, MOUSE_BUTTON1 (= DirectInput rgbButtons[1] = right button; descriptor table
00e8d090) + drag rotate/pitch, MOUSE_BUTTON2 (middle) or CTRL+right drag pan, combat_screen INSERT/DELETE lift.
There is no reset key in the combat sections (HOME is adventure/town only). The OnJog-Camera1/2/Left/Right/Ways
buttons belong to the combat **replay** screen only (UI/CombatReplayScreen/MovieJogDial).

Edge scrolling (combat screen 007ad410 → 007d2c80, 007d2d90): screen-edge direction from the cursor manager;
speed ramps at combat_scroll_speed (1) per second to combat_max_scroll_speed (3), scaled by
(1 + zoom·combat_scroll_speed_zoom_factor (3)), ×10 along ground-forward/right.

## Track ("cinematic") cameras

setvar `camera_cinematic` (stock profile 1; 2 = PA2 set); `combat_camera_random_track` (default −1 = random);
`combat_camera_antirepeater` (1); arrow stop/brake distance 4 / 18 (manager init 007dbe40).
* Camera set: the arena's `(ArenaDesc) CamerasSet`, then DBID_DEFAULT_CAMERA_SET =
  `_(CamerasSet)/Default.(CamerasSet).xdb` (UIGameRoot cameraSets; 007d8a60, 007da700).
* Matching (007da700): first `(CombatCamera)` in list order with ActionType ANY or equal, actor and target
  filters (007da530: 0 NO_MATTER, 1 ABSENT, 2 ANY, 3 SPECIFIC visual list, 4 HERO, 5 MACHINE, 6 BUILDING
  (+BuildingType), 7 CREATURE, 8 CREATURE_1x1, 9 CREATURE_2x2; plus Zone = inclusive internal-grid rectangle,
  007d89b0), SpellID, AzimuthRange (007d8420: a = deg(facing actor→point) − 90, pass when
  wrap360(a − Min) ≤ wrap360(Max − Min)).
* Not IsForced → requires camera_cinematic == 1 and the anti-repeater (007d9280): with counter c and MaxCount M
  (`ArenaObjects/_(CombatCamera)/CameraAntiRepeat.xdb`, all 5) the camera plays with probability M/(c + M + 1);
  c increments on every attempt (per battle).
* Track: random (007d8520). Segments: static `(Camera)` (007d99d0): eye = base + Rz(angle)·Pos, rod 0,
  pitch = Rot.x, yaw = Rot.y + angle, FOV from the camera; angle by RotType (ACTOR facing, ACTOR_POINT =
  facing towards the target point 007d8e40, TARGET facing, TARGET_POINT); base by PosType (ACTOR / TARGET
  position, ABSOLUTE = arena origin). Each segment starts at KeyMoment + Offset; the track ends at
  EndMoment + EndOffset (007d8b30). Stock data uses only EVENT_BEGIN, ATTACK_START and EVENT_END.
* Facing convention: angle 0 faces −Y; direction = (sin a, −cos a) (= Godot rotation.y of the unit).

## Combat feel

* Move clips carry their forward travel in a body bone (Footman move: Hips y −0.13 → −4.69 over 0.8 s =
  MovementSpeed 5.7). Granny root-motion fields are unused (RootMotion null, LoopTranslation 0). The creature
  moves at (BasicSkelAnim) MovementSpeed × SpeedFactor and the cycle plays in place: the client extracts the
  bone's linear drift (CharacterBuilder.ExtractRootMotion) and plays clips at SpeedFactor.
* Flying signs: E_GOLD_OUTLINE / E_BLACK_OUTLINE (UIGameRoot) name brush textures — Light.xdb (32×32 warm
  point, CONVERT_TRANSPARENT_ADD) and BlackGlow.xdb (7×7 solid black, soft rim) — stamped around the glyph
  shapes of the ID_FLYING_SIGN_FORMAT text (sign creation 0054a5c0 → 00670cb0, text element 006707c0,
  effect list 00570990). Text style `<flying_signs>` = `<font face=System size=24fx>`.

## Open points

* Mouse axis normalisation: the input layer accumulates DirectInput deltas × bindconfigure coefficients
  (MOUSE_AXIS_X/Y 1.2, MOUSE_AXIS_Z 160; 00492e20) in fixed point; the final scale to command units wasn't
  traced. The client uses 0.0012 per pixel and 0.75 per wheel notch.
* Held keys are assumed to contribute level × frame time.
* The arena accessor behind the configurator's `+0x44` / `+0x74` / `+0x2c` was not identified; the internal
  width W = field width + 4 and the field centre/corners are used.
* Static camera Rot: the exe builds a quaternion from Rot.x/y/z about X/Y/Z before converting; the data only
  fits Rot.x = pitch, Rot.y = yaw (Maya export), which the client uses.
* Not reproduced: animated (AnimSet) track cameras (3 in stock data), the arrow-following camera
  (CCombatEventMissileMove → 007da180), camera shakers, `combat_time_factor` / dynamic-battle speed options.
* Signs: whether stamps combine by max or by sum (max used — keeps the red digits readable).
