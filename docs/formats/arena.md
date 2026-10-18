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

The battlefield itself is a 12×10 grid of 1-tile cells (14×14 for sieges with a fort). **Open
question:** whether `MountPoint` is the grid corner or centre, and which axis is 12 long. On
Grass_Small_01 the object-free area around (15,15) fits both readings; resolve with an in-game
screenshot or the exe (search the `MountPoint` consumer).

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
13 effects, `Rampart_Sky` light). It needs the geometry and Granny decoders from the other
workstreams to render.

## Engine root table

`Roots/roots.xdb` (and `roots-twin.xdb`) is a positional list of 162 hrefs the engine loads at
startup: default lights, the interface arenas (incl. main menu), combat grid selection material
(`UI/CombatArena/Grid/Select.xdb`), camera sets, `UI/uiconsts.(UIGameConstsH5).xdb`, the font table,
hero attributes UI, duel presets, adventure shared groups, dwelling flyby scenes... The slot index
is the meaning; names must be recovered from the exe. Treat it as the entry point for
"what does the engine load by default".
