using H5.Adventure.State;
using H5.Combat;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Town assaults: the SiegeSetup handed to the combat engine and the combat bonuses that
// town buildings and town specialisations give to the troops guarding (or assaulting) a
// town. Values come from DefaultStats `adventure/TownBuildingBonuses` and the fan manual
// (Tavern p.224-243 building lists, grails "A Tear of Asha is required", specialisations p.244).
public sealed partial class Adventure
{
    /// <summary>Stat modifiers for one battle side, split by what the engine applies them to.</summary>
    public sealed class TownCombatBonus
    {
        /// <summary>Applied to every creature of the side, hero or not (BattleState.SideBonuses).</summary>
        public int Attack, Defence, Morale, Luck;
        /// <summary>Applied to the side's hero only ("defending heroes").</summary>
        public int HeroSpellPower, HeroKnowledge;
    }

    /// <summary>
    /// "Town level" maximum: every building level defined for the town type, i.e. the town level
    /// with everything built (reproduces the fan manual p.309 table for all eight factions).
    /// </summary>
    public int TownMaxLevel(string townType) =>
        Db.Buildings.TryGetValue(townType, out var list) ? Math.Max(1, list.Count) : 1;

    /// <summary>Fortification layout and siege modifiers of a town under attack.</summary>
    public SiegeSetup SiegeFor(TownState t)
    {
        var spec = SpecializationBonus(t);
        return new SiegeSetup
        {
            Town = t.Type,
            FortLevel = Math.Clamp(t.Level("TB_FORT"), 0, 3),
            TownLevel = Math.Max(1, t.DevLevel),
            TownMaxLevel = Math.Max(1, TownMaxLevel(t.Type)),
            Stoneworks = t.Type == Towns.Fortress && t.Level("TB_SPECIAL_4") > 0,
            WallsSpecialization = spec == "TOWN_BONUS_WALLS",
            TowersSpecialization = spec == "TOWN_BONUS_TOWERS",
            // Guard Post: 5 Defenders × town level × month (p.308; CreaturesPerTownLevelPerMonth).
            // TODO(exe): confirm that "town level" here is the built-levels count, as for moat damage.
            GuardPostDefenders = t.Type == Towns.Fortress && t.Level("TB_SPECIAL_3") > 0
                ? (int)Core.Stats.Get("adventure/TownBuildingBonuses/Fortress_GuardPostStats/CreaturesPerTownLevelPerMonth", 5)
                  * Math.Max(1, t.DevLevel) * S.Month
                : 0,
        };
    }

    /// <summary>The TOWN_BONUS_* of the town's specialisation, or null.</summary>
    public string? SpecializationBonus(TownState t) => Db.TownSpecializationBonus(t.Specialization);

    /// <summary>Bonuses for the town's defenders and for the side assaulting it.</summary>
    public (TownCombatBonus Defenders, TownCombatBonus Assailants) TownBonuses(TownState t)
    {
        var d = new TownCombatBonus();
        var a = new TownCombatBonus();
        double V(string key, double def) => Core.Stats.Get($"adventure/TownBuildingBonuses/{key}", def);

        // Tavern: "Increases morale of troops defending the city by +1".
        if (t.Level("TB_TAVERN") > 0) d.Morale += 1;
        switch (t.Type)
        {
            case Towns.Sylvan when t.Level("TB_SPECIAL_2") >= 2:      // Sparkling Fountain
                d.Luck += (int)V("Sylvian_SparklingFountainDefenderLuckBonus", 2); break;
            case Towns.Inferno when t.Level("TB_SPECIAL_1") > 0:     // Infernal Loom
                a.Luck -= (int)V("Inferno_InfernalLoomEnemyLuckPenalty", 2); break;
            case Towns.Necropolis when t.Level("TB_SPECIAL_2") > 0:  // Unholy Temple
                a.Morale -= (int)V("Necropolis_UnholyTempleEnemyMoralePenalty", 2); break;
        }
        if (t.Level("TB_GRAIL") > 0)
            switch (t.Type)
            {
                case Towns.Inferno: d.HeroSpellPower += (int)V("Inferno_GrailDefenderSpellpowerBonus", 10); break;
                case Towns.Dungeon: d.HeroSpellPower += (int)V("Dungeon_GrailDefenderSpellpowerBonus", 10); break;
                case Towns.Academy: d.HeroKnowledge += (int)V("Academy_GrailDefenderKnowledgeBonus", 10); break;
                case Towns.Fortress:                                   // Blazing Anvil
                    d.HeroSpellPower += (int)V("Fortress_BlazingAnvil_DefenderSpellPowerBonus", 6);
                    d.Defence += (int)V("Fortress_BlazingAnvil_DefenderDefenceBonus", 6);
                    break;
            }

        // Specialisations (p.244 ff.): PLUS_* for the troops guarding, MINUS_* for the assailants.
        switch (SpecializationBonus(t))
        {
            case "TOWN_BONUS_PLUS_DEFENCE_OFFENCE": d.Attack += 1; d.Defence += 1; break;
            case "TOWN_BONUS_PLUS_DEFENCE_OFFENCE_2": d.Attack += 2; d.Defence += 2; break;
            case "TOWN_BONUS_PLUS_DEFENCE": d.Defence += 1; break;
            case "TOWN_BONUS_PLUS_DEFENCE_2": d.Defence += 2; break;
            case "TOWN_BONUS_PLUS_LUCK": d.Luck += 1; break;
            case "TOWN_BONUS_PLUS_MORALE": d.Morale += 1; break;
            case "TOWN_BONUS_MINUS_DEFENCE_OFFENCE": a.Attack -= 1; a.Defence -= 1; break;
            case "TOWN_BONUS_MINUS_DEFENCE_OFFENCE_2": a.Attack -= 2; a.Defence -= 2; break;
            case "TOWN_BONUS_MINUS_OFFENCE": a.Attack -= 1; break;
            case "TOWN_BONUS_MINUS_OFFENCE_2": a.Attack -= 2; break;
            case "TOWN_BONUS_MINUS_LUCK": a.Luck -= 1; break;
            case "TOWN_BONUS_MINUS_MORALE": a.Morale -= 1; break;
        }
        return (d, a);
    }

    /// <summary>
    /// Kingdom-wide grail effects on a player's heroes in every battle: Haven (Elrath's Sentinel)
    /// and Sylvan grails +2 Luck (DefaultStats Heaven/Sylvian_GrailLuckBoost), Stronghold Tower of
    /// Heroes +4 Attack (Stronghold_TowerOfHeroes_OffenceBonus).
    /// </summary>
    public (int Attack, int Luck) GrailHeroBonus(int player)
    {
        int attack = 0, luck = 0;
        foreach (var t in S.Towns.Where(t => t.Owner == player && t.Level("TB_GRAIL") > 0))
            switch (t.Type)
            {
                case Towns.Haven: luck += (int)Core.Stats.Get("adventure/TownBuildingBonuses/Heaven_GrailLuckBoost", 2); break;
                case Towns.Sylvan: luck += (int)Core.Stats.Get("adventure/TownBuildingBonuses/Sylvian_GrailLuckBoost", 2); break;
                case Towns.Stronghold: attack += (int)Core.Stats.Get("adventure/TownBuildingBonuses/Stronghold_TowerOfHeroes_OffenceBonus", 4); break;
            }
        return (attack, luck);
    }

    static void AddSide(SideBonus s, TownCombatBonus b)
    {
        s.Attack += b.Attack; s.Defence += b.Defence; s.Morale += b.Morale; s.Luck += b.Luck;
    }
}
