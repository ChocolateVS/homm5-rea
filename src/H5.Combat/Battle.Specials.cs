using System.Text.RegularExpressions;
using H5.Core.Data;

namespace H5.Combat;

// Hero specialisations and artifact set bonuses. Constants come from DefaultStats
// (combat/HeroSpecializations, ArtifactsSetsEffectsConsts); behaviour follows the in-game
// specialisation and set descriptions (Text/Game/Heroes/Specializations, RPGStats/ArtifactSets).
public static class Specials
{
    public const string Dragonish = "ARTFSET_EFFECT_DRAGONISH", Dwarven = "ARTFSET_EFFECT_DWARVEN", Lions = "ARTFSET_EFFECT_LIONS",
        Magis = "ARTFSET_EFFECT_MAGIS", Necromancers = "ARTFSET_EFFECT_NECROMANCERS", Hunters = "ARTFSET_EFFECT_HUNTERS",
        Ogres = "ARTFSET_EFFECT_OGRES", Runic = "ARTFSET_EFFECT_RUNIC", Demonic = "ARTFSET_EFFECT_DEMONIC";

    /// <summary>"Base + floor(level / LevelDivisor) * PerDividedLevel" formulas used by several DefaultStats entries.</summary>
    public static double LevelFormula(RpgStats st, string path, int level, double defBase = 0, double defDiv = 1, double defPer = 0)
    {
        double b = st.Get(path + "/Base", defBase), div = Math.Max(1, st.Get(path + "/LevelDivisor", defDiv)), per = st.Get(path + "/PerDividedLevel", defPer);
        return b + Math.Floor(level / div) * per;
    }

    static readonly Regex FamilySpec = new(@"^All (?<names>.+?) in hero'?s army gain \+1 to their Attack and Defen[cs]e for every two levels", RegexOptions.IgnoreCase);
    static readonly Regex FamilySpecAlt = new(@"^(?<names>.+?) in hero'?s army gain \+1 to their Attack and Defen[cs]e for every two levels", RegexOptions.IgnoreCase);

    /// <summary>
    /// Creatures boosted by a "+1 Attack/Defense every two levels" specialisation, read from the specialisation text
    /// itself so that every such hero works without a hand-written table.
    /// </summary>
    public static List<string> FamilyFromDescription(GameDatabase db, string description)
    {
        var m = FamilySpec.Match(description.Trim());
        if (!m.Success) m = FamilySpecAlt.Match(description.Trim());
        if (!m.Success) return new List<string>();
        var names = m.Groups["names"].Value.Replace(" and ", ", ").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var ids = new List<string>();
        foreach (var n in names)
            foreach (var c in db.Creatures.Values)
                if (PluralForms(c.Name).Any(p => p.Equals(n, StringComparison.OrdinalIgnoreCase)))
                    ids.Add(c.Id);
        return ids.Distinct().ToList();
    }

    static IEnumerable<string> PluralForms(string name)
    {
        yield return name;
        yield return name + "s";
        yield return name + "es";
        if (name.EndsWith("y")) yield return name[..^1] + "ies";
        if (name.EndsWith("man")) yield return name[..^3] + "men";
        if (name.EndsWith("f")) yield return name[..^1] + "ves";
        if (name.EndsWith("fe")) yield return name[..^2] + "ves";
    }

    public static int Pieces(HeroState h, string set) => h.SetPieces.GetValueOrDefault(set);
}

public sealed partial class Battle
{
    int Spec(HeroState h, string spec) => h.Present && h.Specialization == spec ? h.Level : -1;

    /// <summary>"+1 Attack/Defense for every two levels, starting on first level".</summary>
    int FamilyBonus(Unit u)
    {
        var h = HeroOf(u);
        return h.Present && h.SpecFamily.Contains(u.CreatureId) ? (h.Level + 1) / 2 : 0;
    }

    double SpecMagicResistance(Unit u)
    {
        int l = Spec(HeroOf(u), "HERO_SPEC_IMPREGNABLE");
        return l < 0 ? 0 : 0.05 + 0.01 * l;
    }

    double HeroInitiativeMultiplier(HeroState h)
    {
        int l = Spec(h, "HERO_SPEC_FEATHER_MAGE");
        return l < 0 ? 1 : 1 + Stats.Get("combat/HeroSpecializations/Wizard/FeatherMage_InitiativeBonusPerLevel", 0.005) * l;
    }

    void SpecCombatStart()
    {
        for (int side = 0; side < 2; side++)
        {
            var h = S.Heroes[side];
            if (!h.Present) continue;
            // Poisoner: enemy stacks may start the battle under Decay (DefaultStats Warlock/Poisoner_*).
            int pl = Spec(h, "HERO_SPEC_POISONER");
            if (pl >= 0 && Db.Spell("SPELL_PLAGUE") is { } plague)
            {
                double p = Stats.Get("combat/HeroSpecializations/Warlock/Poisoner_PlagueProb", 0.1) + Stats.Get("combat/HeroSpecializations/Warlock/Poisoner_PlagueProbPerLevel", 0.02) * pl;
                foreach (var u in S.LivingOf(1 - side).Where(u => u.Kind == UnitKind.Creature).ToList())
                    if (S.Rng.Chance(p)) ApplyEffectSpell(Actor.Hero(side), side, plague, HeroMastery(h, plague), HeroSpellPower(h, plague), u, h.Level, h);
            }
            // Centaur Mistress: +1% starting ATB per level for centaurs.
            int cl = Spec(h, "HERO_SPEC_CENTAUR_MISTRESS");
            if (cl >= 0)
                foreach (var u in S.LivingOf(side).Where(u => u.CreatureId.StartsWith("CREATURE_CENTAUR")))
                    u.Atb += Stats.Get("combat/HeroSpecializations/Barbarian/CentaurMistress_StartATBBonusPerHeroLevel", 0.01) * cl;
            // Dwarven set (4 pieces): expert Endurance and Deflect Missile on the whole army.
            if (Specials.Pieces(h, Specials.Dwarven) >= 4)
                foreach (var id in new[] { "SPELL_STONESKIN", "SPELL_DEFLECT_ARROWS" })
                    if (Db.Spell(id) is { } sp)
                        foreach (var u in S.LivingOf(side).Where(u => u.Kind == UnitKind.Creature).ToList())
                            ApplyEffectSpell(Actor.Hero(side), side, sp, Mastery.Expert, h.SpellPower, u, h.Level, h);
        }
    }

    /// <summary>Blooddrinker: each enemy stack killed gives the whole army Attack.</summary>
    void SpecOnEnemyStackKilled(int killerSide)
    {
        var h = S.Heroes[killerSide];
        int l = Spec(h, "HERO_SPEC_BLOODDRINKER");
        if (l < 0) return;
        double bonus = Specials.LevelFormula(Stats, "combat/HeroSpecializations/Barbarian/Blooddrinker_OffenceBonusPerStack", l, 1, 6, 1);
        foreach (var u in S.LivingOf(killerSide).Where(u => u.Kind == UnitKind.Creature).ToList())
        {
            var e = u.GetEffect("FX_BLOODDRINKER");
            AddEffect(u, new Effect { Id = "FX_BLOODDRINKER", Value = (e?.Value ?? 0) + bonus, Positive = true, Dispellable = false });
        }
    }

    /// <summary>Bombardier: ballista shots carry a Fireball.</summary>
    void SpecBallistaShot(Unit ballista, Unit target)
    {
        var h = HeroOf(ballista);
        int l = Spec(h, "HERO_SPEC_BOMBARDIER");
        if (l < 0 || Db.Spell("SPELL_FIREBALL") is not { } fb) return;
        double power = Stats.Get("combat/HeroSpecializations/Demonlord/Bombardier_FireballSpellpower", -1)
                       + Math.Floor(l / Math.Max(1, Stats.Get("combat/HeroSpecializations/Demonlord/Bombardier_FireballSpellpowerLevelDivisor", 5)))
                       * Stats.Get("combat/HeroSpecializations/Demonlord/Bombardier_FireballSpellpowerPerLevel", 1);
        ResolveSpell(ballista.Id, ballista.Side, fb, HeroMastery(h, fb), Math.Max(0, power), null, target.Pos, fromHero: false);
    }

    /// <summary>Sacred Hammer: light spells on friends may also cast a free Righteous Might.</summary>
    void SpecAfterFriendlyLightSpell(HeroState h, Unit target)
    {
        int l = Spec(h, "HERO_SPEC_SACRED_HAMMER");
        if (l < 0 || !target.Alive) return;
        double p = Stats.Get("combat/HeroSpecializations/Runemage/SacredHammer_ChanceToCastBase", 0.2) + Stats.Get("combat/HeroSpecializations/Runemage/SacredHammer_ChanceToCastPerLevel", 0.03) * l;
        var spellId = Stats.Str("combat/HeroSpecializations/Runemage/SacredHammer_SpellToCast", "SPELL_BLOODLUST");
        if (S.Rng.Chance(p) && Db.Spell(spellId) is { } sp)
        {
            Emit(new AbilityTriggered(Actor.Hero(h.Side), "HERO_SPEC_SACRED_HAMMER", target.Id));
            ApplyEffectSpell(Actor.Hero(h.Side), h.Side, sp, HeroMastery(h, sp), HeroSpellPower(h, sp), target, h.Level, h);
        }
    }
}
