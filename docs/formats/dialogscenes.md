# Dialog scenes — `DialogScenes/**/DialogScene.xdb`

Map scripts call `StartDialogScene("/DialogScenes/A2C1/M1/S1/DialogScene.xdb#xpointer(/DialogScene)", callback, saveName)`.
The exe (`005de0d0`) looks the scene up and reports `Dialog "%s" is not exists` when it is missing
(the stock scripts reference one such scene, `DialogScenes/A2C3/M2/S2`, which was never shipped);
the callback still runs. UIGameRoot binds `N_DIALOG_SCENE_SCREEN_ID` to `UI/DialogueScreen` (speaker name
`HeroName`, line `HeroText`, `SkipScene` button sending `skip_scene`). 35 stock scenes are used by the
shipped scripts (A2C0–A2C3, A2Single); `Presentation`, `For_KRI`, `FX_Test`, `Res` and the `A2PWL` stub
point at unshipped test maps.

Loader: `src/H5.Assets/Scenes/DialogSceneData.cs` (`DialogSceneLoader`). Player: `game/src/Cinematics/DialogScenePlayer.cs`.

## Scene file

| Field | Meaning |
|---|---|
| `Map` | An `AdvMapDesc` (`Maps/SmallSpecialArenas/…/map.xdb` + `GroundTerrain.bin`) used as the 3D set. |
| `SoundSet`, `Music`, `MusicVolume` | Ambient set; `Sounds/_(Music)` descriptor (Ogg file relative to `bin/`). |
| `CustomSceneAmbientLight` | `Lights/_(AmbientLight)` replacing the map's light. |
| `sentences/Item` | The lines, in order (below). |
| `objects/Item` | Actors present from the start: `#xpointer(id(item_…)/AdvMapHero|AdvMapMonster)` references to actors declared inline anywhere in the file, or inline `AdvMapMonster`s. |
| `CameraShiftZ` | Added to every camera anchor's height (0.1 in all stock scenes). |
| `Anim3DSoundVolume` | Volume of animation sounds. |

Actors: `AdvMapHero` (`Shared` → `AdvMapHeroShared`; dialog scenes use its `HeroCharacterArena`, which has
the speech gestures `spneutral`, `sporator`, `speech_knee`…; the hero's name is the shared descriptor's
`Editable/NameFileRef`) and `AdvMapMonster` (`Shared` → `AdvMapMonsterShared`, `Creature` → creature
character with its arena anim set). `Pos` is in tiles (heroes may be fractional), `Rot` radians about +Z;
like other map objects the origin is the centre of tile `floor(Pos)`.

## Sentence

| Field | Meaning |
|---|---|
| `text` | Subtitle (UTF-16 text file). |
| `sound` | Voice (`Sounds/_(Sound)` → `bin/SoundsLoc/<uid>`, Ogg). 400 of 567 shipped lines are voiced. |
| `heroLink` / `monsterLink` | The speaker (inline actor or id reference). |
| `MonsterCustomName` | Text shown instead of the speaker's name. |
| `NewCameraSet` (`cameraSet` in old files) | `DSceneCameraSet` (below). |
| `DynamicCamera` | true (all stock sentences): the camera moves from the set's start to finish camera. |
| `duration` | Seconds. 346 of 400 voiced lines have 0, the rest small values (0.5–8) → read as time added after the voice. Unvoiced lines last `duration`. *Inferred from the data, not traced in the exe.* |
| `soundDuration` | Always 0 in the stock data. |
| `StopAmbient`, `StopMusic` | Stop the ambient set / music at this line. |
| `CustomAmbientLight` | Light override for the line (13 lines). |
| `ActorAnimationIndex`, `AnimName`, `AnimationDelay` | The speaker's gesture and its delay. |
| `CustomSounds/Item` | `Delay`, `sound`, `Duration`. |
| `CustomEffects/Item` | `Delay`, `Effect`, `Pos` (world units, Z-up), `Rot`, `MovementSpeed`, `MovePoints`, `ConstantPitch`. |
| `CustomAnimations/Item` | Actor link, `AnimationDelay`, `AnimName` (`move`, `death`, `happy`, `attack00`, `idle00`, …), `MovePoints` (tiles, walked in order), `FinalAngle` (degrees, the facing after the walk), `MovementSpeed` (0 = the anim set's). |

## Cameras

`DSceneCameraSet`: `StartCamera`/`FinishCamera` (`DSceneCamera` files), `StartCameraDiff`/`FinishCameraDiff`
(added to the anchor), `StartCorrectionRot`/`FinishCorrectionRot` (added to yaw), `UniformCameraMovement`
(0 in 885 sets, 1 in 318), `IgnoreYawDiff` (2 sets), `Direction` (0/1), `Circles` (22 sets with 1).

`DSceneCamera`: `Pos` (the camera record below), `Absolute` (false in 39 cameras: relative to the speaker),
`Rot` (extra yaw for relative cameras).

### Camera record (also map dialogs and `MoveCamera`)

Serializer `009fedd0` writes `Rod, Pitch, Yaw, Roll, FOV, ClipDistanceMin, ClipDistanceMax, Anchor` (offsets
0x00…0x1C). Orientation, from `007cd120`: half-angle quaternions about the world axes — Pitch about X
(axis constant at `00f3fc28`), Roll about Y (`00f3fc34`), Yaw about Z (`00f3fc40`), angles in radians
(×0.5 at `00e0ab70`); the routine returns the quaternion applied to (0,0,1) (the up vector). So the
camera's forward axis is +Y:

```
forward = (−sin Yaw·cos Pitch, cos Yaw·cos Pitch, sin Pitch)      // negative pitch looks down
eye     = Anchor − forward·Rod                                     // Anchor = look-at point
```

Anchors are in world units (2 per map tile). Checks: A2C1M1's first map dialog (Pitch −0.71, Rod 53) puts
the eye 37 units above the map looking down at the anchor; dialog anchors coincide with the speakers'
positions ×2 at head height (e.g. Giovanni after walking to tile (29,13) → anchor (59,27,3.7)); the
over-the-shoulder shot of Ornella's line looks from behind Giovanni towards Ornella. Implemented in
`src/H5.Assets/Scenes/CameraPos.cs` (tested).

Interpolation used by the player (*not traced in the exe*): linear in all parameters, yaw the short way
unless `Direction` forces a way and `Circles` adds turns; non-uniform sets ease in/out (smoothstep).

## Map dialogs (`StartAdvMapDialog(index)`)

`map.xdb dialogs/Item`: `Sentences/Item` (`Text`, `Sound`, `Pause` seconds after the voice), `Camera` (the
record above, over the adventure map), `Floor`, background/speech volumes. Shown on
`UI/MiniDialogScreen/MiniDialogScreen` (`HeroText`, Skip → `skip`). 156 calls in the shipped scripts.
