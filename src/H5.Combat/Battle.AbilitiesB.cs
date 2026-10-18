namespace H5.Combat;

// Creature abilities of Academy / Dungeon / Fortress / Stronghold / neutral creatures audited against H5_Game.exe
// (docs/rules/audit/combat_abilities_b.md).
public sealed partial class Battle
{
    /// <summary>Foul Wyvern venom: its own effect (SPELL_ABILITY_VENOM, 0x128), separate from poison (0xb4).</summary>
    internal const string FxVenom = "FX_VENOM";

    /// <summary>
    /// Poisonous Attack / Venom (exe FUN_00a61720 for SPELL_ABILITY_POISONOUS_ATTACK, FUN_00a61e60 for
    /// SPELL_ABILITY_VENOM): two independent effects that can be on the same target. The effect is (re)applied only when
    /// the target has none or the existing one's power is not greater than the new one (a stronger poison is never
    /// replaced by a weaker one). Power = the poisoner's stack size at the time of poisoning; damage per turn =
    /// power × perPower, for <paramref name="counter"/> of the target's turns (AssasinPoisonCounter for every poisoner,
    /// VenomDuration for venom).
    /// </summary>
    internal void ApplyDamageOverTime(Unit a, Unit t, string fx, double perPower, int counter, bool dispellable)
    {
        int power = a.Count;
        if (t.GetEffect(fx) is { } current && current.Value2 > power) return;
        AddEffect(t, new Effect { Id = fx, Value = perPower * power, Value2 = power, Actions = counter, SourceSide = a.Side, Dispellable = dispellable });
    }
}
