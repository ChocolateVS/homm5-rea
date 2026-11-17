namespace H5.Combat;

// ABILITY_HOWL (docs/rules/audit/combat_sweep_19b.md): traced against H5_Game.exe.
public sealed partial class Battle
{
    /// <summary>
    /// Howl, exe FUN_00a5cbc0 (called by the attack routine FUN_00a65260 @ 00a65653, right after the Rune of Magic
    /// Control 00a5ca80 and before the strikes; not an activated ability — the CombatAbilities record has no
    /// ActivatedSpell). When the attacker is alive with HasAbility(0x60), has a charge (creature interface vf 0x2c =
    /// +0x20, the Wolf's SpellPoints1 = 1) and the target is alive (004e9280): the free placement for the attacker's size
    /// (vf 0x44) nearest to the TARGET's cell (vf 0x40) — FUN_00c558b0 flag 0: FUN_00b3fac0(0, size, 0, 0), FUN_00b3e1e0
    /// with the battle RNG for ties; none → nothing, the charge kept. Then the charge is spent (vf 0x30(c − 1)) and
    /// FUN_00c56750 creates the same creature there; its count is the wolves' own: the first argument is vf 0x1c(t) of
    /// the creature's unit base (vbase 2, vtable 00e4fba4: 008aec10 → 008a42c0, RET 4 — it takes the pushed event time t)
    /// = the count of the stack's history entry at t (008acdd0; the same history gives the health 008a4190 and death
    /// 008a4250), i.e. the count at the attack's start. The new stack: no charge (vf 0x30(0)); the ctor's ATB draw
    /// (00a73e70, battle RNG [0, 0.25)) is replaced by the battle RNG's vf 8(0, HowlMaxATBValueAtStart) through the ATB
    /// setter 00a2d6a0 (min 1.25).
    /// </summary>
    void HowlOnAttack(Unit u, Unit t)
    {
        if (!u.Alive || u.IsObject || u.IsWarMachine || !HasAbilityNow(u, Ab.Howl) || u.AbilityUses.ContainsKey(Ab.Howl) || !t.Alive) return;
        var probe = new Unit { Id = -1, Size = u.Size };
        var exe = ExePos(t);
        if (ExePlacement(probe, new Cell(exe.X - (u.Size - 1), exe.Y - (u.Size - 1)), S.Rng) is not { } at) return;
        u.AbilityUses[Ab.Howl] = 1;
        Emit(new AbilityTriggered(u.Id, Ab.Howl, t.Id));
        var w = ExeSummonAt(u.Side, u.CreatureId, u.Count, Ab.Howl, at);
        w.AbilityUses[Ab.Howl] = 1;
        float max = (float)Stats.Get("combat/Creatures/Neutral/HowlMaxATBValueAtStart", 0.5);
        SetAtb(w.Id, Math.Min((float)S.Rng.RangeD(0f, max), 1.25f));
    }
}
