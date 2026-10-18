using H5.Core.Data;

namespace H5.Combat;

// Spell hit resolution details recovered from H5_Game.exe by the combat spell audit
// (docs/rules/audit/combat_spells.md): the integer magic-resistance chance, the protection percentage and
// elemental artifact modifiers.
public sealed partial class Battle
{
    /// <summary>
    /// Magic resistance as an integer percent (exe FUN_0097c9f0, tail 0097cfab..0097d0bf):
    /// +15 Resistance skill effect, Impregnable +5+level, Plate Mail of Stability +20, Boots of Interference +10;
    /// with an Aura of Magic Resistance (own or adjacent) the sum becomes trunc(clamp(Aura+0.0001, 0, 1)·100 + sum).
    /// Clamped to 0..100; 100 means the spell always fails.
    /// </summary>
    internal int MagicResistanceChance(Unit t)
    {
        int r = 0;
        var h = HeroOf(t);
        if (h.HasPerk(Sk.MagicResistance)) r += 15;
        if (Spec(h, "HERO_SPEC_IMPREGNABLE") is var l && l >= 0) r += l + 5;
        if (h.Present && h.Artifacts.Contains("PLATE_MAIL_OF_STABILITY")) r += 20;
        if (h.Present && h.Artifacts.Contains("BOOTS_OF_INTERFERENCE")) r += 10;
        if (t.Has(Ab.AuraMagicResistance) || Adjacent(t).Any(o => o.Side == t.Side && o.Has(Ab.AuraMagicResistance)))
            r = ExeChop(Math.Clamp((double)(float)Stats.Get("combat/Creatures/Sylvan/AuraOfMagicResistance", 0.3) + 1e-4, 0, 1) * 100 + r);
        return Math.Clamp(r, 0, 100);
    }

    /// <summary>
    /// The resistance roll (exe FUN_0097d2a0): a chance of 100 always resists; otherwise rng(1,100) &lt; chance
    /// resists (so a 15 % chance resists 14 times in 100), with a second roll for Dwarven Luck.
    /// </summary>
    bool RollResistance(Unit t, int chance)
    {
        if (chance >= 100) return true;
        if (chance <= 0) return false;
        if (S.Rng.Range(1, 100) < chance) return true;
        return HeroOf(t).HasPerk(Sk.DwarvenLuck) && S.Rng.Range(1, 100) < chance;
    }

    /// <summary>Caster artifacts that let the target's resistance be skipped (0097cf89: artifact 0x2d Staff of Magi).</summary>
    static bool IgnoresResistance(HeroState? caster) => caster != null && caster.Present && caster.Artifacts.Contains("STAFF_OF_MAGI");

    /// <summary>
    /// Elemental damage artifacts of the casting hero (exe FUN_00976db0): ×1.5 (truncated) for Titan's Trident
    /// (air), Phoenix Feather Cape (fire), Evercold Icicle (water), Earthsliders (earth).
    /// </summary>
    double ElementArtifactDamage(HeroState? caster, string element, double dmg)
    {
        if (caster == null || !caster.Present) return dmg;
        string? art = element switch
        {
            Elements.Air => "TITANS_TRIDENT", Elements.Fire => "PHOENIX_FEATHER_CAPE",
            Elements.Water => "EVERCOLD_ICICLE", Elements.Earth => "ARTIFACT_EARTHSLIDERS", _ => null,
        };
        return art != null && caster.Artifacts.Contains(art) ? ExeChop(dmg * 1.5) : dmg;
    }

    /// <summary>
    /// Elemental protection artifacts of the target's hero (exe FUN_0097af50): air — Ring of Lightning Protection ×0.5;
    /// fire — Iceberg Shield ×0.5, Dwarven Smithy Hammer ×0.75; water — Dragon Flame Tongue ×0.5, Bearhide Wraps ×0.75;
    /// earth — Rigid Mantle ×0.5. Returned as a multiplier on the damage share.
    /// </summary>
    double ElementProtectionArtifacts(Unit t, string element)
    {
        var h = HeroOf(t);
        if (!h.Present) return 1;
        double m = 1;
        bool Has(string a) => h.Artifacts.Contains(a);
        switch (element)
        {
            case Elements.Air: if (Has("RING_OF_LIGHTING_PROTECTION")) m *= 0.5; break;
            case Elements.Fire:
                if (Has("ICEBERG_SHIELD")) m *= 0.5;
                if (Has("ARTIFACT_DWARVEN_SMITHY_HUMMER")) m *= 0.75;
                break;
            case Elements.Water:
                if (Has("DRAGON_FLAME_TONGUE")) m *= 0.5;
                if (Has("ARTIFACT_BEARHIDE_WRAPS")) m *= 0.75;
                break;
            case Elements.Earth: if (Has("ARTIFACT_RIGID_MANTLE")) m *= 0.5; break;
        }
        return m;
    }

    /// <summary>
    /// Damage after protection (exe FUN_0097d600 → FUN_0097d2a0): the protection share P = 1 − clamp(product, 0, 1)
    /// (1 for an immune or resisted target), reduced by the caster's Irresistible Magic to P·(1 − t/100) with
    /// t = {0, 20, 40, 50, 75}[mastery] (table 00f46f18); P is converted to a whole percent by truncation and the
    /// damage becomes trunc((1 − percent/100) · damage).
    /// </summary>
    internal static double ApplyProtectionPercent(double dmg, double damageMultiplier, bool fullProtection, Mastery invocation)
    {
        // Single precision like the exe's float locals: Protection alone (0.85f) gives 1 − 0.85f = 0.14999998f, so
        // the advertised 15 % truncates to 14 % (fan manual p.321 "Nival rounding").
        float prot = fullProtection ? 1f : 1f - Math.Clamp((float)damageMultiplier, 0f, 1f);
        int[] table = { 0, 20, 40, 50, 75 };
        prot = (1f - table[Math.Clamp((int)invocation, 0, 4)] * 0.01f) * prot;
        int pct = Math.Clamp((int)(prot * 100f), 0, 100);
        return ExeChop((1 - pct * (double)0.01f) * dmg);
    }

    /// <summary>The four elementals (creature ids 85..88, exe FUN_008a08c0) cast spells at reduced damage.</summary>
    static bool IsElementalCaster(Unit? u) =>
        u != null && u.CreatureId is "CREATURE_FIRE_ELEMENTAL" or "CREATURE_WATER_ELEMENTAL" or "CREATURE_EARTH_ELEMENTAL" or "CREATURE_AIR_ELEMENTAL";

    /// <summary>
    /// Level-scaled warcry bonus struct {Base, LevelDivisor, PerDividedLevel} (RPGStats reader FUN_00946030):
    /// ((level − 1) / LevelDivisor) · PerDividedLevel + Base with integer division — the formula the speed getter
    /// applies to Battlecry_SpeedBonus (exe 008a1e50, effect caster level at +0x20).
    /// </summary>
    internal int WarcryLevelBonus(string node, int level)
    {
        int div = Math.Max(1, (int)Stats.Get(node + "/LevelDivisor", 1));
        return (level - 1) / div * (int)Stats.Get(node + "/PerDividedLevel", 0) + (int)Stats.Get(node + "/Base", 0);
    }

    /// <summary>Wasp Swarm ATB reduction (exe FUN_0097d7b0, spell 0x27): duration value ×(1 + ((level+2)/3)·perLevel3) for Call of the Wasp.</summary>
    internal double WaspAtbDrop(SpellDef sp, Mastery m, double power, HeroState? hero)
    {
        double drop = sp.DurationAt(m).Eval(power);
        if (hero != null && Spec(hero, "HERO_SPEC_CALL_OF_THE_WASP") is var wl && wl >= 0)
            drop *= (wl + 2) / 3 * (double)(float)Stats.Get("combat/HeroSpecializations/Ranger/CallOfTheWasp_ATBReducePerLevel3", 0.02) + 1;
        return drop;
    }

    /// <summary>
    /// Spell immunities granted by the target hero's artifacts (exe FUN_0097c9f0, 0097cdff..0097ce8a): Dwarven Mithral
    /// Cuirass — Implosion, Greaves — Slow, Helmet — Blind, Shield — Berserk; Ring of Death — Holy Word.
    /// </summary>
    internal bool ArtifactSpellImmunity(Unit t, string spellId)
    {
        var h = HeroOf(t);
        if (!h.Present) return false;
        string? art = spellId switch
        {
            "SPELL_IMPLOSION" => "DWARVEN_MITHRAL_CUIRASS", "SPELL_SLOW" => "DWARVEN_MITHRAL_GREAVES",
            "SPELL_BLIND" => "DWARVEN_MITHRAL_HELMET", "SPELL_BERSERK" => "DWARVEN_MITHRAL_SHIELD",
            "SPELL_HOLY_WORD" => "ARTIFACT_RING_OF_DEATH", _ => null,
        };
        return art != null && h.Artifacts.Contains(art);
    }

    /// <summary>
    /// Chain Lightning's path (exe FUN_00a5dbb0 called from FUN_0085edb0): the first target, then up to three hops,
    /// each to the nearest living stack not yet in the chain (friend or foe), skipping stacks fully protected from
    /// the spell (callback FUN_0085e2f0: protection percent &gt; 99). Equally near stacks are collected and one is
    /// picked with the battle RNG (inclusive range 0..n−1). The distance metric (combat vf +0x118) is not yet traced;
    /// the footprint gap is used.
    /// </summary>
    internal List<Unit> ChainLightningTargets(Unit first, SpellDef sp, int side)
    {
        var chain = new List<Unit> { first };
        for (int hop = 0; hop < 3; hop++)
        {
            var from = chain[^1];
            var best = new List<Unit>();
            int bestD = int.MaxValue;
            foreach (var u in S.Living)
            {
                if (u.IsObject || chain.Contains(u) || FullyProtectedFrom(u, sp, side)) continue;
                int d = Distance(u, from);
                if (d < bestD) { bestD = d; best.Clear(); best.Add(u); }
                else if (d == bestD) best.Add(u);
            }
            if (best.Count == 0) break;
            chain.Add(best.Count == 1 ? best[0] : best[S.Rng.Range(0, best.Count - 1)]);
        }
        return chain;
    }

    /// <summary>Chain Lightning's per-hop base damage: a float halved per hop (×0.5f), truncated for each hop (0085ef1f).</summary>
    internal static int[] ChainHopBases(int baseDamage, int hops)
    {
        var r = new int[hops];
        float f = baseDamage;
        for (int i = 0; i < hops; i++) { r[i] = (int)f; f *= 0.5f; }
        return r;
    }

    /// <summary>Protection percent ≥ 100 against a spell without caster bonuses (immunity, or a damage multiplier of 0).</summary>
    bool FullyProtectedFrom(Unit u, SpellDef sp, int side)
    {
        if (u.Has(Ab.ImmuneMagic) || sp.Element != Elements.None && ElementImmune(u, sp.Element)) return true;
        float mult = (float)(SpellDamageMultiplier(u, sp, side) * ElementProtectionArtifacts(u, sp.Element));
        return (int)((1f - Math.Clamp(mult, 0f, 1f)) * 100f) > 99;
    }

    /// <summary>
    /// Master of Fire's armour crush (exe FUN_004e5b70, effect 0xca): for one turn the target's defence is lowered by
    /// (current defence + an existing crush) / 2 with integer division — the value is fixed when applied and the
    /// defence sum subtracts it (FUN_00989790 case 0xca). Armored stacks (ability 0x55) are immune.
    /// </summary>
    internal void ApplyArmorCrush(Unit t, int side)
    {
        if (t.Has(Ab.Armored)) return;
        int existing = (int)(t.GetEffect(H5.Combat.Fx.ArmorCrushed)?.Value ?? 0);
        int value = (DefenceOf(t) + existing) / 2;
        AddEffect(t, new Effect { Id = H5.Combat.Fx.ArmorCrushed, Value = value, Remaining = 1, SourceSide = side });
    }

    /// <summary>
    /// Set Afire's damage per turn (exe 004e68ca..004e694b): D = the spell damage dealt to the target, divided back by
    /// clamp(MarkOfFire_FireDamageMultiplier, 0, 1000) when the target carries Mark of Fire (both truncated), then
    /// trunc(D · SetAfire_DamageMultiplier).
    /// </summary>
    internal int SetAfireDamage(Unit t, int dealt)
    {
        int d = dealt;
        if (t.HasEffect(H5.Combat.Fx.MarkOfFire))
            d = ExeChop(d / (double)Math.Clamp((float)Stats.Get("combat/Creatures/Fortress/MarkOfFire_FireDamageMultiplier", 2), 0f, 1000f));
        return ExeChop(d * (double)(float)Stats.Get("combat/HeroSkills/Runemage/SetAfire_DamageMultiplier", 0.33));
    }

    /// <summary>The creature currently casting (set by DoCast), for caster-dependent damage rules.</summary>
    Unit? _castingCreature;
    /// <summary>Set by SpellHits when a damage spell was resisted but Irresistible Magic still lets a share through.</summary>
    bool _resistedFullProtection;
}
