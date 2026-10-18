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
