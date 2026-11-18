using H5.Core.Numerics;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>
/// Arcane Forge: craft a creature artifact with the listed effects (in order) and put it into the hero's bag. The exe's
/// forge screen sends two commands: a resource command with −(price) (FUN_004e9410) and CAddMicroArtifactToHeroBagCmd
/// (vtable 0xe0e588, built by FUN_0063ed80 from FUN_0063c440, execute 004284e0 → hero vf 0xbc = bag add FUN_00c0b340);
/// the checks are the screen's — each effect passes FUN_00c0a700 when added (FUN_0063c0b0 builds the artifact with
/// FUN_00c0a8f0), the price is FUN_0063c180 and the "create" action needs it affordable (FUN_0063c230).
/// </summary>
public sealed record CreateMicroArtifactCmd(int HeroId, IReadOnlyList<MicroEffect> Effects) : AdvCommand;
/// <summary>CEquipMicroArtifactCmd (vtable 0xe0e564, execute 004283b0): bag entry (row, index) → army slot.</summary>
public sealed record EquipMicroArtifactCmd(int HeroId, int Slot, int Row, int Index) : AdvCommand;
/// <summary>CMoveMicroArtifactFromCreatureToHeroBagCmd (execute 00428550): army slot → bag entry (row, index) if it is empty, else the bag's first free place.</summary>
public sealed record UnequipMicroArtifactCmd(int HeroId, int Slot, int Row, int Index) : AdvCommand;
/// <summary>CMoveMicroArtifactFromCreatureToCreatureCmd (execute 0042a9e0): swaps the artifacts of two Academy stacks.</summary>
public sealed record MoveMicroArtifactBetweenCreaturesCmd(int HeroId, int FromSlot, int ToSlot) : AdvCommand;
/// <summary>CMoveMicroArtifactInHeroBagCmd (vtable 0xe0e5ac, built by FUN_0063edc0, execute 0042a8a0).</summary>
public sealed record MoveMicroArtifactInBagCmd(int HeroId, int FromRow, int FromIndex, int ToRow, int ToIndex) : AdvCommand;
/// <summary>
/// Dismantle (forge screen FUN_00644600): the resource command (vtable 0xe0dad8) with +price (FUN_0063c180) and
/// CRemoveMicroArtifactFromHeroBagCmd (vtable 0xe0e5d0, execute 00428510 → hero vf 0xc0 = FUN_00c0b500).
/// </summary>
public sealed record DismantleMicroArtifactCmd(int HeroId, int Row, int Index) : AdvCommand;

public sealed partial class Adventure
{
    /// <summary>Academy TB_SPECIAL_2 = town ability 0x16 (table 00f7c998), the Arcane Forge.</summary>
    public const string ArcaneForgeBuilding = "TB_SPECIAL_2";
    public const string ArtificerSkill = "HERO_SKILL_ARTIFICIER";
    /// <summary>The bag's rows keep at least this many entries (FUN_00c0b580 sets the row minimum +0xc = 5).</summary>
    public const int MicroBagRowMin = 5;

    public static int ArtificerMastery(HeroState h) =>
        (int)(h.Skills.FirstOrDefault(s => s.SkillId == ArtificerSkill)?.Mastery ?? Mastery.None);

    /// <summary>The hero Knowledge the effects scale with (hero vf 0x1c, FUN_00b4cd80: never below 1).</summary>
    public static int ForgeKnowledge(HeroState h) => Math.Max(1, h.Knowledge);

    /// <summary>A creature that can carry an artifact: an Academy creature (FUN_004ba910 == 5, the record's town).</summary>
    public bool CanCarryMicroArtifact(string creature) => Core.Creatures.TryGetValue(creature, out var c) && c.Town == "TOWN_ACADEMY";

    /// <summary>
    /// FUN_00c0a2f0: an effect's value for Knowledge k (k &lt; 1 → 1): wounding / health k/5+1, piercing / defence k/4+1,
    /// armor crushing / speed k/15+1, luck / morale k/10+1, haste k, magic protection
    /// trunc(85·sin(π/2·min(1, (ln(10k)/ln 600)³))) with the exe's table sine (FUN_0046eae0); −1 for other effects.
    /// </summary>
    public static int MicroEffectLevel(MicroEffect e, int knowledge)
    {
        int k = knowledge < 1 ? 1 : knowledge;
        switch (e)
        {
            case MicroEffect.Wounding: case MicroEffect.Health: return k / 5 + 1;
            case MicroEffect.Piercing: case MicroEffect.Defence: return k / 4 + 1;
            case MicroEffect.ArmorCrushing: case MicroEffect.Speed: return k / 15 + 1;
            case MicroEffect.Luck: case MicroEffect.Morale: return k / 10 + 1;
            case MicroEffect.Haste: return k;
            case MicroEffect.MagicProtection:
                // 00c0a340..00c0a3a3: FYL2X is not under the precision control (extended, here double); FMUL / FDIVP are
                // chopped to 24 bits.
                float v = X87.Div(DetMath.Log(X87.Mul(k, 10.0)), DetMath.Log(600.0));   // 00e22a78 = 10.0, 00e88640 = 600.0
                v = X87.Mul(v, X87.Mul(v, v));
                if (v >= 1.0f) v = 1.0f;                                         // 00e12e00
                float s = TableSin(X87.Mul(v, 1.5707963705062866));              // 00e62098
                return (int)X87.Mul(s, 85.0);                                    // 00e88638, FISTP with truncation
            default: return -1;
        }
    }

    /// <summary>The exe's 514 sine table floats at 00e11de8 (bit patterns as stored, T[1] = 0.0122715 = 0x3c490e67).</summary>
    static readonly float[] SinTable = Array.ConvertAll(new uint[]
    {
        0x00000000, 0x3c490e67, 0x3cc90aa0, 0x3d16c326, 0x3d48fb36, 0x3d7b2b6a, 0x3d96a909, 0x3dafb67f,
        0x3dc8bd30, 0x3de1bc12, 0x3dfab29e, 0x3e09cf9a, 0x3e164064, 0x3e22abac, 0x3e2f10aa, 0x3e3b6ed6,
        0x3e47c5ac, 0x3e5414e8, 0x3e605c03, 0x3e6c9a78, 0x3e78cfc0, 0x3e827dcc, 0x3e888e9b, 0x3e8e9a2c,
        0x3e94a03c, 0x3e9aa088, 0x3ea09aed, 0x3ea68f08, 0x3eac7cd9, 0x3eb263fa, 0x3eb84428, 0x3ebe1d43,
        0x3ec3ef07, 0x3ec9b952, 0x3ecf7bc0, 0x3ed53650, 0x3edae87d, 0x3ee09247, 0x3ee6336a, 0x3eebcbc5,
        0x3ef15af3, 0x3ef6e0d2, 0x3efc5d20, 0x3f00e7de, 0x3f039c41, 0x3f064b88, 0x3f08f5a1, 0x3f0b9a6b,
        0x3f0e39d6, 0x3f10d3d0, 0x3f136827, 0x3f15f6dc, 0x3f187fbb, 0x3f1b02c5, 0x3f1d7fd8, 0x3f1ff6d3,
        0x3f226794, 0x3f24d22d, 0x3f273659, 0x3f299418, 0x3f2beb4a, 0x3f2e3bde, 0x3f3085c2, 0x3f32c8c5,
        0x3f3504f7, 0x3f373a26, 0x3f396840, 0x3f3b8f36, 0x3f3daef7, 0x3f3fc760, 0x3f41d873, 0x3f43e1fc,
        0x3f45e3fc, 0x3f47de61, 0x3f49d10b, 0x3f4bbbf9, 0x3f4d9f0a, 0x3f4f7a1c, 0x3f514d40, 0x3f531844,
        0x3f54db38, 0x3f5695ea, 0x3f58485a, 0x3f59f267, 0x3f5b9421, 0x3f5d2d56, 0x3f5ebe06, 0x3f604620,
        0x3f61c593, 0x3f633c60, 0x3f64aa54, 0x3f660f80, 0x3f676bd3, 0x3f68bf3c, 0x3f6a09ab, 0x3f6b4b0e,
        0x3f6c8366, 0x3f6db292, 0x3f6ed8a1, 0x3f6ff573, 0x3f710907, 0x3f72134d, 0x3f731444, 0x3f740bdd,
        0x3f74fa05, 0x3f75dece, 0x3f76ba06, 0x3f778bbd, 0x3f7853f4, 0x3f791299, 0x3f79c79b, 0x3f7a72fc,
        0x3f7b14ba, 0x3f7bacc5, 0x3f7c3b2e, 0x3f7cbfc2, 0x3f7d3ab4, 0x3f7dabd2, 0x3f7e132b, 0x3f7e70b0,
        0x3f7ec472, 0x3f7f0e5e, 0x3f7f4e66, 0x3f7f84a9, 0x3f7fb107, 0x3f7fd391, 0x3f7fec46, 0x3f7ffb16,
        0x3f800000, 0x3f7ffb16, 0x3f7fec46, 0x3f7fd391, 0x3f7fb107, 0x3f7f84a9, 0x3f7f4e66, 0x3f7f0e5e,
        0x3f7ec472, 0x3f7e70b0, 0x3f7e132b, 0x3f7dabd2, 0x3f7d3ab4, 0x3f7cbfc2, 0x3f7c3b2e, 0x3f7bacc5,
        0x3f7b14ba, 0x3f7a72fc, 0x3f79c79b, 0x3f791299, 0x3f7853f4, 0x3f778bbd, 0x3f76ba06, 0x3f75dece,
        0x3f74fa05, 0x3f740bdd, 0x3f731444, 0x3f72134d, 0x3f710907, 0x3f6ff573, 0x3f6ed8a1, 0x3f6db292,
        0x3f6c8366, 0x3f6b4b0e, 0x3f6a09ab, 0x3f68bf3c, 0x3f676bd3, 0x3f660f80, 0x3f64aa54, 0x3f633c60,
        0x3f61c593, 0x3f604620, 0x3f5ebe06, 0x3f5d2d56, 0x3f5b9421, 0x3f59f267, 0x3f58485a, 0x3f5695ea,
        0x3f54db38, 0x3f531844, 0x3f514d40, 0x3f4f7a1c, 0x3f4d9f0a, 0x3f4bbbf9, 0x3f49d10b, 0x3f47de61,
        0x3f45e3fc, 0x3f43e1fc, 0x3f41d873, 0x3f3fc760, 0x3f3daef7, 0x3f3b8f36, 0x3f396840, 0x3f373a26,
        0x3f3504f7, 0x3f32c8c5, 0x3f3085c2, 0x3f2e3bde, 0x3f2beb4a, 0x3f299418, 0x3f273659, 0x3f24d22d,
        0x3f226794, 0x3f1ff6c2, 0x3f1d7fd8, 0x3f1b02c5, 0x3f187fbb, 0x3f15f6dc, 0x3f136827, 0x3f10d3d0,
        0x3f0e39d6, 0x3f0b9a6b, 0x3f08f590, 0x3f064b88, 0x3f039c41, 0x3f00e7de, 0x3efc5d20, 0x3ef6e0d2,
        0x3ef15af3, 0x3eebcbc5, 0x3ee6336a, 0x3ee09247, 0x3edae87d, 0x3ed5362f, 0x3ecf7bc0, 0x3ec9b952,
        0x3ec3ef07, 0x3ebe1d43, 0x3eb84428, 0x3eb263fa, 0x3eac7cd9, 0x3ea68f08, 0x3ea09aed, 0x3e9aa088,
        0x3e94a03c, 0x3e8e9a2c, 0x3e888e9b, 0x3e827dcc, 0x3e78cfc0, 0x3e6c9a78, 0x3e605c03, 0x3e5414e8,
        0x3e47c5ac, 0x3e3b6ed6, 0x3e2f10aa, 0x3e22abac, 0x3e164064, 0x3e09cf9a, 0x3dfab29e, 0x3de1bc12,
        0x3dc8bd30, 0x3dafb671, 0x3d96a8ef, 0x3d7b2b4f, 0x3d48fb01, 0x3d16c326, 0x3cc90aa0, 0x3c490e67,
        0x00000000, 0xbc490f3e, 0xbcc90b0c, 0xbd16c35b, 0xbd48fb36, 0xbd7b2b85, 0xbd96a909, 0xbdafb68c,
        0xbdc8bd4b, 0xbde1bc12, 0xbdfab29e, 0xbe09cf9a, 0xbe164064, 0xbe22abac, 0xbe2f10aa, 0xbe3b6ed6,
        0xbe47c5ac, 0xbe54152b, 0xbe605c03, 0xbe6c9a78, 0xbe78cfc0, 0xbe827dcc, 0xbe888e9b, 0xbe8e9a2c,
        0xbe94a03c, 0xbe9aa088, 0xbea09aed, 0xbea68f08, 0xbeac7cd9, 0xbeb263fa, 0xbeb84428, 0xbebe1d43,
        0xbec3ef07, 0xbec9b952, 0xbecf7bc0, 0xbed53650, 0xbedae87d, 0xbee09247, 0xbee6338b, 0xbeebcbc5,
        0xbef15af3, 0xbef6e0d2, 0xbefc5d20, 0xbf00e7de, 0xbf039c41, 0xbf064b88, 0xbf08f5a1, 0xbf0b9a6b,
        0xbf0e39d6, 0xbf10d3d0, 0xbf136827, 0xbf15f6dc, 0xbf187fbb, 0xbf1b02c5, 0xbf1d7fd8, 0xbf1ff6d3,
        0xbf226794, 0xbf24d22d, 0xbf273659, 0xbf299418, 0xbf2beb4a, 0xbf2e3bde, 0xbf3085c2, 0xbf32c8c5,
        0xbf3504f7, 0xbf373a26, 0xbf396840, 0xbf3b8f36, 0xbf3daef7, 0xbf3fc760, 0xbf41d873, 0xbf43e1fc,
        0xbf45e3fc, 0xbf47de61, 0xbf49d10b, 0xbf4bbbf9, 0xbf4d9f0a, 0xbf4f7a1c, 0xbf514d40, 0xbf531844,
        0xbf54db38, 0xbf5695ea, 0xbf58485a, 0xbf59f267, 0xbf5b9421, 0xbf5d2d56, 0xbf5ebe06, 0xbf604620,
        0xbf61c593, 0xbf633c60, 0xbf64aa54, 0xbf660f91, 0xbf676bd3, 0xbf68bf3c, 0xbf6a09ab, 0xbf6b4b0e,
        0xbf6c8366, 0xbf6db292, 0xbf6ed8a1, 0xbf6ff573, 0xbf710907, 0xbf72134d, 0xbf731444, 0xbf740bdd,
        0xbf74fa05, 0xbf75dece, 0xbf76ba06, 0xbf778bce, 0xbf7853f4, 0xbf791299, 0xbf79c79b, 0xbf7a72fc,
        0xbf7b14ba, 0xbf7bacc5, 0xbf7c3b2e, 0xbf7cbfc2, 0xbf7d3ab4, 0xbf7dabd2, 0xbf7e132b, 0xbf7e70b0,
        0xbf7ec472, 0xbf7f0e5e, 0xbf7f4e66, 0xbf7f84a9, 0xbf7fb107, 0xbf7fd391, 0xbf7fec46, 0xbf7ffb16,
        0xbf800000, 0xbf7ffb16, 0xbf7fec46, 0xbf7fd391, 0xbf7fb107, 0xbf7f84a9, 0xbf7f4e66, 0xbf7f0e5e,
        0xbf7ec472, 0xbf7e70b0, 0xbf7e132b, 0xbf7dabd2, 0xbf7d3ab4, 0xbf7cbfc2, 0xbf7c3b2e, 0xbf7bacc5,
        0xbf7b14ba, 0xbf7a72fc, 0xbf79c79b, 0xbf791299, 0xbf7853f4, 0xbf778bbd, 0xbf76ba06, 0xbf75debe,
        0xbf74fa05, 0xbf740bdd, 0xbf731444, 0xbf72134d, 0xbf710907, 0xbf6ff573, 0xbf6ed8a1, 0xbf6db292,
        0xbf6c8356, 0xbf6b4b0e, 0xbf6a09ab, 0xbf68bf3c, 0xbf676bd3, 0xbf660f80, 0xbf64aa54, 0xbf633c60,
        0xbf61c593, 0xbf604620, 0xbf5ebe06, 0xbf5d2d56, 0xbf5b9421, 0xbf59f267, 0xbf584849, 0xbf5695ea,
        0xbf54db27, 0xbf531844, 0xbf514d40, 0xbf4f7a1c, 0xbf4d9f0a, 0xbf4bbbf9, 0xbf49d10b, 0xbf47de61,
        0xbf45e3fc, 0xbf43e1fc, 0xbf41d873, 0xbf3fc760, 0xbf3daef7, 0xbf3b8f36, 0xbf396840, 0xbf373a26,
        0xbf3504f7, 0xbf32c8c5, 0xbf3085c2, 0xbf2e3bde, 0xbf2beb4a, 0xbf299418, 0xbf273659, 0xbf24d21c,
        0xbf226794, 0xbf1ff6c2, 0xbf1d7fc7, 0xbf1b02c5, 0xbf187fbb, 0xbf15f6dc, 0xbf136827, 0xbf10d3d0,
        0xbf0e39d6, 0xbf0b9a6b, 0xbf08f5a1, 0xbf064b88, 0xbf039c41, 0xbf00e7de, 0xbefc5d20, 0xbef6e0d2,
        0xbef15af3, 0xbeebcba3, 0xbee6336a, 0xbee09247, 0xbedae87d, 0xbed5362f, 0xbecf7bc0, 0xbec9b952,
        0xbec3ef07, 0xbebe1d43, 0xbeb84428, 0xbeb263fa, 0xbeac7cd9, 0xbea68f08, 0xbea09aed, 0xbe9aa088,
        0xbe94a01b, 0xbe8e9a0b, 0xbe888e7a, 0xbe827daa, 0xbe78cfc0, 0xbe6c9a78, 0xbe605c03, 0xbe5414e8,
        0xbe47c5ac, 0xbe3b6ed6, 0xbe2f10aa, 0xbe22abac, 0xbe164064, 0xbe09cf57, 0xbdfab218, 0xbde1bc12,
        0xbdc8bd08, 0xbdafb649, 0xbd96a909, 0xbd7b2b6a, 0xbd48fb1c, 0xbd16c326, 0xbcc90a6b, 0xbc490dfb,
        0x00000000, 0x00000000,
    }, BitConverter.UInt32BitsToSingle);

    /// <summary>
    /// FUN_0046eae0: 512-entry sine table (00e11de8), t = x·81.48733f, i = trunc(t − 0.5) (FSUBR, FISTP under OR AH,0xc),
    /// linear interpolation (T[j+1] − T[j])·(t − i) + T[j], every step chopped, over the exe's own table values.
    /// </summary>
    static float TableSin(float x)
    {
        float t = X87.Mul(x, 81.48733f);
        int i = (int)X87.Sub(t, 0.5f);
        int j = i & 0x1ff;
        return X87.Add(X87.Mul(X87.Sub(SinTable[j + 1], SinTable[j]), X87.Sub(t, i)), SinTable[j]);
    }

    /// <summary>
    /// FUN_00c0a700: an effect can be added when it is not Wounding (1), the hero has Artificier, the artifact has fewer
    /// than three effects and one more stays within the mastery, and the effect is not on it yet.
    /// </summary>
    public static bool CanAddMicroEffect(MicroArtifact a, MicroEffect e, int mastery)
    {
        if (e == MicroEffect.Wounding || mastery <= 0) return false;
        int n = a.Effects.Count;
        if (n >= 3 || n + 1 > mastery) return false;
        return a.Effects.All(x => x.Effect != e);
    }

    /// <summary>
    /// FUN_00c0a8f0: adds the effect with its value for the hero's Knowledge; an Ultimate Artificier (mastery ≥ 4) marks
    /// the artifact half price (+0x20 stays set once set).
    /// </summary>
    public static bool AddMicroEffect(MicroArtifact a, MicroEffect e, HeroState h)
    {
        if (!CanAddMicroEffect(a, e, ArtificerMastery(h))) return false;
        int level = MicroEffectLevel(e, ForgeKnowledge(h));
        if (level < 0) return false;
        a.Effects.Add(new MicroArtifactEntry(e, level));
        a.HalfCost = a.HalfCost || ArtificerMastery(h) >= 4;
        return true;
    }

    /// <summary>FUN_00c0a630: effect i costs its table price × (i + 1); half price: (cost + 1) / 2 per resource.</summary>
    public Resources MicroEffectCost(MicroArtifact a, int i)
    {
        if (i < 0 || i >= a.Effects.Count) return new Resources(0, 0, 0, 0, 0, 0, 0);
        var c = Purse.Scale(Db.MicroArtifactEffectCost(a.Effects[i].Effect), i + 1);
        if (!a.HalfCost) return c;
        return new Resources((c.Wood + 1) / 2, (c.Ore + 1) / 2, (c.Mercury + 1) / 2, (c.Crystal + 1) / 2, (c.Sulfur + 1) / 2,
            (c.Gem + 1) / 2, (c.Gold + 1) / 2);
    }

    /// <summary>FUN_0063c180: the artifact's price = the sum of its effects' costs (also the dismantle refund).</summary>
    public Resources MicroArtifactCost(MicroArtifact a)
    {
        var p = new Purse();
        for (int i = 0; i < a.Effects.Count; i++) p.Add(MicroEffectCost(a, i));
        return new Resources(p[0], p[1], p[2], p[3], p[4], p[5], p[6]);
    }

    /// <summary>
    /// The artifact built from the screen's three selectors (FUN_0063c0b0): the non-empty choices added in order with
    /// FUN_00c0a8f0; null when one of them is refused.
    /// </summary>
    public static MicroArtifact? BuildMicroArtifact(HeroState h, IEnumerable<MicroEffect> effects)
    {
        var a = new MicroArtifact();
        foreach (var e in effects)
        {
            if (e == MicroEffect.None) continue;
            if (!AddMicroEffect(a, e, h)) return null;
        }
        return a;
    }

    /// <summary>
    /// Name parts (forge screen FUN_0063c560): shell = table entry n (1 Amulet, 2 Orb, 3 Trinket; FUN_00c0ae80);
    /// prefix (two or more effects) = triangular index of the first two effects, i = min, j = max of (effect − 1):
    /// ((2·10 − i − 1)·i)/2 − i + j − 1, 0 when out of range; the "of" name is the first effect's (one effect) or the
    /// third's (three effects). Formats MicroArtifactName1..3.
    /// </summary>
    public static (int Shell, int Prefix, MicroEffect OfEffect) MicroArtifactNameParts(MicroArtifact a, int prefixCount = 45)
    {
        int n = a.Effects.Count;
        int prefix = -1;
        if (n > 1)
        {
            int x = (int)a.Effects[0].Effect - 1, y = (int)a.Effects[1].Effect - 1;
            int i = Math.Min(x, y), j = Math.Max(x, y);
            int idx = (2 * 10 - i - 1) * i / 2 - i + j - 1;
            prefix = idx < 0 || idx >= prefixCount ? 0 : idx;
        }
        var of = n == 1 ? a.Effects[0].Effect : n == 3 ? a.Effects[2].Effect : MicroEffect.None;
        return (n, prefix, of);
    }

    // ------------------------------------------------------------------ the hero's bag

    static bool BagValid(HeroState h, int row, int index) =>
        row >= 0 && row < h.MicroBag.Count && index >= 0 && index < h.MicroBag[row].Count;

    /// <summary>
    /// FUN_00c0b340 (hero vf 0xbc): into the row holding the fewest artifacts (the first of equals), its first empty
    /// entry (FUN_00c0bff0 / 00c0b970) or appended, then the rows are rebalanced (FUN_00c0b230).
    /// </summary>
    public static void MicroBagAdd(HeroState h, MicroArtifact a)
    {
        int best = -1, fewest = 0;
        for (int r = 0; r < h.MicroBag.Count; r++)
        {
            int filled = h.MicroBag[r].Count(x => x != null);
            if (best == -1 || filled < fewest) { best = r; fewest = filled; }
        }
        if (best < 0) return;
        var row = h.MicroBag[best];
        int free = row.FindIndex(x => x == null);
        if (free >= 0) row[free] = a; else row.Add(a);
        MicroBagBalance(h);
    }

    /// <summary>
    /// FUN_00c0b500 → FUN_00c0be10 (hero vf 0xc0): an occupied entry is removed when its row is longer than the minimum,
    /// else emptied; then FUN_00c0b230.
    /// </summary>
    public static bool MicroBagRemove(HeroState h, int row, int index)
    {
        if (!BagValid(h, row, index) || h.MicroBag[row][index] == null) return false;
        if (h.MicroBag[row].Count > MicroBagRowMin) h.MicroBag[row].RemoveAt(index); else h.MicroBag[row][index] = null;
        MicroBagBalance(h);
        return true;
    }

    /// <summary>
    /// FUN_00c0b230 (simplified): while a row is longer than the other, its last artifact moves to the end of the other
    /// row. TODO(exe): the exact insertion point (FUN_00c0c140 / 00c0ba00) is not decoded.
    /// </summary>
    static void MicroBagBalance(HeroState h)
    {
        if (h.MicroBag.Count != 2) return;
        for (int pass = 0; pass < 2; pass++)
        {
            var a = h.MicroBag[pass]; var b = h.MicroBag[1 - pass];
            if (a.Count > b.Count && a.Count > MicroBagRowMin && a[^1] is { } last)
            {
                a.RemoveAt(a.Count - 1);
                int free = b.FindIndex(x => x == null);
                if (free >= 0) b[free] = last; else b.Add(last);
            }
        }
    }

    // ------------------------------------------------------------------ commands

    string? DoCreateMicroArtifact(CreateMicroArtifactCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != Current.Index) return "not your hero";
        // The forge screen is the only way to craft: an own Academy town with the Arcane Forge where the hero stands
        // (enter_special FUN_00618c10 → FUN_00640850 with the town's hero).
        if (S.Town(h.InTown) is not { } t || t.Owner != h.Owner || t.Type != "TOWN_ACADEMY" || t.Level(ArcaneForgeBuilding) <= 0)
            return "the hero is not in a town with an Arcane Forge";
        if (c.Effects.Count(e => e != MicroEffect.None) == 0) return "no effect chosen";
        if (BuildMicroArtifact(h, c.Effects) is not { } a) return "the hero cannot add these effects";
        var cost = MicroArtifactCost(a);
        if (!Current.Purse.CanAfford(cost)) return "not enough resources";
        Give(Current, cost, -1);
        MicroBagAdd(h, a);
        Emit(new ArmyChanged(h.Id, -1));
        return null;
    }

    string? DoEquipMicroArtifact(EquipMicroArtifactCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != Current.Index) return "not your hero";
        if (ArtificerMastery(h) <= 0) return "the hero lacks Artificer";
        if (c.Slot < 0 || c.Slot >= h.Army.Length || h.Army[c.Slot] is not { Count: > 0 } st) return "no such stack";
        if (!BagValid(h, c.Row, c.Index) || h.MicroBag[c.Row][c.Index] is not { Effects.Count: > 0 } a) return "no such artifact";
        if (!CanCarryMicroArtifact(st.Creature)) return "only Academy creatures can carry it";
        // 004bcb50 overwrites the stack's artifact (an artifact already on the stack is lost; the screen asks first,
        // artifact-replace-confirmation), then the bag entry goes (vf 0xc0).
        st.Artifact = a.Clone();
        MicroBagRemove(h, c.Row, c.Index);
        Emit(new ArmyChanged(h.Id, -1));
        return null;
    }

    string? DoUnequipMicroArtifact(UnequipMicroArtifactCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != Current.Index) return "not your hero";
        if (c.Slot < 0 || c.Slot >= h.Army.Length || h.Army[c.Slot] is not { Count: > 0 } st || MicroArtifact.IsEmpty(st.Artifact)) return "no artifact on that stack";
        // 00428550: an empty bag entry at (row, index) takes it (vf 0xb8), otherwise vf 0xbc places it.
        if (BagValid(h, c.Row, c.Index) && h.MicroBag[c.Row][c.Index] == null) h.MicroBag[c.Row][c.Index] = st.Artifact;
        else MicroBagAdd(h, st.Artifact!);
        st.Artifact = null;
        Emit(new ArmyChanged(h.Id, -1));
        return null;
    }

    string? DoMoveMicroArtifactBetweenCreatures(MoveMicroArtifactBetweenCreaturesCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != Current.Index) return "not your hero";
        if (c.FromSlot < 0 || c.FromSlot >= h.Army.Length || c.ToSlot < 0 || c.ToSlot >= h.Army.Length) return "invalid slot";
        if (h.Army[c.FromSlot] is not { Count: > 0 } src || h.Army[c.ToSlot] is not { Count: > 0 } dst) return "no such stack";
        if (!CanCarryMicroArtifact(src.Creature) || !CanCarryMicroArtifact(dst.Creature)) return "only Academy creatures can carry it";
        if (MicroArtifact.IsEmpty(src.Artifact)) return "no artifact on that stack";
        (src.Artifact, dst.Artifact) = (MicroArtifact.IsEmpty(dst.Artifact) ? null : dst.Artifact, src.Artifact);
        Emit(new ArmyChanged(h.Id, -1));
        return null;
    }

    string? DoMoveMicroArtifactInBag(MoveMicroArtifactInBagCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != Current.Index) return "not your hero";
        if (!BagValid(h, c.FromRow, c.FromIndex) || !BagValid(h, c.ToRow, c.ToIndex)) return "invalid bag place";
        var a = h.MicroBag[c.FromRow][c.FromIndex];
        if (MicroArtifact.IsEmpty(a)) return "no such artifact";
        // 0042a8a0: an empty target → remove (vf 0xc0) and put (vf 0xb8); an occupied one → swap (vf 0xc4, FUN_00c0af20).
        var b = h.MicroBag[c.ToRow][c.ToIndex];
        if (b == null) { MicroBagRemove(h, c.FromRow, c.FromIndex); if (BagValid(h, c.ToRow, c.ToIndex) && h.MicroBag[c.ToRow][c.ToIndex] == null) h.MicroBag[c.ToRow][c.ToIndex] = a; else MicroBagAdd(h, a!); }
        else { h.MicroBag[c.ToRow][c.ToIndex] = a; h.MicroBag[c.FromRow][c.FromIndex] = b; }
        Emit(new ArmyChanged(h.Id, -1));
        return null;
    }

    string? DoDismantleMicroArtifact(DismantleMicroArtifactCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || !h.Alive || h.Owner != Current.Index) return "not your hero";
        if (!BagValid(h, c.Row, c.Index) || h.MicroBag[c.Row][c.Index] is not { Effects.Count: > 0 } a) return "no such artifact";
        Give(Current, MicroArtifactCost(a));
        MicroBagRemove(h, c.Row, c.Index);
        Emit(new ArmyChanged(h.Id, -1));
        return null;
    }

    // ------------------------------------------------------------------ upgrade, transfers

    /// <summary>
    /// The town's forge upgrade (FUN_00cd2310, from the hero-in-town event FUN_00cd2390 — on arrival, and when the forge is
    /// built with the hero there): with the Arcane Forge and Artificier, hero vf 0xc8 (FUN_00b4f200) raises every effect of
    /// the bag (FUN_00c0b040) and of the army's stacks to its value for the hero's current Knowledge (FUN_00c0a770, never
    /// lowered); if anything changed, the CMicroartifactsUpgradedDialog message (UI/AcademyCreatureArtifacts/
    /// MicroartifactsUpgradedMessage) is shown.
    /// </summary>
    public bool UpgradeMicroArtifacts(HeroState h, TownState t)
    {
        if (t.Type != "TOWN_ACADEMY" || t.Level(ArcaneForgeBuilding) <= 0 || ArtificerMastery(h) <= 0) return false;
        int k = ForgeKnowledge(h);
        bool changed = false;
        void Up(MicroArtifact? a)
        {
            if (a == null) return;
            foreach (var e in a.Effects)
                if (MicroEffectLevel(e.Effect, k) is var v && e.Level < v) { e.Level = v; changed = true; }
        }
        foreach (var row in h.MicroBag) foreach (var a in row) Up(a);
        foreach (var st in h.Army) if (st != null) Up(st.Artifact);
        if (changed) Emit(new Message("MICROARTIFACTS_UPGRADED", "All creatures' artifacts have been upgraded up to hero's knowledge"));
        return changed;
    }

    /// <summary>
    /// Whole-stack merges (FUN_004bcad0 / FUN_00cdd180): when the moving stack carries an artifact, the target's own one goes
    /// to the bag of an Academy hero of the two armies (FUN_00b4eb80: hero vf 0x254 == 5, else it is lost) and the target
    /// takes the moving one; a partial split leaves the artifact on the source.
    /// </summary>
    void MergeMicroArtifact(Stack src, Stack dst, HeroState? a, HeroState? b)
    {
        if (MicroArtifact.IsEmpty(src.Artifact)) return;
        if (!MicroArtifact.IsEmpty(dst.Artifact))
        {
            var bagHero = new[] { a, b }.FirstOrDefault(x => x is { Town: "TOWN_ACADEMY" });
            if (bagHero != null) MicroBagAdd(bagHero, dst.Artifact!);
        }
        dst.Artifact = src.Artifact;
        src.Artifact = null;
    }
}
