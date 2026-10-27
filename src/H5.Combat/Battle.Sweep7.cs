using H5.Core.Data;

namespace H5.Combat;

// Combat audit sweep 7 (docs/rules/audit/combat_sweep_7.md): creature spell effects re-checked against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// Counterspell can be armed (exe FUN_0097c320 case 0x41): the hero's side faces a hero, the hero is not armed yet
    /// (effect 0x41), it has at least 1 mana (vf 0x234) and the enemy hero's class (vf 0x258) is not
    /// HERO_CLASS_BARBARIAN (8).
    /// </summary>
    internal bool CounterspellArmable(HeroState h)
    {
        var e = Enemy(h.Side);
        return e.Present && h.Mana >= 1 && e.Class != "HERO_CLASS_BARBARIAN";
    }

    /// <summary>
    /// Spells Counterspell can stop (exe FUN_00978670: FUN_00a349f0 and not FUN_00a345e0 on the spell record +0x88): every
    /// school but Warcries, Special and None, and never SPELL_ABILITY_DARK_RITUAL (0x43).
    /// </summary>
    static bool Counterable(SpellDef sp) =>
        sp.Id != "SPELL_ABILITY_DARK_RITUAL" && sp.School is not ("MAGIC_SCHOOL_WARCRIES" or "MAGIC_SCHOOL_SPECIAL" or "MAGIC_SCHOOL_NONE" or "");

    /// <summary>
    /// Invisibility can be cast (exe FUN_0097c320 case 0x13d): a charge is left, the stack is not invisible already and it
    /// is not next to an enemy (aura flag 1 of FUN_008a4b70 = an enemy among the neighbours, tested by vf 0x21c(1)).
    /// </summary>
    internal bool NextToEnemy(Unit u) => S.LivingOf(1 - Controller(u)).Any(e => !e.IsObject && AreAdjacent(u, e));

    /// <summary>
    /// Fear's immunity (exe FUN_00a5e1b0): the spell application check FUN_00982010 for SPELL_ABILITY_FEAR (0xa0) — the
    /// FUN_0097c9f0 table (Undead, Mechanical, Elemental, Immunity to Mind Control), Immunity to Magic, war machines,
    /// Anti-Magic, the target hero's artifacts — not the bare mind immunity.
    /// </summary>
    internal bool FearImmune(Unit t) =>
        Db.Spell("SPELL_ABILITY_FEAR") is { } sp ? ExeSpellImmune(t, sp, sp.Id, null) : MindImmune(t);

    /// <summary>
    /// Bear Roar's victims (exe FUN_00a5e800): skipped when FUN_0097d2a0 gives SPELL_ABILITY_BEAR_ROAR (0x147) 100 % —
    /// the FUN_0097c9f0 immunities (ExeSpellImmune).
    /// </summary>
    internal bool BearRoarImmune(Unit t) =>
        Db.Spell("SPELL_ABILITY_BEAR_ROAR") is { } sp ? ExeSpellImmune(t, sp, sp.Id, null) : MindImmune(t);

    /// <summary>
    /// Bear Roar's health (exe FUN_008a2130): under effect 0x147 the per-creature health (record health + the flat bonus
    /// FUN_008d9390) becomes trunc((BearRoar_HealthBonus + 1.0001f) · hp) — x87 sum of the two floats, chop (OR AH,0x0C).
    /// </summary>
    internal static int BearRoarHealth(int hp, double healthBonus) =>
        (int)(((double)(float)healthBonus + (double)1.0001f) * hp);

    /// <summary>
    /// Sleeping Strike's maximum damage (exe FUN_00a597a0): against a target under effect 0x150, an attacker whose creature
    /// family (FUN_004babf0: BaseCreature when set, else its own id) is CREATURE_VAMPIRE (0x23) uses its maximum damage —
    /// Vampires, Vampire Lords and Nosferatu.
    /// </summary>
    internal bool VampireFamily(Unit a) =>
        a.Kind == UnitKind.Creature && (a.CreatureId == "CREATURE_VAMPIRE"
            || Db.Creatures.TryGetValue(a.CreatureId, out var d) && d.BaseCreature == "CREATURE_VAMPIRE");

    /// <summary>
    /// Rage of the Forest's defence (exe FUN_008a32b0): the full defence d (FUN_008a22f0) becomes d − d/2 (integer); the
    /// attack (FUN_008a4800) gains d/2 of the same full defence — both from the current value, not from the defence at the cast.
    /// </summary>
    internal static int RageOfForestDefence(int d) => d - d / 2;

    /// <summary>
    /// Power Feed can be used (exe FUN_0097c1d0): the stack has its own hero, is controlled by its owner (vf 0x18 == vf 0xc),
    /// the Ranger-only rule (+0x2dc) holds, and the hero does not carry effect 0x14d already — from any stack (the effect
    /// goes with the feeding stack's death).
    /// </summary>
    internal bool PowerFeedAvailable(Unit u, HeroState h) =>
        h.Present && Controller(u) == u.Side
        && !(h.Links.TryGetValue(Ab.PowerFeed, out var linked) && S.Unit(linked) is { Alive: true })
        && (!Stats.Bool("combat/Creatures/Sylvan/PowerFeedOnlyRanger") || h.Class == "HERO_CLASS_RANGER");

    /// <summary>
    /// Start of a creature's turn (exe FUN_00bbcb40 / FUN_00bbcaa0 → FUN_00bbc8e0): Battle Frenzy (0x112) is removed
    /// (vf 0x270 with force 1 — the 0x7ffffffe duration does not protect it), and a stack with Defile Magic (0x6d) releases
    /// every SPELL_ABILITY_DEFILE_MAGIC (0x146) it put on others (vf 0x30 list, vf 0x270, links cleared by vf 0x2a4).
    /// </summary>
    internal void Sweep7TurnStart(Unit u)
    {
        u.FrenzyMultiplier = 1;
        RemoveEffect(u, H5.Combat.Fx.BerserkerRage);   // exe 00bbc951: vf 0x270(0xf5, time, force 1)
        if (HasAbilityNow(u, Ab.DefileMagic))
            foreach (var v in S.Units.Where(v => v.GetEffect(FxDefiled)?.SourceUnit == u.Id).ToList())
                RemoveEffect(v, FxDefiled);
    }

    /// <summary>
    /// SPELL_EFFECT_MASTER_OF_LIGHTNING (exe FUN_004e5c20): ATB a = clamp(ATB, 0, 1), then a − a · EmpoweredLightningATBDrop
    /// in single precision.
    /// </summary>
    internal void MasterOfLightningAtb(Unit t)
    {
        float a = Math.Clamp((float)GetAtb(t.Id), 0f, 1f);
        SetAtb(t.Id, a - a * (float)Stats.Get("combat/HeroSkills/EmpoweredLightningATBDrop", 0.3));
    }
}
