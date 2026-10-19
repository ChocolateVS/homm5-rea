using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>
/// Hero specialisations and artifact-set effects confirmed against H5_Game.exe
/// (docs/rules/audit/combat_heroes_specs.md).
/// </summary>
[Collection("game")]
public class AuditSpecSetTests(GameDataFixture f)
{
    (BattleState s, Battle b) Setup()
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot("CREATURE_FOOTMAN", 10) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot("CREATURE_IMP", 10) } },
            7, skipTactics: true, arena: "Grass_Big_01");
        return (s, new Battle(f.Db, s));
    }

    static Unit Of(BattleState s, string id) => s.Units.First(u => u.CreatureId == id);
    double Stat(string path, double def) => f.Db.Stats.Get(path, def);

    // exe 008a4330 → 004e4270: the effect's own caster hero (effect vf 0xc) is passed to the factor, so an enemy
    // Expediter's Haste would add its bonus, and our own (non-Expediter) hero's level is irrelevant.
    [Fact]
    public void ExpediterBonusFollowsTheCasterOfHaste()
    {
        var (s, b) = Setup();
        var footman = Of(s, "CREATURE_FOOTMAN");
        s.Heroes[0].Specialization = "";
        s.Heroes[1].Specialization = "HERO_SPEC_HASTE";
        s.Heroes[1].Level = 10;
        footman.Effects.Add(new Effect { Id = "SPELL_HASTE", Value = 0.1, Remaining = 3, SourceSide = 1 });
        double per = Math.Clamp(Stat("combat/HeroSpecializations/Knight/Expediter_HasteBonusPerLevel", 0.01), 0, 100);
        Assert.Equal(1 + 0.1 + 10 * per, b.EffectInitiativeFactors(footman, s.Heroes[0]), 9);

        footman.Effects[0].SourceSide = 0;   // cast by our non-Expediter hero: no bonus
        Assert.Equal(1.1, b.EffectInitiativeFactors(footman, s.Heroes[0]), 9);
    }

    // exe 0085f0f0: Bombardier fireball power = integer (level − 1)·PerLevel / Divisor + Base (−1, /5, ×1), unclamped;
    // the engine used Base + floor(level/Divisor)·PerLevel clamped at 0.
    [StockFact]
    public void BombardierPowerUsesLevelMinusOne()
    {
        var (_, b) = Setup();
        Assert.Equal(-1, b.BombardierPower(1));
        Assert.Equal(-1, b.BombardierPower(5));
        Assert.Equal(0, b.BombardierPower(6));
        Assert.Equal(0, b.BombardierPower(10));
        Assert.Equal(1, b.BombardierPower(11));
    }

    // exe FUN_0085fe60: base Portion[mastery−1]; Gate Keeper adds level × per-level; the Demonic 2-piece set multiplies
    // only the base portion (not the bonuses).
    [Fact]
    public void GatingPortionAddsBonusesAndScalesOnlyTheBase()
    {
        var (s, b) = Setup();
        var h = s.Heroes[0];
        var imp = Of(s, "CREATURE_IMP");
        var portions = f.Db.Stats.Array("combat/HeroSkills/DemonLord/Gating/PortionOfSummonedByGatingLevel");
        h.Skills = new() { new SkillEntry("HERO_SKILL_GATING", Mastery.Advanced) };
        h.Perks = new();
        h.Specialization = "HERO_SPEC_GATE_KEEPER";
        h.Level = 20;
        h.SetPieces = new();
        double gk = 20 * Stat("combat/HeroSkills/DemonLord/Gating/GatekeeperHeroSpecPerLevelPortionBonus", 0.01);
        Assert.Equal(portions[1] + gk, b.GatingPortion(imp, h), 9);

        h.SetPieces[Specials.Demonic] = 2;
        int pct = (int)Stat("ArtifactsSetsEffectsConsts/Demonic_2DemonLord_GatingBonusPercents", 25);
        Assert.Equal(portions[1] * (1 + pct * 0.01) + gk, b.GatingPortion(imp, h), 9);
    }

    // exe 00989fd0 case 0x3b: Death Scream morale = trunc(MoralePenalty · f), f = Dark Emissary multiplier (else 1)
    // plus BansheeWhail_DeMorale with 2+ Necromancers pieces — so Dark Emissary with the set gives 1·(2+1) = 3.
    [StockFact]
    public void DeathScreamMoraleCombinesDarkEmissaryAndTheSet()
    {
        var (s, b) = Setup();
        var h = s.Heroes[0];
        h.SetPieces = new();
        h.Specialization = "";
        Assert.Equal(1, b.DeathScreamMoralePenalty(h));
        h.Specialization = "HERO_SPEC_DARK_EMISSARY";
        Assert.Equal(2, b.DeathScreamMoralePenalty(h));
        h.SetPieces[Specials.Necromancers] = 2;
        Assert.Equal(3, b.DeathScreamMoralePenalty(h));
        h.Specialization = "";
        Assert.Equal(2, b.DeathScreamMoralePenalty(h));
    }

    // exe 004e4270 (Death Scream effect): initiative factor 1 − InitiativePenalty × (screaming hero has 2+
    // Necromancers pieces ? BansheeWhail_DeInitiativeMultiplier : 1); Dark Emissary plays no part.
    [Fact]
    public void DeathScreamInitiativeDoublesWithTheNecromancersSet()
    {
        var (s, b) = Setup();
        var footman = Of(s, "CREATURE_FOOTMAN");
        footman.Effects.Add(new Effect { Id = "FX_BANSHEE_HOWL", Value = 1, Remaining = 3, SourceSide = 1 });
        s.Heroes[1].SetPieces = new();
        double pen = Stat("combat/HeroSkills/Necromancer/DeathScream_InitiativePenalty", 0.1);
        Assert.Equal(1 - pen, b.EffectInitiativeFactors(footman, s.Heroes[0]), 9);
        s.Heroes[1].SetPieces[Specials.Necromancers] = 2;
        int mult = (int)Stat("ArtifactsSetsEffectsConsts/Necromancers_2Necromancer_BansheeWhail_DeInitiativeMultiplier", 2);
        Assert.Equal(1 - mult * pen, b.EffectInitiativeFactors(footman, s.Heroes[0]), 9);
    }

    // exe 008a4800 / 008a22f0: with SPELL_ARTFSET_NECROMANCERS_DEBUFF attack and defence are trunc(value·(1 − 20/100)).
    [Fact]
    public void NecromancersDebuffCutsAttackAndDefence()
    {
        var (s, b) = Setup();
        var footman = Of(s, "CREATURE_FOOTMAN");
        int a = b.AttackOf(footman), d = b.DefenceOf(footman);
        footman.Effects.Add(new Effect { Id = "SPELL_ARTFSET_NECROMANCERS_DEBUFF", SourceSide = 1, Dispellable = false });
        int pct = (int)Stat("ArtifactsSetsEffectsConsts/Necromancers_4_EnemyCreature_BadMoralePenaltyToAttackDefencePercents", 20);
        Assert.Equal((int)((1 - pct * 0.01) * a), b.AttackOf(footman));
        Assert.Equal((int)((1 - pct * 0.01) * d), b.DefenceOf(footman));
    }

    // exe 008cf7ff / 00989fd0 case 0x129: a Knight with 2+ Lions pieces marks its strike targets once (permanent);
    // the mark lowers morale by Lions_2_HeroAttack_DeMorale.
    [Fact]
    public void LionsSetDemoralisesTheKnightsTargets()
    {
        var (s, b) = Setup();
        var h = s.Heroes[0];
        var imp = Of(s, "CREATURE_IMP");
        int before = b.MoraleOf(imp);
        h.Class = "HERO_CLASS_KNIGHT";
        h.SetPieces = new() { [Specials.Lions] = 1 };
        b.LionsMark(h, imp);
        Assert.False(imp.HasEffect("SPELL_ARTFSET_LIONS_DEMORALIZED"));
        h.SetPieces[Specials.Lions] = 2;
        b.LionsMark(h, imp);
        b.LionsMark(h, imp);
        Assert.Single(imp.Effects, e => e.Id == "SPELL_ARTFSET_LIONS_DEMORALIZED");
        Assert.Equal(before - (int)Stat("ArtifactsSetsEffectsConsts/Lions_2_HeroAttack_DeMorale", 2), b.MoraleOf(imp));
        h.Class = "HERO_CLASS_WIZARD";
        var footman = Of(s, "CREATURE_FOOTMAN");
        b.LionsMark(h, footman);
        Assert.False(footman.HasEffect("SPELL_ARTFSET_LIONS_DEMORALIZED"));
    }

    // exe FUN_00bc1eb0: Feather Mage hero initiative ×(1 + clamp(Base + level·PerLevel, 0, 5)).
    [Fact]
    public void FeatherMageInitiativeIsClamped()
    {
        var (s, b) = Setup();
        var h = s.Heroes[0];
        h.Specialization = "HERO_SPEC_FEATHER_MAGE";
        h.Level = 20;
        double per = Stat("combat/HeroSpecializations/Wizard/FeatherMage_InitiativeBonusPerLevel", 0.005);
        double baseB = Stat("combat/HeroSpecializations/Wizard/FeatherMage_InitiativeBonusBase", 0);
        double hi = Stat("HeroInitiative", 10);
        Assert.Equal(hi * (1 + Math.Clamp(baseB + 20 * per, 0, 5)), b.HeroInitiativeOf(0), 9);
        h.Level = 5000;   // clamp at +500%
        Assert.Equal(hi * 6, b.HeroInitiativeOf(0), 9);
    }

    // exe 00a5c300: Blooddrinker attack per kill = integer ((level − 1) / 6)·1 + 1 (was 1 + floor(level/6)).
    [StockFact]
    public void LevelFormulaUsesLevelMinusOne()
    {
        const string path = "combat/HeroSpecializations/Barbarian/Blooddrinker_OffenceBonusPerStack";
        Assert.Equal(1, Specials.LevelFormula(f.Db.Stats, path, 1, 1, 6, 1));
        Assert.Equal(1, Specials.LevelFormula(f.Db.Stats, path, 6, 1, 6, 1));
        Assert.Equal(2, Specials.LevelFormula(f.Db.Stats, path, 7, 1, 6, 1));
        Assert.Equal(3, Specials.LevelFormula(f.Db.Stats, path, 13, 1, 6, 1));
    }

    // exe 009844e0 / 00bbebd0: Poisoner and Sacred Hammer chances are trunc(100·clamp(Base + level·PerLevel, 0, 1)).
    [StockFact]
    public void PoisonerAndSacredHammerChancesAreClampedWholePercents()
    {
        var (_, b) = Setup();
        Assert.Equal((int)(100 * Math.Clamp(0.1 + 7 * 0.02, 0, 1)), b.PoisonerChance(7));
        Assert.Equal(100, b.PoisonerChance(200));
        Assert.Equal((int)(100 * Math.Clamp(0.2 + 7 * 0.03, 0, 1)), b.SacredHammerChance(7));
        Assert.Equal(100, b.SacredHammerChance(200));
    }

    // exe 00983790 @ 0098431f: Mind Cleaner blocks the enemy hero's spell with chance 100/max(1, circle+1) − caster
    // level + Mind Cleaner level; a blocked spell is gone from the caster's book for the combat.
    [Fact]
    public void MindCleanerBlocksTheEnemySpell()
    {
        var (s, b) = Setup();
        var caster = s.Heroes[0];
        var mc = s.Heroes[1];
        mc.Specialization = "HERO_SPEC_MIND_CLEANER";
        mc.Level = 400;   // chance ≥ 100 → always blocks
        caster.Level = 1;
        var sp = f.Db.Spell("SPELL_MAGIC_ARROW")!;
        if (!caster.Spells.Contains(sp.Id)) caster.Spells.Add(sp.Id);
        Assert.Contains(sp.Id, b.HeroSpells(caster));
        b.MindCleanerCheck(caster, sp);
        Assert.DoesNotContain(sp.Id, b.HeroSpells(caster));

        caster.AbilityUses.Clear();
        mc.Level = 1; caster.Level = 400;   // chance ≤ 0 → never
        b.MindCleanerCheck(caster, sp);
        Assert.Contains(sp.Id, b.HeroSpells(caster));
    }
}
