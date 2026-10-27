using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Adventure-map spells (GameMechanics/Spell/Adventure_Spells, MAGIC_SCHOOL_ADVENTURE). Mana costs are the
// spells' TrainedCost; the rules are the stock spell texts (Text/Game/Spells/Adventure/*) and the fan
// manual's Adventure Magic section (p.184):
//   Instant Travel (SPELL_DIMENSION_DOOR, 15 mana): teleport within 800 movement points "whatever the
//     terrain type" (100 per side step, 141 per diagonal); costs half the hero's daily movement; land to
//     land or sea to sea only.
//   Town Portal (SPELL_TOWN_PORTAL, 20 mana): to the nearest own town (see PortalTown); all movement points are lost.
//   Summon Creatures (SPELL_SUMMON_CREATURES, 0 + 1 mana per creature): chosen stacks from the nearest own
//     town's garrison; takes 75% of the hero's starting movement points.
//   Vessel of Shalassa (SPELL_SUMMON_BOAT, 4 mana): brings the nearest empty ship next to the hero
//     (see Adventure.Boats.cs for the exe evidence).
// TODO(exe): exact tie-breaks for "nearest" (straight-line distance across floors is assumed here) and
// whether a spell may be cast with fewer movement points than it costs (rejected here).

public sealed record CastAdventureSpellCmd(int HeroId, string Spell, Tile? Target = null, int TownObject = -1, List<int>? Stacks = null) : AdvCommand;

public sealed record AdventureSpellCast(int HeroId, string Spell, Tile From, Tile To) : AdvEvent;

public sealed partial class Adventure
{
    public const string SpellDimensionDoor = "SPELL_DIMENSION_DOOR";
    public const string SpellTownPortal = "SPELL_TOWN_PORTAL";
    public const string SpellSummonCreatures = "SPELL_SUMMON_CREATURES";
    public const string SpellSummonBoat = "SPELL_SUMMON_BOAT";

    /// <summary>Instant Travel range in movement points (fan manual p.184).</summary>
    public const int DimensionDoorRange = 800;

    public int AdventureSpellCost(string spell) => Db.Core.Spell(spell)?.ManaCost ?? 0;

    /// <summary>Straight movement cost between two tiles ignoring terrain: 100 per side step, 141 per diagonal.</summary>
    public static int FlatMoveCost(Tile a, Tile b)
    {
        int dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
        int diag = Math.Min(dx, dy);
        return diag * 141 + (Math.Max(dx, dy) - diag) * 100;
    }

    /// <summary>Whether Instant Travel may land on <paramref name="t"/> (free, explored, same land/sea kind, in range).</summary>
    public bool CanDimensionDoorTo(HeroState h, Tile t)
    {
        if (t.Floor != h.Pos.Floor || FlatMoveCost(h.Pos, t) > DimensionDoorRange) return false;
        var f = Map.Floor(t.Floor);
        if (f == null || !f.InBounds(t.X, t.Y) || f.IsBlocked(t.X, t.Y)) return false;
        if (f.IsWater(t.X, t.Y) != h.InBoat) return false;
        if (HeroAt(t) != null || ObjectsAt(t).Any()) return false;
        var p = PlayerByIndex(h.Owner);
        return p != null && t.Floor < p.Explored.Count && p.Explored[t.Floor][t.Y * f.Width + t.X];
    }

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
        int mana = AdventureSpellCost(c.Spell);
        switch (c.Spell)
        {
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
