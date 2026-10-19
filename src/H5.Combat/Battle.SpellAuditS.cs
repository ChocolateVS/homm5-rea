using H5.Core.Data;

namespace H5.Combat;

// Spells audit part S (docs/rules/audit/combat_spells_s.md): spell immunities per H5_Game.exe FUN_0097c9f0.
public sealed partial class Battle
{
    /// <summary>
    /// Spells FUN_004e5cb0 counts as damage spells (base ids): they are the ones Irresistible Magic lets through an immunity
    /// (FUN_0097d2a0) and the only ones that test the element immunities. Disrupting Ray counts with a Disrupter caster.
    /// </summary>
    static readonly HashSet<string> ExeDamageSpells = new()
    {
        "SPELL_MAGIC_ARROW", "SPELL_MAGIC_FIST", "SPELL_LIGHTNING_BOLT", "SPELL_ICE_BOLT", "SPELL_FIREBALL", "SPELL_FROST_RING",
        "SPELL_CHAIN_LIGHTNING", "SPELL_METEOR_SHOWER", "SPELL_IMPLOSION", "SPELL_ARMAGEDDON", "SPELL_UNHOLY_WORD", "SPELL_HOLY_WORD",
        "SPELL_LAND_MINE", "SPELL_WASP_SWARM", "SPELL_ABILITY_EXPLOSION", "SPELL_EFFECT_FIRE_SHIELD", "SPELL_SKILL_CHILLING_BONES",
        "SPELL_FIREWALL", "SPELL_STONE_SPIKES", "SPELL_UBER_CHAIN_LIGHTNING", "SPELL_BOSS_FIREWALL", "SPELL_EFFECT_MAGMA_SHIELD",
        "SPELL_DEEP_FREEZE", "SPELL_DIVINE_VENGEANCE", "SPELL_UBER_METEOR_SHOWER", "SPELL_WARCRY_SHOUT_OF_MANY",
        "SPELL_ABILITY_CALL_STORM", "SPELL_EFFECT_LIQUID_FLAME_BREATH",
    };

    internal bool ExeDamageSpell(string id, HeroState? hero) =>
        ExeDamageSpells.Contains(id) || id == "SPELL_DISRUPTING_RAY" && hero != null && Spec(hero, "HERO_SPEC_DISRUPTER") >= 0;

    /// <summary>
    /// Spells that may hit a war machine or a spawned object (FUN_0097c9f0, targets whose vf 0x7c / 0x8c — the war machine
    /// and spawn casts — are non-null): spells 1..10, Explosion, Call Lightning, Firewall, Stone Spikes, the boss Chain
    /// Lightning / Firewall / Meteor Shower, and Disrupting Ray with a Disrupter caster. Everything else is 100 %.
    /// </summary>
    static readonly HashSet<string> ExeWarMachineSpells = new()
    {
        "SPELL_MAGIC_ARROW", "SPELL_MAGIC_FIST", "SPELL_LIGHTNING_BOLT", "SPELL_ICE_BOLT", "SPELL_FIREBALL", "SPELL_FROST_RING",
        "SPELL_CHAIN_LIGHTNING", "SPELL_METEOR_SHOWER", "SPELL_IMPLOSION", "SPELL_ARMAGEDDON", "SPELL_ABILITY_EXPLOSION",
        "SPELL_ABILITY_CALL_LIGHTNING", "SPELL_FIREWALL", "SPELL_STONE_SPIKES", "SPELL_UBER_CHAIN_LIGHTNING", "SPELL_BOSS_FIREWALL",
        "SPELL_UBER_METEOR_SHOWER",
    };

    internal bool ExeWarMachineSpell(string id, HeroState? hero) =>
        ExeWarMachineSpells.Contains(id) || id == "SPELL_DISRUPTING_RAY" && hero != null && Spec(hero, "HERO_SPEC_DISRUPTER") >= 0;

    /// <summary>
    /// The creature HasAbility for the derived immunities (FUN_008a34c0 → FUN_00b61950 with the flags of FUN_00b618a0, taken
    /// from the stack's own ability list: [0] Undead, [1] Mechanical, [2] Elemental, [3] Immunity to Magic): Immunity to
    /// Blind and to Mind Control hold for any of the four, Immunity to Slow / Air / Fire / Water / Earth for Immunity to
    /// Magic. Shawl of the Great Lich (artifact 0x45) on the opposing hero cancels Immunity to Mind Control outright, the
    /// derived one included (checked first, 008a35c0). Vampirism's dynamic Undead does not feed the flags.
    /// </summary>
    internal bool HasAbilityExe(Unit u, string ability)
    {
        if (ability == Ab.ImmuneMind && Enemy(u.Side) is { Present: true } e && e.Artifacts.Contains("ARTIFACT_SHAWL_OF_GREAT_LICH"))
            return false;
        if (u.Has(ability)) return true;
        bool magic = u.Abilities.Contains(Ab.ImmuneMagic);
        return ability switch
        {
            Ab.ImmuneBlind or Ab.ImmuneMind => magic || u.Abilities.Contains(Ab.Undead) || u.Abilities.Contains(Ab.Mechanical)
                                               || u.Abilities.Contains(Ab.Elemental),
            Ab.ImmuneSlow or Ab.ImmuneAir or Ab.ImmuneFire or Ab.ImmuneWater or Ab.ImmuneEarth => magic,
            _ => false,
        };
    }

    bool RuneElementImmune(Unit u, string element) =>
        element != Elements.None && u.GetEffect(H5.Combat.Fx.RuneElementalImmunity) is { } ri
        && (ExeElement((int)ri.Value) == element || ExeElement((int)ri.Value2) == element);

    bool ElementAbilityImmune(Unit u, string element) => element switch
    {
        Elements.Air => HasAbilityExe(u, Ab.ImmuneAir), Elements.Fire => HasAbilityExe(u, Ab.ImmuneFire),
        Elements.Water => HasAbilityExe(u, Ab.ImmuneWater), Elements.Earth => HasAbilityExe(u, Ab.ImmuneEarth),
        _ => false,
    };

    /// <summary>
    /// True when FUN_0097c9f0 gives the spell 100 % on the target (immune — no resistance roll; FUN_0097d2a0 then lets only
    /// Irresistible Magic's share of a damage spell through). <paramref name="id"/> is the base spell (FUN_00a34110).
    /// Order as in the exe: Holy / Unholy Word kinds (FUN_00977600) → Magic Fist / Weakening Strike never immune →
    /// Immunity to Magic or Rune of Elemental Immunity for the spell's element (any spell) → Enchanted Obsidian against
    /// every non-damage spell → war machines and spawns → the per-spell table → Armored vs Disrupting Ray (FUN_004e5e60) →
    /// Anti-Magic (FUN_004e41c0) → the target hero's artifacts → element immunities, for damage spells and Plague only.
    /// </summary>
    internal bool ExeSpellImmune(Unit t, SpellDef sp, string id, HeroState? hero)
    {
        if (id == "SPELL_UNHOLY_WORD" && (t.Has(Ab.Undead) || t.Has(Ab.Demonic))) return true;
        if (id == "SPELL_HOLY_WORD" && !t.Has(Ab.DemonRaged) && !t.Has(Ab.Undead) && !t.Has(Ab.Demonic)) return true;
        if (id is "SPELL_MAGIC_FIST" or "SPELL_ABILITY_WEAKNING_STRIKE") return false;
        if (t.Has(Ab.ImmuneMagic) || RuneElementImmune(t, sp.Element)) return true;
        if (t.Has(Ab.EnchantedObsidian) && !ExeDamageSpell(id, hero)) return true;
        if ((t.IsWarMachine || t.IsObject) && !ExeWarMachineSpell(id, hero)) return true;
        bool undead = t.Has(Ab.Undead), mech = t.Has(Ab.Mechanical), elem = t.Has(Ab.Elemental);
        bool table = id switch
        {
            "SPELL_CURSE" => HasAbilityExe(t, Ab.ImmuneCurse),
            "SPELL_SLOW" => HasAbilityExe(t, Ab.ImmuneSlow),
            "SPELL_PLAGUE" or "SPELL_EFFECT_SLEEPING_STRIKE" => undead || mech || elem,
            "SPELL_FORGETFULNESS" => HasAbilityExe(t, Ab.ImmuneMind),
            "SPELL_BERSERK" => HasAbilityExe(t, Ab.ImmuneMind) || HasAbilityExe(t, Ab.ImmuneBerserk),
            "SPELL_BLIND" => HasAbilityExe(t, Ab.ImmuneBlind),
            "SPELL_HYPNOTIZE" => HasAbilityExe(t, Ab.ImmuneMind) || HasAbilityExe(t, Ab.ImmuneHypnotize),
            "SPELL_WASP_SWARM" or "SPELL_REGENERATION" or "SPELL_RESURRECT" => undead || elem || mech,
            "SPELL_PHANTOM" => t.IsClone,
            "SPELL_ANIMATE_DEAD" or "SPELL_VAMPIRISM" => elem || mech,
            "SPELL_ABILITY_FEAR" or "SPELL_ABILITY_BEAR_ROAR" => undead || mech || elem || HasAbilityExe(t, Ab.ImmuneMind),
            "SPELL_BOSS_FIREWALL" => t.Has(Ab.Demonic),
            _ => false,
        };
        if (table) return true;
        if (t.Has(Ab.Armored) && id == "SPELL_DISRUPTING_RAY" && !ExeDamageSpell(id, hero)) return true;
        if (t.GetEffect("SPELL_ANTI_MAGIC") is { } am && sp.Level <= (int)am.Value) return true;
        if (ArtifactSpellImmunity(t, id)) return true;
        return (ExeDamageSpell(id, hero) || id == "SPELL_PLAGUE") && ElementAbilityImmune(t, sp.Element);
    }

    /// <summary>
    /// Summon Elementals' count (exe FUN_00862800, chop mode): trunc(max(1, Eval(power))), then with Fire Affinity
    /// trunc(count · 1.4f) (DAT_00e4c194). The engine multiplied before truncating. A further halving (count / 2, at least 1)
    /// when a hero object reached through the caster's vf 0x10 → 0xc → 0 has vf 0x318 &gt; 0 is not identified (not ported).
    /// </summary>
    internal static int SummonElementalsCount(SpellDef baseDef, Mastery m, double power, HeroState? hero)
    {
        float e = (float)baseDef.DamageAt(m).Eval(power);
        int count = (int)Math.Max(e, 1f);
        if (hero != null && hero.HasPerk("HERO_SKILL_FIRE_AFFINITY")) count = (int)(count * 1.4f);
        return count;
    }

    /// <summary>The counter-summon element of FUN_00861120's pair table: Fire ↔ Water, Earth ↔ Air (creatures 0x55..0x58).</summary>
    internal static string OppositeElemental(string kind) => kind switch
    {
        "CREATURE_FIRE_ELEMENTAL" => "CREATURE_WATER_ELEMENTAL", "CREATURE_WATER_ELEMENTAL" => "CREATURE_FIRE_ELEMENTAL",
        "CREATURE_EARTH_ELEMENTAL" => "CREATURE_AIR_ELEMENTAL", "CREATURE_AIR_ELEMENTAL" => "CREATURE_EARTH_ELEMENTAL",
        _ => kind,
    };

    /// <summary>
    /// Elemental Balance (skill 0x54): when a hero summons elementals or a phoenix and the summon succeeded, the opposing hero
    /// with the skill gets the same summon on its side (exe FUN_00861630 tail / FUN_00862130: opponent via caster vf 0x10 →
    /// vf 0xc, HeroHasSkill(0x54), FUN_00861500 / FUN_00861340 with that hero as summoner; logged as skill 0x54).
    /// </summary>
    Unit? ElementalBalanceSummon(int casterSide, string kind, int count, string by)
    {
        var e = Enemy(casterSide);
        if (!e.Present || !e.HasPerk("HERO_SKILL_ELEMENTAL_BALANCE")) return null;
        var u = SummonStack(1 - casterSide, kind, Math.Max(1, count), by, atb: S.Rng.Next(6) * 0.05);
        if (u != null) Emit(new AbilityTriggered(Actor.Hero(1 - casterSide), "HERO_SKILL_ELEMENTAL_BALANCE", u.Id));
        return u;
    }

    /// <summary>
    /// Rune of Magic Control (exe FUN_00a5ca80, decoded from the disassembly 00a5caa8..00a5cb46): when the rune holder attacks,
    /// FUN_004e6470 / FUN_004e4000 list the target's effects that count as beneficial for its side — dispellable, timed
    /// (not a whole-battle duration), not a warcry effect, not in FUN_004e3ac0's never-dispelled list, of any school; one is
    /// picked with the battle RNG (Random(n)), added to the attacker (vf 0x264) and removed from the target (vf 0x270).
    /// The rune itself is removed by FUN_00a5cb80 together with Rune of Battle Rage. The engine took Light-school effects only.
    /// </summary>
    internal static bool MagicControlCandidate(Effect e) =>
        e.Positive && e.Dispellable && !double.IsPositiveInfinity(e.Remaining) && !NeverDispelled.Contains(e.Id)
        && e.Id is not (H5.Combat.Fx.RallyingCry or H5.Combat.Fx.Battlecry);

    /// <summary>
    /// Arcane Crystal explosion (exe FUN_00a60e20, from the death handler FUN_00a61310; decoded from the disassembly
    /// 00a60e4f..00a6105d): damage = trunc(Eval(the crystal spell's damage table at the caster's mastery, caster's power))
    /// in chop mode — stored at creation; every creature, war machine or spawn of either army adjacent to the crystal
    /// (FUN_00af4450 lists both armies, FUN_00b3d3f0 the footprint adjacency) takes it straight through the stack damage
    /// call (no resistance or protection).
    /// </summary>
    void ArcaneCrystalExplosion(Unit crystal)
    {
        int dmg = crystal.Power;
        if (dmg <= 0) return;
        Emit(new AbilityTriggered(crystal.Id, "SPELL_ARCANE_CRYSTAL"));
        foreach (var u in S.Living.Where(o => o.Id != crystal.Id && AreAdjacent(crystal, o)).ToList())
            if (u.Alive) DealDamage(u, dmg, crystal.Id, AttackKind.Other, "ARCANE_CRYSTAL");
    }
}
