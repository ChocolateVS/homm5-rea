using H5.Core.Data;

namespace H5.Combat;

// Combat audit sweep 9 (docs/rules/audit/combat_sweep_9.md): rows traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// Berserker Rage cast (exe FUN_00984a00 @ 00988051 → FUN_00bbc1d0), after effect 0xf5 was added with count 2:
    /// vf 0x200 (FUN_008aa960: the turn-start reset, retaliation available again); FUN_00bbc0e0 — the berserk attack on
    /// a stack of the enemy army (vf 0x10), else of the own army (vf 0xc), through FUN_00bbbc00 (melee FUN_00c542d0, or
    /// a shot when the stack can shoot); when none is possible a Defend event (vtable 00e17db8, vf 0x1c = FUN_004e0280 →
    /// unit vf 0x204 = the Defend handler FUN_008a6be0); finally vf 0x268(0xf5) drops the count to 1. The attack runs
    /// while the count is 2, so it is never retaliated (FUN_00a5fee0 @ 00a6005c); the effect stays (defence 0, attack +
    /// defence) until the stack's next turn start (FUN_00bbc8e0 @ 00bbc951, vf 0x270 force).
    /// </summary>
    internal void BerserkerRageCast(Unit u)
    {
        u.RetaliationsLeft = HasAbilityNow(u, Ab.UnlimitedRetaliation) ? int.MaxValue : 1;
        if (!AutoBerserkAttack(u, anySide: false, approach: false) && u.Alive) DefendEffects(u);
        if (u.GetEffect(H5.Combat.Fx.BerserkerRage) is { } e) e.Value2 = 1;
    }

    /// <summary>exe FUN_00a5fee0 @ 00a6005c: no retaliation against an attacker whose effect 0xf5 has count (+0x20) ≥ 2.</summary>
    internal static bool BerserkerRageBlocksRetaliation(Unit attacker) =>
        attacker.GetEffect(H5.Combat.Fx.BerserkerRage) is { Value2: >= 2 };

    /// <summary>The hero's vf 0x230 (exe FUN_00bbfae0): adds mana given in thousandths. With <paramref name="cap"/> nothing
    /// happens at full mana, and the result is capped at the maximum (fraction cleared). Returns whether mana rose.</summary>
    internal bool AddManaMilli(HeroState h, int milli, bool cap)
    {
        int old = h.Mana;
        if (cap && h.MaxMana <= old) return false;
        int frac = h.ManaMilli + milli % 1000;
        h.Mana += frac / 1000 + milli / 1000;
        h.ManaMilli = frac % 1000;
        if (cap && h.MaxMana <= h.Mana) { h.Mana = h.MaxMana; h.ManaMilli = 0; }
        if (h.Mana != old) Emit(new ManaChanged(Actor.Hero(h.Side), h.Mana));
        return old < h.Mana;
    }

    /// <summary>
    /// Spirit Link mana feed (exe FUN_008a6e20 @ 008a6f61..008a7112, the creature's damage routine): a stack linked by a
    /// hero's 0x3a (FUN_00a6e270) gives that hero, while below maximum mana, vf 0x230(lost · 1000 / SpiritLink_HPForOneMana,
    /// cap) — integer division, the thousandths accumulate. <paramref name="lost"/> is the damage, or the stack's whole
    /// health when it dies (or is a Phantom, 0x28). The engine added ceil(damage / HPForOneMana) per hit.
    /// </summary>
    internal void SpiritLinkFeed(Unit t, int lost)
    {
        if (lost <= 0) return;
        foreach (var h in S.Heroes)
            if (h.Present && h.Links.TryGetValue(Sk.MarkOfNecromancer, out var linked) && linked == t.Id && h.Mana < h.MaxMana)
            {
                int per = Math.Max(1, (int)Stats.Get("combat/HeroSkills/Necromancer/SpiritLink_HPForOneMana", 75));
                AddManaMilli(h, lost * 1000 / per, cap: true);
            }
    }

    /// <summary>
    /// Spirit Link cast (exe FUN_00984a00 case 0x3a, 009879e1..00987b03): a stack linked before is released
    /// (FUN_00a6e310, vf 0x270); the new link is set; when there was none, mana = min(max mana, mana + total health
    /// (vf 0x1dc) / SpiritLink_HPForOneMana + 1), applied only when it rises.
    /// </summary>
    internal void SpiritLinkCast(HeroState h, Unit t)
    {
        bool hadLink = h.Links.TryGetValue(Sk.MarkOfNecromancer, out var old) && S.Unit(old) is { Alive: true };
        h.Links[Sk.MarkOfNecromancer] = t.Id;
        if (hadLink) return;
        int per = Math.Max(1, (int)Stats.Get("combat/HeroSkills/Necromancer/SpiritLink_HPForOneMana", 75));
        int mana = Math.Min(h.MaxMana, t.TotalHealth / per + h.Mana + 1);
        if (mana > h.Mana) { h.Mana = mana; Emit(new ManaChanged(Actor.Hero(h.Side), h.Mana)); }
    }

    /// <summary>
    /// Guardian Angel's resurrection (exe FUN_00988860 → FUN_00984a00 case 0xf1 → FUN_009789b0 case 0xf1): the hit points
    /// are round(max(0, Eval of SPELL_EFFECT_GUARDIAN_ANGEL's damage table (FUN_00a34ca0) at the hero's level (vf 0x23c)))
    /// — Base 1 + PerPower 1 · level in both shipped data sets (all four mastery items equal). The engine used the manual's
    /// 240 + 30 · level.
    /// </summary>
    internal int GuardianAngelAmount(HeroState h)
    {
        var sp = Db.Spell("SPELL_EFFECT_GUARDIAN_ANGEL");
        double v = sp != null && sp.Damage.Length > 0 ? sp.DamageAt(Mastery.None).Eval(h.Level) : 1 + h.Level;
        return (int)Math.Round(Math.Max(0, v), MidpointRounding.ToEven);
    }
}
