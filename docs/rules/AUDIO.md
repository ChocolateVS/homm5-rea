# Audio — behaviour recovered from H5_Game.exe

Classes (RTTI, namespace `NSound` unless noted): `CMusicPlayerStack` (vtable 00e47be8), `CMusicPlayer`
(00e47b4c), `CNonLoopMusicPlayer` (00e47b88), music calculators `CConstantMusicCalculator` (00e47b28),
`CCompositeMusicCalculator` (00e47bc4), `CArrayMusicCalculator` (00e47c24), `CAdvMapMusicCalculator`
(00e478b4), `CAdvMapHeroMusicCalculator` (00e4786c), `CTerrainMusicCalculator` (00e47890),
`CSiegeMusicCalculator` (00e47aa0), ambience `CAdvAmbientSound` (00e4781c), `CAmbientSound` (00e800ac),
screens `CSoundScreen` / `CAdventureSoundScreen` / `CTownSoundScreen`; low level `NFMSound::CMusicSystem` (FMOD).
Project for re-running: `tools/audiore/ghidra.sh` (copy of the analysed adventure project).

## Music selection

* **Composite** (Next 00819ae0): asks each child calculator, picks uniformly at random among the
  non-null answers; if the pick equals the previous track and it already played twice in a row, that
  candidate is removed and it re-draws → never three times in a row.
* **Adventure** (built by 00815d60, wrapped by 00815f40): composite of [selected hero's
  `AdventureMusic` (008164e0), music of the tile under the selected hero (00816570, via the tile type's
  `AdventureMusic`), constant(campaign `AdventureMusic`)]; in a campaign mission also
  composite(`additionalAdventureMusics`) and the whole thing is wrapped in `CAdvMapMusicCalculator`
  (00816370): the mission's `startMusics` composite once, then the main composite.
* **Wait** (other players' turns, 008162d0): race `WaitMusic` + campaign `additionalWaitMusics`.
  *Inferred:* the race is the local human player's (the race argument isn't visible in the decompile).
* **Combat** (008186b0): `combatMusics` of two races (FUN_00818640 called twice — taken as the two
  armies' races) + campaign `additionalCombatMusics`.
* **Siege** (ctor 00818a90, Next 008188f0): the race's `SiegeMusic` first, then the combat composite.
* **Front end**: UIGameRoot `MAIN_MENU_MUSIC_ID` (006931a0), `MP_MAIN_MENU_MUSIC_ID` (006938c0),
  `CREDITS_SCREEN_MUSIC_ID` (00793d20), grail themes (0076db30, 005a5d90).
* **Players**: `CMusicPlayerStack` pushes a player per screen and pauses/unpauses the previous top
  (00819e00); `CNonLoopMusicPlayer` plays one track and pops (battle results, grail).
  *Inferred from data names (FMOD layer not traced):* fade in/out over `FadeIn`/`FadeOut` ms; after a
  track ends a random silence of `MinPausedTime..MaxPausedTime` ms before the calculator is asked again.

## Ambience

* **Adventure** (`CAdvAmbientSound::Calc`, 00815400): the tile under the view centre (clamped to the
  map) and its 8 neighbours each vote for their tile type's `AmbientSoundSet`; tiles not yet explored
  vote for `INVISIBLE_AMBIENT_SOUND`; the set with most votes plays (search starts at a random bucket, so
  ties are random).
* **Sets**: `Loop` runs continuously; a random member of `sounds` plays after a pause of
  `MinPauseTime..MaxPauseTime` seconds, repeatedly. Sets crossfade over setvar
  `sound_ambient_fade_duration` (registered at 00b6b840, default 2.0 s; stock autoexec 2), and
  `sound_ambient_enable` (default 1) switches ambience off.
* **Towns** use the race's `TownAmbientSoundSet`; **combat** the arena's `AmbientSoundSet`.

## Volumes

The original has only `sound_musicvolume`, `sound_sfxvolume` and `sound_ambient_enable`. H5Reborn
keeps that: Music bus = music volume; SFX bus = sound volume; `Ambient` and `Speech` buses send into
SFX (ambient muted by the switch). Localised voice (`bin/SoundsLoc`) goes to Speech.

## Event sounds and positional sound (pass 2)

UIGameRoot `sounds` / `musics` are looked up **by name at their use sites**, so string
cross-references give the exact triggers (all in `game/src/Audio/AdventureEventAudio.cs` unless noted):

| Sound / music | Trigger (exe) |
|---|---|
| NEW_WEEK_NEUTRAL/GOOD/BAD_SOUND | start-of-week notice, by the moon week's `Type` (WEEKTYPE_NEUTRAL=0/GOOD=1/BAD=2) — 009946e0 |
| HERO_LEVEL_UP_SOUND (HERO_RELEARNING_SOUND when relearning; same file) | level-up dialog — 006a8f50 |
| OBJECTIVE_ACTIVE/COMPLETED/FAILED_SOUND | objective state 2/3/4 with its notice, only when the notice is shown (visible objective, own player) — 00c5fec0; client: `ScriptUiBridge` `objective_state` |
| TUTORIAL_SOUND | tutorial message box opens — 00770df0; client: `ScriptUiBridge` |
| HERO_JOIN_FLEE_SOUND | neutrals offer to join / flee — 005a5d90 |
| HERO_FIND_GRAIL_MUSIC, BUILD_GRAIL_MUSIC | one-shot music (CNonLoopMusicPlayer) when the grail is found / the grail building is built — 005a5d90 + 0076db30, 00615230 |
| HIRE_CREATURES_SOUND, HIRE_HERO_SOUND, BUY_WAR_MACHINE_SOUND | recruit (006d8ba0), tavern hire (006350b0), blacksmith (006ece20) |
| CHAT_GET_MESSAGE_SOUND | new chat lines — 00726630 (DuelWaitScreen, AdvNetScreens) |
| WHOOSH_SOUND | combat: leaving a cinematic camera back to the battle camera — 0054a9e0 → 0053e980 (CinematicCameras.End) |
| CREDITS_SCREEN_MUSIC_ID | credits screen (00793d20), pushed over the paused menu theme |

Not wired (no matching feature yet): HERO_DIG_HOLE_SOUND, MARKET_EXCHANGE_SOUND, BUY_ARTIFACT/SPELL/RUMORS/THIEVES_SOUND,
MAIL_SEND_SOUND, FORTHILL_UPGRADE_CREATURES/NO_RESOURCES_SOUND, MICROARTIFACT_UPGRADED_SOUND — `AudioSystem.PlayNamed(id)`
is the hook for those screens.

*Inferred from data names (use sites not traced):*
* Object visits play the shared descriptor's `SoundEffect` (294 descriptors; the Stables one is the same Stabels sound as
  STABLES_VISIT_SOUND, which plays only for a Stables without one). Town buildings: the race's TableRaceMusic
  `MagicGuildSound` / `MarketplaceSound` / `BlacksmithSound` / `TownHallSound` when that building's screen opens and
  `UpgradeTownBuildingSound` when a building is finished (fields read at 0053a6f0; no shipyard screen yet).
* Dialog scenes: scene `Music` (× `MusicVolume`%) is pushed on the music stack; per sentence `MusicVolumePercent`
  (0 = unchanged), `MusicFadeOutDuration` ms (fade to silence), `MusicOverride` (Music, StartTime s into the track,
  after TimeShift s), `StopMusic`, `StopAmbient` (never set in stock data) — field names from the serializer 00b31280.
* Campaign `WinMissionMusic` / `LoseMissionMusic` (serializer 00c49eb0) play once over the mission results window.
* Adventure-map dialogs: `BackgroundMusicVolume` / `BackgroundAmbientVolume` / `SpeechVolume` (percent, per
  `dialogs/Item`, serializer 00a0da40; all 100 in stock maps) scale music/ambience/voice while the dialog runs.

**Positional sounds** (`MapSoundEmitters`): descriptors with an `AdventureSoundEffect` (37: mines, waterwheels,
arenas, monoliths, whirlpools…) loop a 3D sound at the object, on its own floor only; a walking hero runs its
AdvMapAnimSet `move` clip Sound (looping 3D, e.g. Heroes/DemonLord/Move — there are no per-terrain footstep sounds in
the data). 3D sounds use the Sound descriptor's MinDistance/MaxDistance as FMOD does: full volume inside Min, then
Min/distance (Godot inverse distance, UnitSize = Min, MaxDb = 0).

**Still not confirmed from the exe:** the FMOD-level fade/pause timing (CMusicPlayer vtable 00e47b4c; the Music
descriptor reader 00b13360 only names the fields) and the race arguments of the wait/combat music calculators.
**Combat sound coverage:** anim-set clip sounds (attack/hit/death/move…: UnitView), ability and spell
`SpellVisual` `SoundEffect`s (effects runtime), siege `HitSound`s (SiegeView), luck/morale effects; Shot
descriptors carry no sound fields (projectile sounds come from the shooter's attack clip).
