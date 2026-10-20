using H5.Core.Data;

namespace H5.Combat;

// Combat audit sweep 4 (docs/rules/audit/combat_sweep_4.md): magic / rune hero perks re-checked against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>Warlock's Luck multiplier of the hero cast being resolved (1 outside such a cast).</summary>
    double _warlockLuck = 1;

    /// <summary>
    /// Spells Warlock's Luck applies to (exe FUN_004e5cb0, on the base spell FUN_00a34110): Magic Arrow … Armageddon,
    /// Unholy / Holy Word, Land Mine, Wasp Swarm, Explosion, Fire Shield, Chilling Bones, Firewall, Stone Spikes, the boss
    /// spells, Magma Shield, Deep Freeze, Divine Vengeance, Shout of Many, Call Storm, Liquid Flame and — only for a caster
    /// with HERO_SPEC_DISRUPTER (vf 0x294(0x2a)) — Disrupting Ray.
    /// </summary>
    static readonly HashSet<string> WarlocksLuckSpells = new()
    {
        "SPELL_MAGIC_ARROW", "SPELL_MAGIC_FIST", "SPELL_LIGHTNING_BOLT", "SPELL_ICE_BOLT", "SPELL_FIREBALL", "SPELL_FROST_RING",
        "SPELL_CHAIN_LIGHTNING", "SPELL_METEOR_SHOWER", "SPELL_IMPLOSION", "SPELL_ARMAGEDDON", "SPELL_UNHOLY_WORD", "SPELL_HOLY_WORD",
        "SPELL_LAND_MINE", "SPELL_WASP_SWARM", "SPELL_ABILITY_EXPLOSION", "SPELL_EFFECT_FIRE_SHIELD", "SPELL_SKILL_CHILLING_BONES",
        "SPELL_FIREWALL", "SPELL_STONE_SPIKES", "SPELL_UBER_CHAIN_LIGHTNING", "SPELL_BOSS_FIREWALL", "SPELL_EFFECT_MAGMA_SHIELD",
        "SPELL_DEEP_FREEZE", "SPELL_DIVINE_VENGEANCE", "SPELL_UBER_METEOR_SHOWER", "SPELL_WARCRY_SHOUT_OF_MANY",
        "SPELL_ABILITY_CALL_STORM", "SPELL_EFFECT_LIQUID_FLAME_BREATH",
    };

    internal static bool WarlocksLuckApplies(string baseSpell, HeroState h) =>
        WarlocksLuckSpells.Contains(baseSpell) || baseSpell == "SPELL_DISRUPTING_RAY" && h.Specialization == "HERO_SPEC_DISRUPTER";

    /// <summary>
    /// Warlock's Luck (exe FUN_00983790 @ 00983ab3..00983b6c): rolled ONCE per hero cast, before the targets are resolved,
    /// with the hero's own luck roll (hero vf 0x1d0 = FUN_00a6b820: luck clamped to ±5, ability roll at 10·|luck| %; good →
    /// FUN_00a6a680 = GoodLuck_DamageMultiplier, or ElvenLuck_DamageMultiplier with Elven Luck; bad → BadLuck multiplier).
    /// A result ≤ 1 becomes 1 — bad luck is rolled but never reduces the spell. The multiplier goes into the cast record
    /// (+0x34, FUN_004e6570) and multiplies every target's damage after protection (FUN_0097d600).
    /// </summary>
    double RollWarlocksLuck(HeroState h, SpellDef sp)
    {
        if (!h.Present || !h.HasPerk(Sk.WarlocksLuck) || !WarlocksLuckApplies(BaseSpell(sp.Id), h)) return 1;
        int luck = Math.Clamp(h.Luck, -5, 5);
        double result = 1;
        if (luck > 0 && RollPercent(luck * 10))
            result = h.HasPerk(Sk.ElvenLuck) ? Stats.Get("combat/HeroSkills/Ranger/ElvenLuck_DamageMultiplier", 2.25)
                                             : Stats.Get("combat/LuckAndMorale/GoodLuck_DamageMultiplier", 2);
        else if (luck < 0) RollPercent(-luck * 10);   // the bad-luck roll is made; its multiplier (< 1) is discarded
        if (result <= 1) return 1;
        Emit(new LuckProc(Actor.Hero(h.Side), true));
        return result;
    }

    /// <summary>FUN_0097d600: round(round((1 − protection)·damage) · luck) — both conversions under chop.</summary>
    internal static double ApplyWarlocksLuck(double protectedDamage, double luck) =>
        luck == 1 ? protectedDamage : ExeChop((float)protectedDamage * (float)luck);

    /// <summary>
    /// Mark of the Wizard (exe FUN_00983790 @ 00983e41..00983ec4 → FUN_009834b0): after a hero's cast of an aimed spell
    /// (record +0xcc), not a mass spell, not Teleport (0x20) or Hypnotize (0x14), the spell is cast once more on every stack
    /// holding the hero's mark (effect 0x3f, vf 0x30) — the main target included — when the hero can still pay it
    /// (vf 0x220 = FUN_00bc0030) and the stack is a valid target (FUN_00982010); each extra cast is paid like a normal one
    /// (vf 0x224 = FUN_00bbf610: cost with Erratic Mana). The engine used to double the damage of one cast instead.
    /// </summary>
    void MarkOfTheWizardRecast(int caster, int side, SpellDef sp, Mastery m, double power, bool fromHero)
    {
        var h = fromHero ? S.Heroes[side] : null;
        if (h == null || !sp.IsAimed || IsMass(sp.Id) || BaseSpell(sp.Id) is "SPELL_TELEPORT" or "SPELL_HYPNOTIZE") return;
        if (!h.Links.TryGetValue(LinkMarkOfWizard, out var mid) || S.Unit(mid) is not { Alive: true } marked) return;
        int cost = HeroManaCost(h, sp);
        if (h.Mana < cost) return;
        // The repeat is aimed at the marked stack and its cell (vf 0x40 → the record's target cell).
        Cell? cell = TargetingOf(sp) is SpellTargeting.Cell or SpellTargeting.UnitAndCell ? marked.Pos : null;
        if (ValidateSpellTarget(caster, side, sp, m, marked.Id, cell) != null) return;
        ResolveSpellCore(caster, side, sp, m, power, TargetingOf(sp) == SpellTargeting.Cell ? null : marked, cell, fromHero);
        h.Mana -= SkillAuditH.ErraticManaSpend(cost, h.HasPerk(Sk.ErraticMana), HeroRoll, S.Rng);
        Emit(new ManaChanged(Actor.Hero(side), h.Mana));
    }

    /// <summary>
    /// Empathy (hero interface vf 0x2c = FUN_00bbfbf0, called by the good / bad morale handlers FUN_00a6cf90 (1) /
    /// FUN_00a6cd80 (0) for the stack's own hero): with the perk — or 3+ pieces of the Lions set (vf 0x328(3) ≥ 3) — the
    /// hero's ATB moves by ±Empathy_ATBAffectionOnMorale, capped at 1.25 (FUN_00465610). The engine handled good morale only
    /// and capped at 1.
    /// </summary>
    internal void EmpathyOnMorale(Unit u, bool good)
    {
        var h = HeroOf(u);
        if (!h.Present || !(h.HasPerk(Sk.Empathy) || Specials.Pieces(h, Specials.Lions) >= 3)) return;
        float step = (float)Stats.Get("combat/HeroSkills/Runemage/Empathy_ATBAffectionOnMorale", 0.1);
        float atb = (float)h.Atb;
        SetAtb(Actor.Hero(h.Side), Math.Min(good ? atb + step : atb - step, 1.25f));
    }

    /// <summary>
    /// Eternal Light (effect flag +0x53 set by FUN_004e4c10 → FUN_004e45e0: a Light spell cast by a hero with the perk on a
    /// stack of its own side): FUN_0097c9f0 → FUN_004e41c0 gives an incoming opposite spell (FUN_004e3bc0: Curse/Bless,
    /// Slow/Haste, Weakness/Bloodlust, on the base spells) a 50 % resistance base, to which the usual resistance additions
    /// are added (one roll). The engine rolled a separate 50 % after the resistance roll.
    /// </summary>
    internal int EternalLightResistance(Unit t, SpellDef sp)
    {
        if (!Opposites.TryGetValue(BaseSpell(sp.Id), out var opp) || t.GetEffect(opp) is not { } oe) return 0;
        if (oe.School != "MAGIC_SCHOOL_LIGHT" || oe.SourceSide < 0 || oe.SourceSide != t.Side) return 0;
        return S.Heroes[oe.SourceSide].HasPerk(Sk.EternalLight) ? 50 : 0;
    }

    /// <summary>
    /// Detain Destructive / Dark / Light / Summoning on creature casts (exe FUN_008a11c0 → FUN_004e5f60): after a creature
    /// cast the ATB is set (0) and then lowered by DetainMagic_CasterExtraATBDrop when the opposing hero has the Detain perk
    /// of the spell's school — the engine applied it to hero casts only.
    /// </summary>
    internal double CreatureCastAtb(Unit u, SpellDef sp)
    {
        var e = Enemy(u.Side);
        string detain = sp.School switch
        {
            "MAGIC_SCHOOL_DESTRUCTIVE" => Sk.DetainDestructive, "MAGIC_SCHOOL_DARK" => Sk.DetainDark,
            "MAGIC_SCHOOL_LIGHT" => Sk.DetainLight, "MAGIC_SCHOOL_SUMMONING" => Sk.DetainSummoning, _ => "",
        };
        if (e.Present && detain.Length > 0 && e.HasPerk(detain))
            return 0f - (float)Stats.Get("combat/HeroSkills/Barbarian/DetainMagic_CasterExtraATBDrop", 0.25);
        return 0;
    }
}
