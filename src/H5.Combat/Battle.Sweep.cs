namespace H5.Combat;

// Combat audit sweep (docs/rules/audit/combat_sweep_*.md): mechanics re-checked against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// Rune of Magic Control (exe FUN_00a5ca80, called once by the attack routine FUN_00a65260 @ 00a65646 — after the
    /// approach, before the strike count vf 0x1c0 and the strike loop; not on retaliations): when the attacker holds the
    /// rune (effect 0xfb) and the target is alive, one of the target's effects accepted by FUN_004e4000 is chosen with the
    /// battle RNG, added to the attacker (vf 0x264) and removed from the target (vf 0x270); the rune is used up. The stolen
    /// effect therefore already counts for this attack's strikes. The engine stole after the first hit, on any hit.
    /// </summary>
    void RuneOfMagicControlSteal(Unit a, Unit t)
    {
        if (!a.HasEffect(H5.Combat.Fx.RuneMagicControl) || !t.Alive) return;
        var candidates = t.Effects.Where(MagicControlCandidate).ToList();   // any school (exe FUN_004e4000)
        RemoveEffect(a, H5.Combat.Fx.RuneMagicControl);
        if (candidates.Count == 0) return;
        var stolen = candidates[S.Rng.Next(candidates.Count)];
        RemoveEffect(t, stolen.Id);
        AddEffect(a, new Effect { Id = stolen.Id, Value = stolen.Value, Value2 = stolen.Value2, Remaining = stolen.Remaining,
            Positive = true, SourceSide = a.Side, Mastery = stolen.Mastery, Power = stolen.Power, School = stolen.School, SpellLevel = stolen.SpellLevel });
    }
}
