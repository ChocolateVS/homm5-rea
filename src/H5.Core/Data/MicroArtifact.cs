namespace H5.Core.Data;

/// <summary>
/// The micro-artifact effects (exe enum MicroArtifactEffectType, enum-to-string FUN_00ad2400; GameMechanics/RefTables/
/// MicroArtifactEffects.xdb holds the name, icon and cost of each).
/// </summary>
public enum MicroEffect
{
    None = 0, Wounding = 1, Piercing = 2, ArmorCrushing = 3, Health = 4, Defence = 5,
    MagicProtection = 6, Luck = 7, Morale = 8, Speed = 9, Haste = 10,
}

/// <summary>One crafted effect: the effect and its value (exe 8-byte entry {type, level}).</summary>
public sealed class MicroArtifactEntry
{
    public MicroEffect Effect { get; set; }
    public int Level { get; set; }
    public MicroArtifactEntry() { }
    public MicroArtifactEntry(MicroEffect effect, int level) { Effect = effect; Level = level; }
}

/// <summary>
/// A creature artifact made in the Academy Arcane Forge (exe 0x24-byte struct at army stack +0x2c and in the hero's
/// bag): +0x00 vector of effect entries (at most three), +0x10..+0x19 the per-effect sums as signed bytes (rebuilt by
/// FUN_00c0a220 / FUN_00c0a770 from the entries through FUN_00c0a560), +0x20 the "made with Ultimate Artificer" flag
/// that halves its price (FUN_00c0a8f0 / FUN_00c0a630). The stack carries one when the entry list is non-empty
/// (FUN_00c0a440 &gt; 0).
/// </summary>
public sealed class MicroArtifact
{
    public List<MicroArtifactEntry> Effects { get; set; } = new();
    /// <summary>Exe +0x20: set once a hero with Artificer mastery ≥ 4 added an effect; the price is halved (rounded up).</summary>
    public bool HalfCost { get; set; }

    /// <summary>
    /// The summary byte of an effect (exe +0x10.. per FUN_00c0a560: the sum of that effect's levels as a signed
    /// char), which the stat getters read through FUN_00c0a2c0.
    /// </summary>
    public int Bonus(MicroEffect e)
    {
        int s = 0;
        foreach (var x in Effects) if (x.Effect == e) s += x.Level;
        return (sbyte)s;
    }

    public MicroArtifact Clone() => new()
    {
        HalfCost = HalfCost,
        Effects = Effects.Select(e => new MicroArtifactEntry(e.Effect, e.Level)).ToList(),
    };

    public static bool IsEmpty(MicroArtifact? a) => a == null || a.Effects.Count == 0;
}
