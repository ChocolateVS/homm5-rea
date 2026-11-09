using H5.Core.Data;
namespace H5.Combat;

// Combat audit sweep 17 (docs/rules/audit/combat_sweep_17.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// FUN_00a34740(spell, hero, week) — the cost the combat cost 009781e0 starts with (ECX = the entry's spell, EDX = the
    /// caster's vf 0x78 hero interface, null for a creature), the hive's 008b07f0 / 008b08a0 and the adventure cast
    /// (<c>Adventure.AdventureSpellCost(hero, spell)</c>, the same chain). The record is the base of a mass (00a33e20) or
    /// empowered (00a33f70) version, else the spell (00a3474a..00a34768; no record → 0); cost = its +0x90 (TrainedCost);
    /// SPELL_ABILITY_AVATAR_OF_DEATH (0xc8) with a hero: hero level · RPGStats +0x888 (AvatarOfDeath_ManaCostPerLevel,
    /// 00a34785..00a3479b); × 2 for a mass / empowered version (00a347b1); the week (00bcddf0). Warcries (the spell's own
    /// record +0x88 = 6): with a hero, Voice Training (vf 0x174(0xbc) &gt; 0) chop(min((1.001f − d) · c, c − 1)), War Leader
    /// (vf 0x190(0x4b)) − (level / div · per + base); a result ≤ 0 gives 0 (00a34886 SETLE). Other spells with a hero:
    /// Teleport + HERO_SPEC_BEATER / 2, a worn RING_OF_MAGI / 2, Power Bargain on an empowered spell, Arcane Training;
    /// a result &lt; 0 gives 0 (00a349d4 SETL).
    /// </summary>
    internal int ExeSpellBaseCost(HeroState? h, SpellDef sp)
    {
        string baseId = BaseSpell(sp.Id);
        var rec = baseId != sp.Id ? Db.Spell(baseId) : sp;
        if (rec == null) return 0;                                                    // 008496a0 null → 0
        int c = rec.ManaCost;
        if (h != null && sp.Id == "SPELL_ABILITY_AVATAR_OF_DEATH")
            c = h.Level * (int)Stats.Get("combat/HeroSpecializations/Necromancer/AvatarOfDeath_ManaCostPerLevel", 2);
        if (baseId != sp.Id) c += c;                                                  // 0x00a347b1 ADD ESI,ESI
        c = MoonWeek.SpellCost(c, S.Week);                                            // 00a347b9 → 00bcddf0
        if (sp.School == "MAGIC_SCHOOL_WARCRIES")
        {
            if (h != null)
            {
                if (h.HasPerk(Sk.ShoutTraining))
                    c = ExeChop(Math.Min((double)(float)((1.001f - (float)Stats.Get("combat/HeroSkills/Barbarian/VoiceTraining_WarcryCostDiscount", 0.2)) * c), c - 1.0));
                if (h.Specialization == "HERO_SPEC_WAR_LEADER")
                {
                    const string w = "combat/HeroSpecializations/Barbarian/WarLeader_WarcryCostReducePerHeroLevel/";
                    int div = (int)Stats.Get(w + "LevelDivisor", 1);
                    c -= (div == 0 ? 0 : h.Level / div) * (int)Stats.Get(w + "PerDividedLevel", 0) + (int)Stats.Get(w + "Base", 0);
                }
            }
            return c <= 0 ? 0 : c;                                                    // 00a34886 SETLE: ≤ 0 → 0
        }
        if (h != null)
        {
            if (sp.Id == "SPELL_TELEPORT" && h.Specialization == "HERO_SPEC_BEATER") c /= 2;
            if (h.Artifacts.Contains("RING_OF_MAGI")) c /= 2;
            if (h.Specialization == "HERO_SPEC_POWER_BARGAIN" && sp.Id.StartsWith("SPELL_EMPOWERED_", StringComparison.Ordinal))
            {
                float r = (float)Stats.Get("combat/HeroSpecializations/Warlock/PowerBargain_CostReduce", 0);
                float per = (float)Stats.Get("combat/HeroSpecializations/Warlock/PowerBargain_CostReducePerLevel", 0);
                float f = (float)(1.0f - (double)r - h.Level * (double)per);
                c = ExeChop(Math.Clamp((double)f, 0.5, 1.0) * c);
            }
            if (h.HasPerk(Sk.ArcaneTraining)) c = ExeChop(Math.Min((double)(float)(c * (double)0.8f), (double)(float)(c - 1.0)));
        }
        return Math.Max(0, c);                                                        // 00a349d4 SETL: < 0 → 0
    }

    /// <summary>
    /// The ready hooks of the tick FUN_004d90d0: a present stack whose gauge goes from below 1.0 to 1.0 or more runs
    /// vf 0x1c8 and, when it holds, vf 0x160, then reads its ATB again (only then is it "ready"). vf 0x1c8 is the bad
    /// morale roll FUN_00a6b8f0 (ABILITY_UNDEAD (0xa) via vf 0x28c → no; morale vf 0x1b0 clamped to ±5, ≥ 0 → no; the
    /// stack RNG's Range(1, 100) &lt; −10 · morale → bad morale) — for a creature (CCombatCreature vf 0x1c8 = 008aec70 →
    /// FUN_008a3e90) only while the byte of its vf 0x50 timeline (FUN_008ace60 on [this − 0x94], also tested by the Battle
    /// Dive finish) is set. vf 0x160 is FUN_00a6cd80 (generic units: 00a74c43 / 00a76886 / 008b1bc6 thunks) — for a
    /// creature FUN_008a8140 first casts SPELL_ARTFSET_NECROMANCERS_DEBUFF (0xce) when the enemy hero wears 4+ Necromancer
    /// pieces (FUN_004d8d60(5) &gt; 3) — : the BAD_MORALE animation, the sequence counter bump (FUN_00a2db60), the ATB
    /// setter vf 0x18c (FUN_00a6b690) with RPGStats +0xad8 (BadMorale_ATBValue), FUN_00bbcaa0 → part of the turn start
    /// (<see cref="TurnStartPartA"/>) and the own hero's Empathy (vf 0x2c(0)). The creature turn start 008a6c40 and the
    /// turn end do not run: no retaliation reset, no start-of-turn damage, no action-count tick, no good morale (the
    /// engine rolled at the turn start, after those, and then finished the turn). A diving stack is left alone, as before
    /// (the timeline byte's meaning is not resolved).
    /// </summary>
    internal void ReadyHook(Unit u)
    {
        if (!u.Alive || u.HasEffect(FxDiving)) return;
        int morale = MoraleOf(u);
        if (morale >= 0 || !MoraleRoll(morale)) return;
        RageEvent(u, "BadMoraleRP");                                                // 004d9239..004d9271: vf 0x6c → vf 0x7c(DemonRage +0x54)
        if (u.Kind == UnitKind.Creature && Specials.Pieces(Enemy(u.Side), Specials.Necromancers) > 3)   // 008a8140
            AddEffect(u, new Effect { Id = NecromancersDebuff, SourceSide = 1 - u.Side, Dispellable = false });
        Emit(new MoraleProc(u.Id, false));
        SetAtb(u.Id, (float)Stats.Get("combat/LuckAndMorale/BadMorale_ATBValue", 0.5));   // vf 0x18c(RPGStats +0xad8)
        TurnStartPartA(u);                                                          // vf 0xd8 → FUN_00bbcaa0 → FUN_00bbc8e0
        if (!u.Alive) { CheckEnd(); seqDirty = true; return; }                      // a damage-over-time kill (00bbb900)
        EmpathyOnMorale(u, good: false);                                            // hero vf 0x2c(0)
        seqDirty = true;                                                            // 00a6ce7f FUN_00a2db60: turn counter +0x38
    }

    /// <summary>Set when the turn sequence must be rebuilt (FUN_00a2db60 bumps +0x38; FUN_00a2e310 rebuilds on a change).</summary>
    bool seqDirty;

    /// <summary>
    /// FUN_00bbc8e0, the part of a creature's turn start the bad morale path also runs (FUN_00bbcaa0 when vf 0xd8 holds):
    /// SPELL_EFFECT_BATTLE_FRENZY (0x112), SPELL_ABILITY_BERSERKER_RAGE (0xf5) and SPELL_EFFECT_PREPARATION (0x11e)
    /// removed (vf 0x270), the Defile Magic release (vf 0x28c(0x6d)), then FUN_00bbc510 (Battle Dive finish; a diving
    /// stack does not reach here), the Dash count, the damage over time and the gated arrival: <see cref="ExeTurnStartTail"/>
    /// (combat_sweep_18.md).
    /// </summary>
    void TurnStartPartA(Unit u) => ExeTurnStartTail(u);

    /// <summary>
    /// FUN_00a72ae0(dt, now) over the unit's effect list (+0x4c): FUN_004e3d50 leaves the markers 0x7ffffffd..0x7fffffff
    /// alone, else remaining (+0x1c) − dt when dt &lt; remaining, otherwise flags the effect expired (+0x34 = 1, +0x30 = now;
    /// FUN_00a72650 runs its expiry). The effect query vf 0x28 (FUN_00a6d330) skips flagged effects, and the list drops them
    /// 1000 units later (now ≥ +0x30 + 1000), so an expired effect is gone at once. A 0-length effect (duration Eval ≤ 0,
    /// 004e57d0) is therefore in force until the next clock tick of any length.
    /// </summary>
    void TickEffects(Unit u, double dtE)
    {
        foreach (var e in u.Effects)
            if (!double.IsPositiveInfinity(e.Remaining)) e.Remaining -= dtE;
        for (int i = u.Effects.Count - 1; i >= 0; i--)
            if (u.Effects[i].Remaining <= Eps || u.Effects[i].Actions == 0)
                RemoveEffectAt(u, i);
    }

    /// <summary>
    /// Call Lightning's amount (FUN_00984a00 case 0xde, 00987ecb..00987f13): at 00987f05 ESP is 4 below the frame base —
    /// the PUSH 0 at 00987efe is FUN_00976b90's last argument, because combat vf 0x3c (00620240: MOV EAX,[ECX+0xa0]; RET)
    /// takes none — so EDX = [base + 0x14] = the caster's vf 0x244 (FUN_00a6a7e0, power; stored at 00984af0), ECX =
    /// [base + 0x70] (the cast record's spell, 0xde), the arguments [base + 0x28] = vf 0x240 (FUN_00a6a700, mastery;
    /// 00984b15), the week (vf 0x3c) and 0. FUN_00976b90 then gives the spell's damage at that mastery and power (the
    /// common path: FUN_00a34ca0, at least 1, chop; the week 00bcde80). The engine used 30 · stack size.
    /// </summary>
    internal int CallLightningDamage(Unit u, SpellDef cl) =>
        (int)SpellBaseDamage(cl, cl, CreatureCastMastery(u, cl), CreatureCastPower(u, cl));
}
