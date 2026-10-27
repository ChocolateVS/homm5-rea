# Saves, profiles, options, Hall of Fame

Findings from H5_Game.exe (Ghidra, `tools/saveres/ghidra.sh`) and the original data, and how
H5Reborn reproduces them.

## Saves
- **Location**: `profiles\<name>\Saves\` (strings `profiles\`, `Saves\`). H5Reborn: `user://profiles/<name>/saves/`.
- **File names**: `save_%d.sav`, first unused number from 0 (`FUN_007ea770` probes `save_%d.sav`
  upward). The visible name is stored inside the file. `UI/save_filename_format.txt` = `save_<value=id>.sav`.
- **Format**: the stock file starts with a version word whose high half is 3 (`FUN_007e4d90`,
  `keep_my_old_saves_alive` setvar accepts older ones) and a type word dispatched by `FUN_007e7500`
  (0 single game, 4 campaign, 1/2 multiplayer). H5Reborn saves are not interchangeable with stock
  saves: they are zips with `manifest.json` (SHA-256 per part), `header.json` and parts
  `adventure.json`, `scripts.json` (Lua heap + threads), `campaign.json`, `client.json`, `thumbnail.png`
  (`src/H5.Save`).
- **Names**: quicksave/quickload use the save named `UI/Common/defaultsavename.txt` (`FUN_007e7500`
  falls back to `defaultsavename` when no name is given). Autosave name `UI/Common/autosavename.txt`
  (`Autosave <value=id>`); CSavesCreator vf 0x1c (`007f9e10`) uses ids 1..3 and then overwrites the oldest.
- **Autosave**: after the AI moves, when a human player is to act and the `autosave` var is set,
  the game saves with `autosavename` (`FUN_00607d50`, var registered in `FUN_00609910`).
- **Keys**: `bind quicksave 'F5'`, `save 'F6'`, `load 'F7'`, `quickload 'F8'` (input_a2.cfg); handlers
  registered in `FUN_007f7190` dispatch to the save manager's vtable (+0x1c named save = `FUN_007ea770`,
  +0x20 quicksave, +0x24 quickload, +0x28 save screen, +0x2c load screen).
- **Thumbnails**: SaveInfo/ScreenShoot (274×180) shows the save's screenshot.
- **Network games**: the exe saves them (`CSaveScreen<SLanSaveHeaderInfo/SUbicomSaveHeaderInfo>`, host reload
  through `CMultiplayerHostLoadLogic`); H5Reborn refuses to save a network game for now. Hot-seat games are
  written with the hot-seat kind. Full ledger: `docs/rules/AUDIT_CAMPAIGN.md`.
- **Save before combat**: `save_before_combat` ("Before Combat <id>", var `FUN_005aadc0`, default 0) saves
  when a human-vs-computer battle starts (`FUN_005a5d90`); the save holds the pending battle, which starts
  again on load. Saving during combat is refused otherwise.
- **Save complete**: `ID_SAVE_COMPLETE` ("Save completed") is shown for `save_ok_time` ms after a save.
- **Generated maps**: saved with the generator options (MapSpec `rmg`) and regenerated on load.

## Options
The whole options screen is data: `UI/uiconsts.(UIGameConstsH5).xdb` → `GameOptions/Categories`
(`game`: Adventure + Combat pages; `video_audio`: Video + Audio pages). Each option gives its type
(`OPTION_CHECK_BOX`, `OPTION_SCROLLER`, `OPTION_COMBO_BOX`, `OPTION_BLOCK_HEADER`), items and values,
`GlobalVar1` (setvar name), `DefaultValue1` (value for check boxes, item index otherwise), `Hide`,
`Preview`, `OptionToDisableID/OptionDisableByValue1/OptionToDisableValue1` and
`DisableByCodeGlobalVar` (e.g. `disable_options_by_multiplayer`, `~gfx_fullscreen`). The disable-rule
direction (target enabled only while the controlling option equals `OptionDisableByValue1`) is
inferred from the full screen → "Stay active" pair. Values persist per profile in
`user://profiles/<name>/options.cfg` under the stock var names, seeded from the shipped
`default_profile/user_a2.cfg`.

## Profiles
ChangeProfile panel in UI/MainMenu2; first run (`ID_SET_FIRST_PROFILE_NAME_SCREEN`) asks for a name in
`UI/SimpleEditBox` titled `UI/SetFirstProfileName/header.txt`.

## Hall of Fame
`UI/UIGameRoot.(UIGameRoot).xdb → HallOfFame`: default results (names + points), rank creature per
score (`Ranks`: Single/CampaignMinPointCount), `MapSizes` and `uiDifficulties` point factors. The
base score before those factors is **not recovered** yet; `HallOfFame.Score(base, size, difficulty)`
applies the data factors and recording a finished game needs the adventure end-of-game hook.
