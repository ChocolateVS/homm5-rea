# Campaigns — `Campaigns/**`

`Campaigns/CampaignsSets/Standart.xdb` (`CampaignsSet`: `Order`, `Campaigns` list) names the stock
campaigns in order: A2C0 (prologue, *Rage of the Tribes*), A2C1, A2C2, A2C3. Loader:
`src/H5.Assets/Scenes/CampaignData.cs`; flow: `game/src/Campaign/**`. (`Roots/roots.xdb` still lists the
base game's `Campaigns/C1…C6.xdb`, which TotE doesn't ship.)

## `(Campaign).xdb`

| Field | Meaning |
|---|---|
| `InternalName` | A2C1 … |
| `NameFileRef`, `FullNameFileRef` | Campaign name (button text). |
| `NameCommentFileRef` | Line under the button (the campaign hero: "Arantir", "Quroq", "Zehir"). |
| `DependentCampaigns` | Campaigns that must be completed first (A2C1 needs A2C0). |
| `Missions/Item` | Missions in order (below). |
| `AdventureMusic`, `WinMissionMusic`, `LoseMissionMusic` | `Sounds/_(Music)` descriptors. |
| `UserCampaign` | false for stock campaigns. |

Text hrefs are relative to the campaign file; in the stock `texts.pak` some sit under extra
`Campaigns/` levels (`Campaigns/Campaigns/…/A2C2M3/…`), which the loader falls back to.

### Mission

| Field | Meaning |
|---|---|
| `NameFileRef` | Mission name ("Last Soul Standing"). |
| `NameCommentFileRef` | "Mission One"… |
| `MissionTag` | `Maps/Scenario/<id>/map-tag.xdb` (`AdvMapDescTag`: map, name, `DescriptionFileRef` = briefing). |
| `Bonuses/Item` | Up to 3 pre-mission bonuses: `Type` (E_BONUS_NONE/ARTIFACT/CREATURE/RESOURCE/SPELL/BUILDING), `Target/Hero` (empty in all stock bonuses = the main hero), `BonusArmy`, `BonusArtifactID`, `BonusResources`, `BonusSpellID`, `Building`. |
| `HeroesPool` | `Count` and `Heroes/Item` (`HeroScriptName`, `TargetCampaign`, `TargetMission` — 1-based): heroes saved when the mission is won and restored in the target mission. |
| `startMusics`, `additional*Musics` | Music overrides. |

Scripts read the chosen bonus with `GetPlayerSelectedCampaignBonusIndex()`.

## Screens

- **Selection:** `UI/MainMenu2` panels `SelectCampaign` (variants `MenuPanel01`…`MenuPanel10` hold 0…9
  `CampaignBlockNN` blocks plus Load Campaign; reactions `campaign_selected`, `select_campaign_back`,
  `select_load_user_campaign`) and `SelectMission` (`MissionBlock01`…`05`, `mission_selected`,
  `select_mission_back`), with the profile/difficulty `SetupPanel`.
- **Pre-mission:** `UI/PWLScreen` (UIGameRoot `ID_PWL_SCREEN`): `CampaignName`, `MapDesc`
  (`<value=mission_number>`, `<value=map_name>`, `<value=map_desc>`), `BonusSelection` with `Bonus_0..2`
  (`BonusIcon`, `BonusDesc` from `UI/pwlscreen/<KIND>.txt`, `BonusSelect` — its second visual state is
  the selected look), `Start`, progress bar, "Press any key" (setvars `pwl_press_any_key_period` 1.0,
  `pwl_press_any_key_fade` 0.5).
- **Results:** `UI/MissionResults` (`ID_MISSION_RESULTS_SCREEN`): header from
  `UI/AdventureScreen-heavy/MissionEndWin.txt` / `MissionEndLoose.txt`, statistics templates in
  `ResultStrings`, Close (`close_mission_results0`).

## Not reproduced yet

Mission statistics on the results screen, hall-of-fame scoring (UIGameRoot `CampaignMinPointCount`),
user campaigns / Load Campaign, the difficulty combo.
