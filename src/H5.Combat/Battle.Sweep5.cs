using H5.Core.Data;

namespace H5.Combat;

// Racial and might perks confirmed against H5_Game.exe by the combat audit sweep, part 5
// (docs/rules/audit/combat_sweep_5.md): Gating, Hellfire, Holy Charge, Preparation, Avenger, Matron Salvo.
public sealed partial class Battle
{
    internal const string PreparationEffect = "SPELL_EFFECT_PREPARATION";

    /// <summary>
    /// Gated creatures (exe FUN_0085fe60 tail, 0086048d..0086054f): n = floor(count · portion) in single precision, then
    /// +1 when a battle-RNG float in [0, 1) is below the fraction left over. The result may be 0: then the cast places
    /// no stack (FUN_00861840 @ 008619df) but still uses the charge and sets the caster's ATB.
    /// </summary>
    internal int GatingCount(Unit u, HeroState h)
    {
        float product = (float)u.Count * (float)GatingPortion(u, h);
        int n = (int)Math.Floor(product);
        if ((float)S.Rng.NextDouble() < product - n) n++;
        return n;
    }

    /// <summary>Gated stack's ATB (exe FUN_00861840 @ 00861b6a / 00861bd9): 0, or 0x3f7fffef (≈ 0.999999) — "Gating
    /// immedately" — when the hero has Urgash's Call (HERO_SKILL_ABSOLUTE_GATING, 0x62).</summary>
    internal static double GatedStartAtb(HeroState h) =>
        h.HasPerk("HERO_SKILL_ABSOLUTE_GATING") ? BitConverter.Int32BitsToSingle(0x3f7fffef) : 0;

    /// <summary>Caster's ATB after gating (exe FUN_00861840 @ 00861930 / 00861971): clamp(QuickGatingStartATB with Quick
    /// Gating, else CallerStartATB, 0, 0.999 [0x3f7fbe77]).</summary>
    internal double GatingCallerAtb(HeroState h)
    {
        const string gp = "combat/HeroSkills/DemonLord/Gating/";
        double v = h.HasPerk(Sk.QuickGating) ? Stats.Get(gp + "QuickGatingStartATB", 0.75) : Stats.Get(gp + "CallerStartATB", 0.5);
        return Math.Clamp((float)v, 0f, BitConverter.Int32BitsToSingle(0x3f7fbe77));
    }

    /// <summary>
    /// Fiery Wrath and Hellfire (exe FUN_00a5ed90): one fire hit through the perk path FUN_0097d740 (Fireball, ECX = 5).
    /// The amount starts at Fiery Wrath's 0.1 × damage; Hellfire (attacker carrying SPELL_SKILL_DEMONIC_FIRE — the
    /// Hellfire perk's spellBuffs — outside retaliations unless Hellwrath 0x5c) needs mana ≥ max(0, ManaCost), then an
    /// ability roll of 30 (EDX = 0x1e, no attacker): the hero pays the cost and BaseDamage + DamagePerSpellPower · SP
    /// (integers, at least 0) is added; Searing Fires (0x5e) multiplies the whole amount by 1.5 (float 00e3a3ac).
    /// </summary>
    internal void FieryWrathAndHellfire(Unit a, Unit t, HeroState ah, int dmg, bool retaliation)
    {
        float amount = ah.HasPerk(Sk.FieryWrath) || ah.HasPerk(Sk.BarbFieryWrath) ? dmg * 0.1f : 0f;
        bool hellfire = false;
        if (ah.HasPerk(Sk.Hellfire) && (!retaliation || ah.HasPerk(Sk.Hellwrath)))
        {
            const string df = "combat/HeroSkills/DemonLord/DemonicFire/";
            int cost = Math.Max(0, (int)Stats.Get(df + "ManaCost", 5));
            if (ah.Mana - cost >= 0 && HeroRoll(30))
            {
                ah.Mana -= cost;
                Emit(new ManaChanged(Actor.Hero(a.Side), ah.Mana));
                int fire = (int)ah.SpellPower * (int)Stats.Get(df + "DamagePerSpellPower", 10) + (int)Stats.Get(df + "BaseDamage", 10);
                amount += Math.Max(0, fire);
                if (ah.HasPerk(Sk.SearingFires)) amount *= 1.5f;
                hellfire = true;
            }
        }
        if (amount > 0) DealPerkElementalDamage(t, amount, "SPELL_FIREBALL", a.Id, hellfire ? "HELLFIRE" : "FIERY_WRATH");
    }

    /// <summary>
    /// Holy Charge strike (exe FUN_00bc1b90 case 0x35, 00bc1dc9..00bc1e5f): the level+3 base, then Cavalry Commander
    /// trunc((clamp(CavalryCommander_HolyChargeBonus, 0, 100) · level + 1) · dmg), then ×3 with Unstoppable Charge
    /// (HERO_SKILL_ABSOLUTE_CHARGE, mastery vf 0x174(0x55)) — before the week of Might and Offender.
    /// </summary>
    internal int HolyChargeDamage(HeroState h, int dmg)
    {
        if (h.Specialization == "HERO_SPEC_JOUSTING")
        {
            float p = Math.Clamp((float)Stats.Get("combat/HeroSpecializations/Knight/CavalryCommander_HolyChargeBonus", 0.01), 0f, 100f);
            dmg = (int)(((double)p * h.Level + 1.0) * dmg);
        }
        if (h.HasPerk(Sk.UnstoppableCharge)) dmg *= 3;
        return dmg;
    }

    /// <summary>Avenger critical chance (exe FUN_00a5ae40): 40, 50 with Cunning of the Woods (0x72). The exe also adds
    /// 10 × a value read from the hero's owner (hero vf 0 → vf 0 → vf 0x20, field +0x24) that is not identified.</summary>
    internal static int AvengerCritChance(HeroState h) => h.HasPerk("HERO_SKILL_CUNNING_OF_THE_WOODS") ? 50 : 40;

    /// <summary>Matron Salvo chance per witch stack (exe FUN_00978e10 @ 00978f2f): hero level · 2 + 10.</summary>
    internal static int MatronSalvoChance(HeroState h) => h.Level * 2 + 10;
}
