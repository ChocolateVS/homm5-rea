using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Assets.Scenes;

namespace H5.Adventure.Engine;

/// <summary>
/// The pre-mission campaign bonus, as the exe offers and gives it.
/// Offer (PWL screen 006b38d0 → 006b25e0, run after the mission's map is loaded, 005a1a50): ARTIFACT / CREATURE /
/// SPELL whose id exists, RESOURCE with a positive amount (first positive of the seven), BUILDING via 006b2270.
/// Choice (006b29d0): the PWL hands (player, bonus record, world, 1-based position among the offered bonuses) to the
/// adventure frame (vtable 00e23004 → 005a5990), which queues a command (005ab370, vtable 00e0f50c, 100 bytes:
/// +0xc world, +0x10 the bonus record copy, +0x5c player, +0x60 number). Its execute 0042bcb0: the bonus object
/// made from the record (00c65be0 → 00c65b10 by type, target hero resolved by 00c65a80); player vf 0x230(number)
/// stores the number (CPlayer +0x694, read back by vf 0x234 = GetPlayerSelectedCampaignBonusIndex 005d2830);
/// then the object's check (vf 0 = 00c65710) and give (vf 4 = 00c65740 → vf 0xc).
/// </summary>
public sealed partial class Adventure
{
    /// <summary>
    /// The player's main hero (CPlayer +0x1d0 via the +0x1c interface vf 0xc = 004fcc40, null when destroyed).
    /// The setter 004fd800 only replaces an invalid main hero, and only with a hero of this player; its callers
    /// are not traced: the map player's MainHero when this player owns it, else the player's first living hero.
    /// </summary>
    public HeroState? MainHeroOf(int player)
    {
        if (player >= 0 && player < Map.Players.Count && Map.Players[player].MainHeroUid is { } uid
            && Map.Objects.Find(o => o.Uid == uid && o.Kind == ObjectKind.Hero)?.Hero?.Shared?.Id is { Length: > 0 } id
            && S.Heroes.Find(h => h.Alive && h.Owner == player && h.DefId == id) is { } main)
            return main;
        return S.Heroes.Find(h => h.Alive && h.Owner == player);
    }

    /// <summary>
    /// The player's main town (CPlayer +0x1d4 via interface vf 4 = 00b5ace0). The setter 004fd770 keeps a valid
    /// main town and takes only a town of this player; 004fdea0 clears it when the town is lost. Callers not
    /// traced: the map player's MainTown when owned, else the player's first town.
    /// </summary>
    public TownState? MainTownOf(int player)
    {
        if (player >= 0 && player < Map.Players.Count && Map.Players[player].MainTownUid is { } uid
            && S.Towns.Find(t => t.Owner == player && Map.Objects[t.Object].Uid == uid) is { } main)
            return main;
        return S.Towns.Find(t => t.Owner == player);
    }

    /// <summary>
    /// The hero a hero bonus goes to (00c657e0): the record's Target hero (an SAdvMapHeroShared, found in the world
    /// by 00c65a80) when the player has it (player vf 0xbc = 004fc6a0: owner and in the player's hero list), else the
    /// main hero. <paramref name="targetShared"/> is the Target hero's definition id ("" = none).
    /// </summary>
    public HeroState? CampaignBonusHero(int player, string targetShared)
    {
        if (targetShared.Length > 0 && S.Heroes.Find(h => h.Alive && h.DefId == targetShared) is { } t && t.Owner == player)
            return t;
        return MainHeroOf(player);
    }

    /// <summary>
    /// 006b25e0: whether the PWL screen offers the bonus. BUILDING (006b2270): the player's main town (vf 4) must
    /// exist and a not-free CUpgradeTownBuildingCmd (005e91f0, vtable 00e0d898, execute 0042fd40) for it must pass
    /// in the world's test mode (00b0f580): the town is the player's, and the town's build call (vf 0x6c = 00cd4a40,
    /// not free) accepts it — the build check vf 0xac = 00cd6a90 (<see cref="BuildCheck"/>) against the player's purse.
    /// </summary>
    public bool CampaignBonusOffered(int player, CampaignBonus b)
    {
        switch (b.Type)
        {
            case "E_BONUS_ARTIFACT": return Core.Artifacts.ContainsKey(b.Artifact);
            case "E_BONUS_CREATURE": return Core.Creatures.ContainsKey(b.Creature);
            case "E_BONUS_RESOURCE": return b.Resources.Any(r => r > 0);
            case "E_BONUS_SPELL": return b.Spell != "SPELL_NONE" && Core.Spells.ContainsKey(b.Spell);
            case "E_BONUS_BUILDING":
                if (MainTownOf(player) is not { } town || town.Owner != player || PlayerByIndex(player) is not { } p) return false;
                return BuildCheck(town, b.Building, p.Purse) == BuildDenial.CanBuild;
            default: return false;
        }
    }

    /// <summary>
    /// The chosen bonus's command (0042bcb0). <paramref name="number"/> is the 1-based position of the bonus among
    /// the offered ones (006b29d0); it is stored on the player even when the bonus then gives nothing. The give
    /// routines: ARTIFACT 00c65980 (artifact made by the world's factory, hero vf 0x84), CREATURE 00c65890 (hero army
    /// vf 0x48 → army vf 0xc, slot −1), SPELL 00c658f0 (hero vf 0xe8(spell, 1)), RESOURCE 00c65490 (purse vf 0xd8 +
    /// the seven amounts → vf 0xdc), BUILDING 00c65920 (main town vf 0x6c(world, name, free = 1): 00cd4a40 skips the
    /// build check when free but still needs a level below the building's maximum). The checks (vf 8): hero kinds
    /// 00c65dd0 need a hero (00c657e0), BUILDING 00c65e70 a main town, RESOURCE 00458480 none.
    /// Returns whether the bonus was given.
    /// </summary>
    public bool ApplyCampaignBonus(int player, CampaignBonus b, int number)
    {
        if (PlayerByIndex(player) is not { } p) return false;
        p.CampaignBonus = number;
        string target = b.TargetHero.Length > 0
            ? Core.Heroes.Values.FirstOrDefault(d => d.SourcePath.TrimStart('/').Equals(b.TargetHero.TrimStart('/'), StringComparison.OrdinalIgnoreCase))?.Id ?? ""
            : "";
        var hero = CampaignBonusHero(player, target);
        bool given = false;
        RunScriptAction(() =>
        {
            switch (b.Type)
            {
                case "E_BONUS_ARTIFACT" when hero != null:
                    if (Core.Artifacts.ContainsKey(b.Artifact)) { ScriptGiveArtifact(hero, b.Artifact); given = true; }
                    break;
                case "E_BONUS_CREATURE" when hero != null:
                    given = Core.Creatures.ContainsKey(b.Creature) && b.Count > 0 && hero.Army.TryAdd(b.Creature, b.Count);
                    break;
                case "E_BONUS_SPELL" when hero != null:
                    if (b.Spell != "SPELL_NONE") { ScriptLearnSpell(hero, b.Spell); given = true; }
                    break;
                case "E_BONUS_RESOURCE":
                    for (int r = 0; r < 7; r++)
                        if (b.Resources[r] != 0) ScriptGive(player, r, b.Resources[r]);
                    given = true;
                    break;
                case "E_BONUS_BUILDING" when MainTownOf(player) is { } town:
                    if (town.MaxLevel.TryGetValue(b.Building, out var max) && town.Level(b.Building) < max)
                    {
                        ScriptUpgradeBuilding(town, b.Building);
                        given = true;
                    }
                    break;
            }
        });
        return given;
    }
}
