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
+X; the armies face each other along it (attacker at cx = 0, on the left of the battle view). Implemented in
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

### World scale (2 units per tile)

Models are authored at **2 world units per map tile**: siege building models span exactly 2 units
per footprint tile (towers 2×2 → ~4.7 units, gate 2×4 → ~8.7; the two side towers, 14 tiles apart, are
26.6 units apart), and the combat camera's `Rod` (50) equals the camera distance fitted to the manual's
combat screenshots (24.8 cells × 2). Terrain heights are in world units. The client maps tiles with
`ArenaGrid.TileSize = 2`; at 1 unit per tile creatures and scenery came out twice too large relative to
the grid (the manual shows creatures about 0.6 of a cell tall).

### Combat camera

`Cameras/CombatArena/CombatArena.xdb` is the only combat-arena camera (`DefaultCamera` of
`_(CamerasSet)/CombatArenas.xdb`): `Pos (−5.68, −5.09, 9.81)`, `Rot (−30, −66.8, 0)`, `Rod 50`, `FOV 70`.
The exe's conversion of Pos/Rot into its orbit camera (Rod, Pitch, angles, Anchor) is not traced, so the client
uses views fitted to the stock manual's screenshots (Game Manual 3.0.pdf p.14; docs/rules/EXE_FINDINGS.md
"Deployment axis and combat views"):

* **Battle view** — looks north (towards higher rows, the 12-cell axis across the screen, attacker on the left
  as the manual's text says): pitch 46.2°, vertical FOV 31.2°, eye 24.8 cells (≈ Rod 50 at 2 units/cell) from a
  target 4.27 cells in from the near edge.
* **Tactics view** — behind the deploying army, looking along the 12-cell axis (attacker seen from the west,
  defender from the east): pitch 43.4°, vertical FOV 35° (the combat screen's default FOV in the exe), eye
  20.2 cells from a target on the centre line 5.0 cells in from that army's edge. The enemy army is hidden until
  combat starts; the camera then glides to the battle view.

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


## Siege battlefields

Rendering data for sieges lives on `BattleState.Siege` (`H5.Combat.SiegeState`, null for field battles) together
with the static layout in `GameDatabase.TownCombat[town]` (`H5.Core.Data.TownCombatDef`):

| Field | Meaning |
|---|---|
| `TownCombatDef.ArenaDesc` | (ArenaDesc) of the siege arena (e.g. `/Arenas/CombatArena/NewGrassarena_siege/…`) |
| `TownCombatDef.Scenery` | AdvMapDesc of the siege terrain (`/Maps/CombatArenas/Grass_Siege_Big_01/…`) |
| `TownCombatDef.MountX/Y/Rot` | Mount point of the battlefield on that terrain (as for field arenas) |
| `ArenaBuildingDef.Object` | (ArenaModObject) with the building's model and damage states |
| `ArenaBuildingDef.Shooter` / `Shot` | Character standing on a tower and its projectile (Shot xdb) |
| `ArenaBuildingDef.HitEffect/HitSound` | Played when the building is hit |
| `ArenaBuildingDef.DestroyEffects` | Effects per `RuinLevel` (0 = first collapse stage, 1 = final) |
| `SiegeBuilding.DefIndex` | Index of the building's `ArenaBuildingDef` |
| `SiegeBuilding.Anchor` | `Pos` of the building in **field** cells (internal − (2, 1)); may lie outside the field |
| `SiegeBuilding.Blocked` / `BlockedDestroyed` | Field cells blocked while intact / once destroyed |
| `SiegeBuilding.Health/MaxHealth/Destroyed` | Current state; `BuildingDamaged` events report every change |
| `SiegeState.Moat` | Moat cells (field coordinates); `SpentMines` lists exploded Academy mines |

Siege fields are 14×14 field cells, i.e. an internal grid of 18×16 with the same 2-column / 1-row margin as the
12×10 field (16×12). Use the field-battle placement rule with the siege MountPoint: field cell (cx, cy) is map
tile (Mount.x + cx, Mount.y − (H + 1) + cy) with H = 14 (assumed by analogy with the 12×10 rule, not yet
verified on screen). Common layout (identical for all eight towns, field cells): walls x 7–9 at y 1–4 and 9–12,
gate x 7–8, y 5–8 (open centre rows 6–7 once destroyed), moat x 6–8 (no moat in front of the gate),
side towers x 9–10 at y 0 and 13, central tower x 13, y 6–7. Tower units have `CreatureId` `SIEGE_<TYPE>` and an
off-field `Pos`; draw them with their building's `Shooter` instead.
