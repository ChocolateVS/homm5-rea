using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Adventure-map spells (GameMechanics/Spell/Adventure_Spells, MAGIC_SCHOOL_ADVENTURE). Mana costs are the
// spells' TrainedCost; the rules are the stock spell texts (Text/Game/Spells/Adventure/*) and the fan
// manual's Adventure Magic section (p.184):
//   Instant Travel (SPELL_DIMENSION_DOOR, 15 mana): exe 008652f0 / 008656e0 case 0x32: same floor, dx² + dy² ≤ 64,
//     needs half the day's movement (vf 0xfc / 2 ≤ vf 0xf4) and takes it (clamped at 0); land to land or sea to sea
//     only (FUN_008649f0). (Was: within 800 movement points per the fan manual.)
//   Town Portal (SPELL_TOWN_PORTAL, 20 mana): to the nearest own town (see PortalTown); all movement points are lost.
//   Summon Creatures (SPELL_SUMMON_CREATURES, 0 + 1 mana per creature): chosen stacks from the nearest own
//     town's garrison; takes 75% of the hero's starting movement points.
//   Vessel of Shalassa (SPELL_SUMMON_BOAT, 4 mana): brings the nearest empty ship next to the hero
//     (see Adventure.Boats.cs for the exe evidence).
// TODO(exe): exact tie-breaks for "nearest" (straight-line distance across floors is assumed here) and
// whether a spell may be cast with fewer movement points than it costs (rejected here).

public sealed record CastAdventureSpellCmd(int HeroId, string Spell, Tile? Target = null, int TownObject = -1, List<int>? Stacks = null) : AdvCommand;

public sealed record AdventureSpellCast(int HeroId, string Spell, Tile From, Tile To) : AdvEvent;

/// <summary>A hero cast the script custom ability SPELL_ABILITY_CUSTOM&lt;Ability&gt; (fires CUSTOM_ABILITY_TRIGGER).</summary>
public sealed record CustomAbilityUsed(int HeroId, int Ability) : AdvEvent;

public sealed partial class Adventure
{
    public const string SpellDimensionDoor = "SPELL_DIMENSION_DOOR";
    public const string SpellTownPortal = "SPELL_TOWN_PORTAL";
    public const string SpellSummonCreatures = "SPELL_SUMMON_CREATURES";
    public const string SpellSummonBoat = "SPELL_SUMMON_BOAT";

    /// <summary>The spell record's mana cost (+0x90) without the hero's modifiers (see the hero overload).</summary>
    public int AdventureSpellCost(string spell) => Db.Core.Spell(spell)?.ManaCost ?? 0;

    /// <summary>
    /// The adventure cast's mana cost: can-cast 00b4c430 → 008647d0 → FUN_00a34740(spell, hero, week = hero vf 0x22c), the
    /// chain the combat cost (009781e0) starts with. 00a34740: the record is the base of a mass (00a33e20) or empowered
    /// (00a33f70) version, else the spell; cost = record +0x90, × 2 for a mass / empowered version; the week (00bcddf0:
    /// Magic / Might and Magic cost / 2, Ether × 2). Warcries (record +0x88 = 6): Voice Training (vf 0x174(0xbc) &gt; 0)
    /// chop(min((1.001f − discount) · cost, cost − 1)), War Leader (speciality 0x4b) − ((level + 1 − 1) / div · per + base),
    /// and a result ≤ 0 gives 0 (0x00a34886 SETLE). Other spells: Teleport (0x20) with HERO_SPEC_BEATER (0xd) / 2, a worn
    /// RING_OF_MAGI (0x2f) / 2, HERO_SPEC_POWER_BARGAIN (0x31) on an empowered spell chop(clamp(1 − reduce − level ·
    /// perLevel, 0.5, 1) · cost), Arcane Training (vf 0x174(0x2a) ≠ 0) chop(min(cost · 0.8f, cost − 1)); never below 0.
    /// Integer halvings truncate toward zero, every float conversion chops (00a34836, 00a34968, 00a349b9).
    /// GAP-details: Avatar of Death (spell 0xc8: level · RPGStats +0x888 as the base, 0x00a34785) is a combat ability and
    /// not modelled here; 008647d0's free entry (00a34470: the book entry's artifact +0x1c set, a wand → cost 0 and
    /// 00b4c430 tests its charges, 00ca62e0) is not separated from the mana entry in the adventure cast.
    /// </summary>
    public int AdventureSpellCost(HeroState h, string spell)
    {
        string baseId = BaseSpell(spell);
        var rec = Db.Core.Spell(baseId) ?? Db.Core.Spell(spell);
        if (rec == null) return 0;                                                   // 008496a0 null → 0
        int c = rec.ManaCost;
        if (baseId != spell) c += c;                                                 // 0x00a347b1
        c = H5.Combat.MoonWeek.SpellCost(c, S.MoonWeek);
        double Stat(string path, double def) => Core.Stats.Get(path, def);
        if (Db.Core.Spell(spell)?.School == "MAGIC_SCHOOL_WARCRIES")
        {
            if (h.HasPerk("HERO_SKILL_VOICE_TRAINING") || EffectiveMastery(h, "HERO_SKILL_VOICE_TRAINING") > 0)
            {
                float d = (float)Stat("combat/HeroSkills/Barbarian/VoiceTraining_WarcryCostDiscount", 0.2);
                c = (int)Math.Truncate(Math.Min((double)(float)((1.001f - (double)d) * c), (double)(float)(c - 1.0)));
            }
            if (h.Specialization == "HERO_SPEC_WAR_LEADER")
            {
                const string w = "combat/HeroSpecializations/Barbarian/WarLeader_WarcryCostReducePerHeroLevel/";
                int div = (int)Stat(w + "LevelDivisor", 1);
                c -= (div == 0 ? 0 : h.Level / div) * (int)Stat(w + "PerDividedLevel", 0) + (int)Stat(w + "Base", 0);
            }
            return c < 1 ? 0 : c;
        }
        if (spell == "SPELL_TELEPORT" && h.Specialization == "HERO_SPEC_BEATER") c /= 2;
        if (h.Artifacts.Contains("RING_OF_MAGI")) c /= 2;
        if (h.Specialization == "HERO_SPEC_POWER_BARGAIN" && spell.StartsWith("SPELL_EMPOWERED_", StringComparison.Ordinal))
        {
            float r = (float)Stat("combat/HeroSpecializations/Warlock/PowerBargain_CostReduce", 0);
            float per = (float)Stat("combat/HeroSpecializations/Warlock/PowerBargain_CostReducePerLevel", 0);
            float f = (float)(1.0f - (double)r - h.Level * (double)per);
            c = (int)Math.Truncate(Math.Clamp((double)f, 0.5, 1.0) * c);
        }
        if (h.HasPerk("HERO_SKILL_ARCANE_TRAINING") || EffectiveMastery(h, "HERO_SKILL_ARCANE_TRAINING") != 0)
            c = (int)Math.Truncate(Math.Min((double)(float)(c * (double)0.8f), (double)(float)(c - 1.0)));
        return Math.Max(0, c);
    }

    /// <summary>Straight movement cost between two tiles ignoring terrain: 100 per side step, 141 per diagonal.</summary>
    public static int FlatMoveCost(Tile a, Tile b)
    {
        int dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
        int diag = Math.Min(dx, dy);
        return diag * 141 + (Math.Max(dx, dy) - diag) * 100;
    }

    /// <summary>
    /// Whether Instant Travel may land on <paramref name="t"/>: the cast check (exe 008652f0 case 0x32) wants the same
    /// floor and dx² + dy² ≤ 64 (Euclidean tiles, not movement points), then the landing check FUN_008649f0
    /// (<see cref="DimensionDoorLandingOk"/>). The movement check (vf 0xfc / 2 ≤ vf 0xf4) is in the cast.
    /// </summary>
    public bool CanDimensionDoorTo(HeroState h, Tile t) =>
        t.Floor == h.Pos.Floor && DistSq(h.Pos, t) <= DimensionDoorRadiusSq && DimensionDoorLandingOk(h, t, h.InBoat);

    /// <summary>The nearest town of the hero's owner (straight-line distance, other floors last), or null.</summary>
    public TownState? NearestOwnTown(HeroState h, Func<TownState, bool>? filter = null) =>
        S.Towns.Where(t => t.Owner == h.Owner && (filter?.Invoke(t) ?? true))
            .OrderBy(t => (t.Pos.Floor == h.Pos.Floor ? 0 : 1_000_000) + FlatMoveCost(h.Pos, t.Pos))
            .ThenBy(t => t.Object)
            .FirstOrDefault();

    /// <summary>
    /// Town Portal's town (cast 00427280 → 008656e0 case 0x33 → 00865170 → FUN_00ccf6e0): the owner's town with the
    /// smallest dx² + dy² + (DepthBetweenFloors (RPGStats +0x6c) × floor difference)² (FUN_00a33600), compared with a
    /// strict &lt; (FUN_00a33560) so the earlier town wins a tie. The caster does not choose.
    /// </summary>
    public TownState? PortalTown(HeroState h)
    {
        long depth = Math.Max(0, Db.Stats.Root.Element("adventure").Int("DepthBetweenFloors", 10));
        TownState? best = null;
        long bestD = 0;
        foreach (var t in S.Towns.Where(t => t.Owner == h.Owner))
        {
            long dx = t.Pos.X - h.Pos.X, dy = t.Pos.Y - h.Pos.Y, dz = depth * (t.Pos.Floor - h.Pos.Floor);
            long d = dx * dx + dy * dy + dz * dz;
            if (best == null || d < bestD) { best = t; bestD = d; }
        }
        return best;
    }

    string? DoCastAdventureSpell(CastAdventureSpellCmd c)
    {
        if (S.Hero(c.HeroId) is not { Alive: true } h || h.Owner != Current.Index) return "not your hero";
        if (h.Garrisoned) return "the hero is in a garrison";
        if (!BookSpells(h).Contains(c.Spell)) return "the hero does not know this spell";
        int mana = AdventureSpellCost(h, c.Spell);
        switch (c.Spell)
        {
            case "SPELL_ABILITY_CUSTOM1": case "SPELL_ABILITY_CUSTOM2": case "SPELL_ABILITY_CUSTOM3": case "SPELL_ABILITY_CUSTOM4":
            {
                // Can-cast FUN_00b4c430: a custom ability in the hero's disabled list (+0x2d8) cannot be cast; the cast check
                // 008652f0 has no further condition for 0x15c..0x15f. The cast 008656e0 takes the spell's mana (hero vf
                // 0x160) and posts the script event (world vf 0x134) with the hero and the ability 1..4 (FUN_00866130),
                // which runs the CUSTOM_ABILITY_TRIGGER handler.
                if (h.DisabledSpells.Contains(c.Spell)) return "the ability is disabled";
                if (h.Mana < mana) return "not enough mana";
                h.Mana -= mana;
                Emit(new AdventureSpellCast(h.Id, c.Spell, h.Pos, h.Pos));
                Emit(new CustomAbilityUsed(h.Id, c.Spell[^1] - '0'));
                return null;
            }
            case SpellDimensionDoor:
            {
                if (c.Target is not { } to) return "no target";
                int move = h.MaxMovePoints / 2;
                if (h.Mana < mana) return "not enough mana";
                if (h.MovePoints < move) return "not enough movement points";
                if (!CanDimensionDoorTo(h, to)) return "cannot travel there";
                var from = h.Pos;
                h.Mana -= mana;
                h.MovePoints -= move;
                h.Pos = to;
                SyncBoat(h);
                Emit(new AdventureSpellCast(h.Id, c.Spell, from, to));
                Emit(new HeroMoved(h.Id, new[] { from, to }, h.MovePoints));
                RevealAround(h);
                return null;
            }
            case SpellTownPortal:
            {
                if (h.Mana < mana) return "not enough mana";
                if (h.InBoat) return "cannot cast at sea";
                var town = PortalTown(h);
                if (town == null) return "no friendly town";
                var to = EntranceOf(town);
                if (HeroAt(to, h.Id) != null) return "the town entrance is occupied";
                var from = h.Pos;
                h.Mana -= mana;
                h.MovePoints = 0;
                h.Pos = to;
                Emit(new AdventureSpellCast(h.Id, c.Spell, from, to));
                Emit(new HeroMoved(h.Id, new[] { from, to }, h.MovePoints));
                RevealAround(h);
                return null;
            }
            case SpellSummonCreatures:
            {
                var town = c.TownObject >= 0 ? S.Town(c.TownObject) : NearestOwnTown(h, t => t.Garrison.StackCount() > 0);
                if (town == null || town.Owner != h.Owner) return "no friendly town with a garrison";
                var slots = (c.Stacks ?? Enumerable.Range(0, State.Army.Slots).ToList()).Distinct()
                    .Where(i => i >= 0 && i < State.Army.Slots && town.Garrison[i] is { Count: > 0 }).ToList();
                if (slots.Count == 0) return "nothing to summon";
                int creatures = slots.Sum(i => town.Garrison[i]!.Count);
                int move = h.MaxMovePoints * 3 / 4;
                if (h.Mana < mana + creatures) return "not enough mana";
                if (h.MovePoints < move) return "not enough movement points";
                // All chosen stacks must fit before anything changes.
                var trial = h.Army.Select(s => s == null ? null : new Stack(s.Creature, s.Count)).ToArray();
                foreach (var i in slots)
                    if (!trial.TryAdd(town.Garrison[i]!.Creature, town.Garrison[i]!.Count)) return "not enough army slots";
                foreach (var i in slots)
                {
                    h.Army.TryAdd(town.Garrison[i]!.Creature, town.Garrison[i]!.Count);
                    town.Garrison[i] = null;
                }
                h.Mana -= mana + creatures;
                h.MovePoints -= move;
                Emit(new AdventureSpellCast(h.Id, c.Spell, h.Pos, h.Pos));
                Emit(new ArmyChanged(h.Id, -1));
                Emit(new ArmyChanged(-1, town.Object));
                return null;
            }
            case SpellSummonBoat:
                return CastSummonBoat(h, mana);
            default:
                return "not an adventure spell";
        }
    }
}
