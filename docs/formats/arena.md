# Combat arenas and 3D scenes

## Combat battlefield assembly

1. `GameMechanics/RefTables/CombatArenaTypes.xdb` (`CombatArenasTable`) maps terrain types to
   lists of arenas per army size (`small` / `average` / `large`) and variant
   (`OldArena`, `newArenas`, `waterArenas`, `deepWaterArenas`).
2. Each arena is an `AdventureFlybyScene` (`GameMechanics/CombatArenaTypes/*.xdb`,
   `Scenes/CombatArenas/*.xdb`):
   * `Scenery` → map descriptor `Maps/CombatArenas/<name>/map.xdb` (an `AdvMapDesc`, the same type
     as adventure maps).
   * `MountPoint { Pos {x,y}, Rot }` — where the combat grid is mounted on that map, in tiles
     (e.g. Beach_Grass_Small_01 (29,35) on a 72×72 map; Grass_Small_01 (15,15) on 40×40).
   * `AdditionalHeight`, `Music`, `AmbientSoundSet`, `TextFormatFileRef`.
   * `ObstaclesGroup` → `ArenaObstaclesGroup { FixedObstacles, links → ArenaObstacle }`
     (random rocks/trees placed on combat cells).
   * `CamerasSet` → `CamerasSet { CombatCameras → CombatCamera }`.
   * `MeleeAttackPoints` (unused in stock arenas).
3. The map descriptor supplies:
   * `TileX/TileY`, `GroundTerrainFileName` → terrain (terrain.md), `tiles` list (layer tile types).
   * `objects`: `AdvMapStatic` items with `Pos {x,y,z}` (tiles), `Rot` (radians), `Floor`,
     `Shared` → `(AdvMapStaticShared)` (model, footprint). These are the scenery around the field.
   * `GroundAmbientLights` → `AmbientLight` (below), `ReflectiveWater`.

### Combat grid placement (resolved)

The playable battlefield is a 12×10 grid of 1-tile cells (14×14 sieges not yet examined).
**Cell (cx, cy), cx ∈ [0,12), cy ∈ [0,10), is map tile (Mount.x + cx, Mount.y − 11 + cy)**, i.e. the
grid covers tiles x ∈ [Mount.x, Mount.x+12), y ∈ [Mount.y−11, Mount.y−1). The long axis (12) is map
+X; the armies face each other along it (attacker at cx = 0). Implemented in
`game/src/Arena/ArenaGrid.cs`. Evidence (all 63 `Scenes/CombatArenas/*.xdb`):

* **Axis/extent — fixed obstacles.** `ArenaObjects/BoatArena/BoatArenaBlocked.xdb` blocks cells
  x 6..9 × y {1,2,5,6,9,10}; `AbyssArena` blocks x 5..10 × y 1..4, 7..10. Both are symmetric about
  x = 7.5, y = 5.5, so the engine's internal grid is 16×12 with playable cells x 2..13, y 1..10
  (internal = playable + (2, 1)); the 4/6 blocked middle columns are the water/abyss between two
  armies standing on the left and right. Hence 12 along X.
* **Position — terrain flatness.** The playable area is flattened in every arena. Scanning 13×11
  vertex windows at offsets from MountPoint, the summed height std-dev over all arenas is minimal at
  window origin (Mount.x, Mount.y−11): 0.09 total, vs 0.15/0.20 at x∓1 and ≥0.89 elsewhere.
* **Position — scenery.** Counting `AdvMapStatic` blocked tiles inside candidate rectangles: corner
  hypotheses extending +X/−Y score 4–15 blocked tiles over all arenas; centred or other-direction
  rectangles score 1769–2937.

### Scenery placement

`AdvMapStatic.Pos` is in tiles (70% integers, the rest free placement). A model's origin sits at the
centre of footprint tile (0,0) of its `blockedTiles`: footprint centroid vs model bounding-box centre
over 473 objects has median error 0.49 for "origin at tile centre" and 1.0 for the alternatives.
So world position = (Pos.x + 0.5, Pos.y + 0.5, terrain height + Pos.z); `Rot` is radians about +Z.

### Combat camera

`Cameras/CombatArena/CombatArena.xdb` is the only combat-arena camera (others are cinematics):
`Pos (−5.68, −5.09, 9.81)`, `Rot (−30, −66.8, 0)`, `Rod 50`, `FOV 70`. Current reading: Pos is the eye
offset from the battlefield centre (map space) looking at the centre, FOV vertical. This gives an
angled view at ~52° with the whole grid in frame and clear sightlines on all arenas tested (an eye
offset from the internal grid origin instead lands inside the treeline). **Unreconciled:** the Rot
angles don't match this look direction, and the main-menu camera (below) uses Pos-as-eye with an
explicit yaw. Needs a screenshot of the original battle screen to settle.

### Grid overlay

`UI/CombatArena/Grid/{Inactive,Select,affected,aimable}[_2].xdb` are `AM_OVERLAY` materials
(256² cell textures with a bright border and ~0.73 alpha fill). `AM_OVERLAY` (also 239 model
materials: ground decals) is rendered as an overlay blend of the texture over the framebuffer,
weighted by alpha (`game/src/Render/Overlay.gdshader`).

## Lighting — `Lights/_(AmbientLight)/**`

`AmbientLight`: `LightColor` (sun), `AmbientColor`, `ShadeColor`, `IncidentShadowColor`,
`ShadowColor`, `ParticlesColor`, `GlossColor`, sun direction `Pitch`/`Yaw` (degrees), shadow
`ShadowPitch/Yaw`, `MaxShadowHeight`, `Sky` (cube texture), `SkyDome` (model), fog
(`FogColor`, `FogStartDistance`, `FogDistance`, `DistanceFog` / `HeightFog` objects), vapour,
clouds (`CloudTex`, `CloudSize`, `CloudDir`, `CloudSpeed`), `SunFlares`, `Rain`, `DepthOfField`,
`Whitening`. Maps to Godot `DirectionalLight3D` + `WorldEnvironment` (ambient, fog, sky).

## Cameras

* `_(CamerasSet)/Default.(CamerasSet).xdb` lists `CombatCamera` objects: event-triggered cinematic
  cameras (`ActionType` CAMERA_ACTION_SPELL_CAST / RANGED / ..., actor/target filters by creature
  visual, `SpellID`), each with `Tracks` of `Segments` → `Cameras/**.(Camera).xdb` plus
  position/rotation modes (`CAMERAPOS_ACTOR`, `CAMERAROT_ACTOR_POINT`) and key moments.
* Static/initial cameras: `initialCamPos { Pos, Rot (deg), Rod (distance), FOV, ClipDistanceMin/Max }`.
* Animated cameras: a camera *model* + `AnimSet` (`cameraFlyupRole`), i.e. a Granny-animated bone.

## Interface 3D scenes — `ArenaDesc`

Screens with 3D backdrops (`Arenas/Interface/{MainMenu,Fort_screen,Hero_hire,Hero_screen,Magic_Guild,NewFort_screen}`)
use `ArenaDesc`:

* `objects` → `ArenaEnvObject { placement (64-byte hex = 4×4 float matrix; all-zero = as exported),
  Model, AnimSet }` — terrain meshes, walls, the animated Haven troops, dead bodies, props.
* `effects` → `Effect` + optional `Locator`.
* `initialCamPos`, `cameraFlyupRole` (main menu: `_(AnimSet)/Interface/MainMenu/Mainmenu_H5A2_Camera`),
  `AmbientLights`, `AmbientSound`, `Music`, `Wind`.

The main menu backdrop is `Arenas/Interface/MainMenu/MainMenu.(ArenaDesc).xdb` (35 objects,
13 effects, `Rampart_Sky` light), rendered statically by `game/src/Arena/MainMenuBackground.cs`.

**ArenaDesc frame:** placement matrices and `initialCamPos` are in mesh space with **Y negated**
(objects with all-zero placement are exported in place). `initialCamPos.Pos` is the eye; the
horizontal view direction is (sin yaw, −cos yaw) in that frame with yaw = `Rot.y`, pitch = `Rot.x`.
With this reading the camera stands in front of the troops facing them and the far `Background`
card lies behind them; without the Y flip the camera faces away from every object.

## Engine root table

`Roots/roots.xdb` (and `roots-twin.xdb`) is a positional list of 162 hrefs the engine loads at
startup: default lights, the interface arenas (incl. main menu), combat grid selection material
(`UI/CombatArena/Grid/Select.xdb`), camera sets, `UI/uiconsts.(UIGameConstsH5).xdb`, the font table,
hero attributes UI, duel presets, adventure shared groups, dwelling flyby scenes... The slot index
is the meaning; names must be recovered from the exe. Treat it as the entry point for
"what does the engine load by default".
