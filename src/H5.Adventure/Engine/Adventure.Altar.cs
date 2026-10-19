using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// Sacrificial Altar (BUILDING_SACRIFICIAL_ALTAR, building type 0x74): sacrifice creatures of the hero's army for
/// experience. Exe: CSacrificeCreatureOnAltar (vtable 0xe0edd4, built by FUN_007b7270 from the altar screen with the
/// whole stack's count, execute 00429cb0) → the building's altar interface (+0x80) slot 0 = 00b00e30.
/// </summary>
public sealed record SacrificeCreatureOnAltarCmd(int HeroId, int Object, int Slot, int Count) : AdvCommand;

/// <summary>
/// Sacrificial Altar: sacrifice a worn (<paramref name="FromBackpack"/> false) or carried artifact for experience.
/// Exe: CSacrificeArtifactOnAltar (vtable 0xe0edf8, built by FUN_00624cf0, execute 00429d20) → altar slot 4 = 00b01120.
/// </summary>
public sealed record SacrificeArtifactOnAltarCmd(int HeroId, int Object, string Artifact, bool FromBackpack) : AdvCommand;

/// <summary>An artifact given up at the Sacrificial Altar (experience before the hero's modifiers).</summary>
public sealed record ArtifactSacrificed(int HeroId, string Artifact, int Experience) : AdvEvent;

public sealed partial class Adventure
{
    public const string SacrificialAltar = "BUILDING_SACRIFICIAL_ALTAR";

    /// <summary>
    /// 00b00e30: experience = trunc((hit points with the hero's bonuses, FUN_004bc470) × count (32-bit IMUL) ×
    /// SacrificialAltar_CreatureHitsToExpCoef (RPGStats+0x1c4, float), x87 FISTP with truncation), before the
    /// hero's experience modifiers.
    /// </summary>
    public int AltarCreatureExperience(HeroState h, string creature, int count)
    {
        float coef = (float)Core.Stats.Get("adventure/SacrificialAltar_CreatureHitsToExpCoef", 3);
        int hp = unchecked(CreatureHitPoints(h, creature) * count);
        return (int)Math.Truncate(hp * (double)coef);
    }

    /// <summary>
    /// 00b01120: experience = trunc(artifact gold cost (FUN_00ca6410) × SacrificialAltar_ArtifactCostOfGoldToExpCoef
    /// (RPGStats+0x1c0, float)), before the hero's experience modifiers.
    /// </summary>
    public int AltarArtifactExperience(string artifact)
    {
        float coef = (float)Core.Stats.Get("adventure/SacrificialAltar_ArtifactCostOfGoldToExpCoef", 0.5);
        int gold = Core.Artifacts.TryGetValue(artifact, out var a) ? a.CostOfGold : 0;
        return (int)Math.Truncate(gold * (double)coef);
    }

    string? AltarHero(int heroId, int obj, out HeroState h)
    {
        h = S.Hero(heroId)!;
        if (h == null || !h.Alive || h.Owner != Current.Index) return "not your hero";
        if (obj < 0 || obj >= Map.Objects.Count || O(obj).Shared.Type != SacrificialAltar) return "not a sacrificial altar";
        return null;
    }

    string? DoSacrificeCreatureOnAltar(SacrificeCreatureOnAltarCmd c)
    {
        if (AltarHero(c.HeroId, c.Object, out var h) is { } err) return err;
        // 00b00e30: slot in the army, 0 < count ≤ the stack's count; no other condition (the last stack may go).
        if (c.Slot is < 0 or >= State.Army.Slots || h.Army[c.Slot] is not { Count: > 0 } s) return "no such stack";
        if (c.Count <= 0 || c.Count > s.Count) return "bad count";
        int exp = AltarCreatureExperience(h, s.Creature, c.Count);
        h.Army[c.Slot] = c.Count < s.Count ? new Stack(s.Creature, s.Count - c.Count) : null;
        Emit(new ArmyChanged(h.Id, -1));
        // hero +0x19c (experience modifiers, FUN_00b4bbb0) then +0x198 (add).
        GainExperience(h, exp);
        return null;
    }

    string? DoSacrificeArtifactOnAltar(SacrificeArtifactOnAltarCmd c)
    {
        if (AltarHero(c.HeroId, c.Object, out var h) is { } err) return err;
        var list = c.FromBackpack ? h.Backpack : h.Artifacts;
        // TODO(exe): the artifact check FUN_006cbbf0 before the sacrifice is not decoded.
        if (!list.Contains(c.Artifact)) return "artifact not carried";
        int exp = AltarArtifactExperience(c.Artifact);
        list.Remove(c.Artifact);
        Emit(new ArtifactSacrificed(h.Id, c.Artifact, exp));
        GainExperience(h, exp);
        return null;
    }
}
