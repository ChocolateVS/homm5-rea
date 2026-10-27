using System.Text.Json.Nodes;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.AdvNet;

/// <summary>
/// What one player may know. The host never sends the full game state: each player gets a
/// redacted copy ("view") of <see cref="AdvState"/> and only the events their side may see.
///
/// Why not lockstep replicas (as duels use)? A replica needs the whole state, including other
/// players' resources, armies, the RNG and what lies under the shroud, so any client could
/// read hidden information. Adventure maps are hidden-information games, so the host is
/// authoritative and clients render views.
///
/// Rules (H5 has a shroud, not a line-of-sight fog: once a tile is explored, what stands on it
/// is visible):
/// - Your own and allied (same team) heroes, towns and purses are exact.
/// - Enemy heroes/towns are visible only on tiles you have explored; their stacks are shown as
///   the original's approximate size bands (Few, Several, Pack…), stored as the band's lower bound;
///   their skills, spells, artifacts, experience, mana and movement are hidden.
/// - Map objects under the shroud are reset to their pristine state; visible neutral guards are
///   banded; unrevealed contents (treasure amounts, bank variants, shrine spells) are hidden.
/// - The RNG, the tavern pool and other players' fog/taverns are never sent.
/// </summary>
public static class ViewFilter
{
    // Original approximate army sizes (Few 1-4 … Legion 1000+), as lower bounds.
    static readonly int[] Bands = { 1, 5, 10, 20, 50, 100, 250, 500, 1000 };

    public static int Band(int count)
    {
        if (count <= 0) return 0;
        int b = 1;
        foreach (var lo in Bands) if (count >= lo) b = lo;
        return b;
    }

    static bool Explored(PlayerState p, Tile t)
    {
        if (t.Floor < 0 || t.Floor >= p.Explored.Count) return false;
        var e = p.Explored[t.Floor];
        return e.Length > 0 && t.Y >= 0 && t.X >= 0 && t.Y * WidthOf(e.Length, p, t.Floor) + t.X < e.Length && e[t.Y * WidthOf(e.Length, p, t.Floor) + t.X];
    }

    // Explored bitmaps are row-major per floor; the width comes from the map (set by Bind).
    [ThreadStatic] static int[]? _widths;
    static int WidthOf(int _, PlayerState __, int floor) => _widths![floor];

    static void Bind(AdvMap map) => _widths = map.Floors.Select(f => f?.Width ?? 1).ToArray();

    public static bool Sees(AdvMap map, PlayerState p, Tile t)
    {
        Bind(map);
        return Explored(p, t);
    }

    static Stack?[] BandArmy(Stack?[] a) =>
        a.Select(s => s is { Count: > 0 } ? new Stack(s.Creature, Band(s.Count)) : null).ToArray();

    /// <summary>The view of <paramref name="full"/> that map player <paramref name="me"/> may see, as JSON.</summary>
    /// <param name="current">Overrides <see cref="AdvState.CurrentPlayer"/> (simultaneous turns: each playing human sees its own turn).</param>
    public static JsonNode ViewFor(AdvState full, AdvMap map, int me, int? current = null)
    {
        Bind(map);
        var v = full.Clone();
        if (current is { } cp) v.CurrentPlayer = cp;
        var mine = v.Players.Find(p => p.Index == me) ?? throw new ArgumentException($"no player {me}");
        bool Ally(int owner) => owner >= 0 && v.Players.Find(p => p.Index == owner) is { } o && o.Team == mine.Team;

        v.Rng = new Rng();
        v.HeroPool.Clear();
        foreach (var p in v.Players)
        {
            if (p.Index == me) continue;
            bool ally = p.Team == mine.Team;
            if (!ally) p.Purse = new Purse();
            p.Explored = p.Explored.Select(_ => Array.Empty<bool>()).ToList();
            p.Tavern = new string?[p.Tavern.Length];
            p.VisitedObjects.Clear();
            p.Keys.Clear();
        }

        v.Heroes = v.Heroes.Where(h => Ally(h.Owner) || (h.Alive && Explored(mine, h.Pos))).Select(h => Ally(h.Owner) ? h : RedactHero(h)).ToList();
        v.OffMapHeroes = v.OffMapHeroes.Where(h => h.Owner == me).ToList();
        // Ships are seen like map objects: on explored tiles, or carrying an allied hero.
        v.Boats = v.Boats.Where(b => Explored(mine, b.Pos) || (b.HeroId >= 0 && v.Heroes.Any(h => h.Id == b.HeroId && Ally(h.Owner)))).ToList();

        foreach (var t in v.Towns)
        {
            if (Ally(t.Owner)) continue;
            if (Explored(mine, t.Pos))
            {
                t.Garrison = BandArmy(t.Garrison);
                t.Available = new int[t.Available.Length];
                t.MageGuild = t.MageGuild.Select(_ => new List<string>()).ToList();
                t.LibrarySpells = new(); t.RuneSpells = new(); t.WarcrySpells = new();
                t.BuiltToday = false;
            }
            else
            {
                t.Owner = -1;
                t.Built = new();
                t.Garrison = Army.Empty();
                t.Available = new int[t.Available.Length];
                t.MageGuild = t.MageGuild.Select(_ => new List<string>()).ToList();
                t.LibrarySpells = new(); t.RuneSpells = new(); t.WarcrySpells = new();
                t.BuiltToday = false;
                t.Specialization = null;
            }
        }

        foreach (var key in v.Objects.Keys.ToList())
        {
            var o = v.Objects[key];
            var pos = key < map.Objects.Count ? map.Objects[key].Pos : default;
            if (!Explored(mine, pos)) { v.Objects[key] = new ObjState { Object = key }; continue; }
            bool owned = Ally(o.Owner);
            o.Guards = o.Guards.Select(g => new Stack(g.Creature, Band(g.Count))).ToList();
            o.Amount = 0;
            o.Variant = -1;
            if (!owned) { o.Available = new int[o.Available.Length]; o.RechargeDay = 0; }
            o.VisitedBy = o.VisitedBy.Where(Ally).ToHashSet();
            if (!o.VisitedBy.Contains(me) && !owned) o.SpellId = null;
        }

        if (v.Battle is { } b)
        {
            int atkOwner = full.Heroes.Find(h => h.Id == b.AttackerHero)?.Owner ?? -1;
            int defOwner = b.DefenderHero >= 0 ? full.Heroes.Find(h => h.Id == b.DefenderHero)?.Owner ?? -1
                : b.DefenderObject >= 0 ? full.Town(b.DefenderObject)?.Owner ?? -1 : -1;
            if (atkOwner != me && defOwner != me) v.Battle = null;
        }
        if (v.Choice is { } c && full.Heroes.Find(h => h.Id == c.Hero)?.Owner != me) v.Choice = null;

        return JsonNode.Parse(v.Serialize())!;
    }

    static HeroState RedactHero(HeroState h) => new()
    {
        Id = h.Id, DefId = h.DefId, Name = h.Name, Owner = h.Owner, Pos = h.Pos, Class = h.Class, Town = h.Town,
        Specialization = h.Specialization, Level = h.Level, Army = BandArmy(h.Army), InBoat = h.InBoat, BoatId = h.BoatId,
        InTown = h.InTown, Garrisoned = h.Garrisoned, Alive = h.Alive,
    };

    // ------------------------------------------------------------------ events

    /// <summary>
    /// Events of one command that map player <paramref name="me"/> may see. <paramref name="actor"/>
    /// issued the command (-1 for host-initiated changes). Uses the post-command state for
    /// visibility and ownership; <paramref name="heroPos"/> has positions from before the command
    /// for heroes that died or left.
    /// </summary>
    public static List<AdvEvent> EventsFor(IEnumerable<AdvEvent> events, AdvState full, AdvMap map, int me, int actor,
        IReadOnlyDictionary<int, (int Owner, Tile Pos)> heroPos)
    {
        Bind(map);
        var mine = full.Players.Find(p => p.Index == me)!;
        bool Ally(int owner) => owner >= 0 && full.Players.Find(p => p.Index == owner) is { } o && o.Team == mine.Team;
        (int Owner, Tile Pos) Hero(int id) =>
            full.Heroes.Find(h => h.Id == id) is { } h ? (h.Owner, h.Pos) : heroPos.TryGetValue(id, out var x) ? x : (-1, default);
        bool HeroSeen(int id) { var (o, p) = Hero(id); return Ally(o) || Explored(mine, p); }
        bool HeroMine(int id) => Ally(Hero(id).Owner);
        Tile ObjPos(int idx) => idx >= 0 && idx < map.Objects.Count ? map.Objects[idx].Pos : default;
        bool TownMine(int idx) => full.Town(idx) is { } t && Ally(t.Owner);
        bool InBattle()
        {
            if (full.Battle is not { } b) return false;
            return HeroMine(b.AttackerHero) || (b.DefenderHero >= 0 && HeroMine(b.DefenderHero)) || (b.DefenderObject >= 0 && TownMine(b.DefenderObject));
        }

        var outList = new List<AdvEvent>();
        foreach (var e in events)
        {
            AdvEvent? keep = e switch
            {
                H5.Adventure.Engine.TurnStarted or DayStarted or WeekStarted or PlayerEliminated or H5.Adventure.Engine.GameEnded => e,
                HeroMoved m when HeroMine(m.HeroId) => m,
                HeroMoved m => m.Path.Where(t => Explored(mine, t)).ToList() is { Count: > 0 } seen ? m with { Path = seen, MovePointsLeft = 0 } : null,
                ObjectVisited ov => HeroMine(ov.HeroId) || Explored(mine, ObjPos(ov.Object)) ? ov : null,
                ObjectRemoved or_ => Explored(mine, ObjPos(or_.Object)) ? or_ : null,
                ObjectCaptured oc => Ally(oc.Player) || Explored(mine, ObjPos(oc.Object)) ? oc : null,
                ResourcesChanged rc => rc.Player == me ? rc : null,
                HeroStatChanged hs => HeroMine(hs.HeroId) ? hs : null,
                ArtifactGained ag => HeroMine(ag.HeroId) ? ag : null,
                SpellLearned sl => HeroMine(sl.HeroId) ? sl : null,
                ExperienceGained xg => HeroMine(xg.HeroId) ? xg : null,
                LevelUpOffered lu => HeroMine(lu.HeroId) ? lu : null,
                SkillLearned sk => HeroMine(sk.HeroId) ? sk : null,
                BattleRequested br => HeroMine(br.AttackerHero) || (br.DefenderHero >= 0 && HeroMine(br.DefenderHero))
                    || (br.DefenderObject >= 0 && TownMine(br.DefenderObject)) ? br : null,
                BattleResolved => actor == me || InBattle() ? e : null,
                ChoiceOffered co => HeroMine(co.HeroId) ? co : null,
                BuildingBuilt bb => TownMine(bb.TownObject) || Explored(mine, ObjPos(bb.TownObject)) ? bb : null,
                CreaturesRecruited cr => TownMine(cr.TownObject) ? cr : null,
                HeroHired hh => HeroMine(hh.HeroId) || Explored(mine, ObjPos(hh.TownObject)) ? hh : null,
                HeroRemoved hr => HeroSeen(hr.HeroId) ? hr : null,
                ArmyChanged ac => (ac.HeroId >= 0 && HeroMine(ac.HeroId)) || (ac.TownObject >= 0 && TownMine(ac.TownObject)) ? ac : null,
                TilesRevealed tr => tr.Player == me ? tr : null,
                BoatCreated bc => Explored(mine, bc.Pos) ? bc : null,
                BoatMoved bm => Explored(mine, bm.From) || Explored(mine, bm.To) ? bm : null,
                HeroEmbarked he => HeroSeen(he.HeroId) ? he : null,
                HeroDisembarked hd => HeroSeen(hd.HeroId) ? hd : null,
                H5.Adventure.Engine.Message => actor == me || actor < 0 ? e : null,
                _ => actor == me ? e : null,
            };
            if (keep != null) outList.Add(keep);
        }
        return outList;
    }
}
