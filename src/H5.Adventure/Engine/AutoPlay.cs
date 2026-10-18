using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using H5.Core.Data;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.Engine;

/// <summary>Resolves the pending adventure battle by letting the combat AI play both sides ("quick combat").</summary>
public static class QuickCombat
{
    public static ResolveBattleCmd Resolve(Adventure game, ulong seed, IBattleAI? ai = null)
    {
        ai ??= new GreedyBattleAI { MaxCandidates = 12 };
        var state = game.CreatePendingBattle(seed);
        var battle = new Battle(game.Core, state);
        battle.Begin();
        BattleRunner.RunToEnd(battle, ai, ai);
        List<ArmySlot> Survivors(int side) => state.Units
            .Where(u => u.Side == side && u.Alive && !u.Summoned && !u.IsWarMachine)
            .GroupBy(u => u.CreatureId).Select(g => new ArmySlot(g.Key, g.Sum(u => u.Count))).ToList();
        int winner = state.Winner ?? 1;
        return new ResolveBattleCmd(winner, Survivors(0), Survivors(1)) { Player = game.Current.Index };
    }
}

/// <summary>
/// A deliberately simple adventure AI used for simulations and smoke tests (not the original AI):
/// builds and recruits in towns, walks heroes to the nearest affordable target (pickups, mines,
/// weak neutrals, enemy towns), answers choices, and quick-combats every battle.
/// </summary>
public sealed class SimpleAdventureBot
{
    /// <summary>Routes commands through the game's command sink (e.g. the map-script host) when one is set.</summary>
    static AdvResult Send(Adventure g, AdvCommand c) => g.CommandSink?.Invoke(c) ?? g.Apply(c);

    public int MaxActionsPerTurn { get; init; } = 60;

    public void PlayTurn(Adventure g, ulong battleSeedBase)
    {
        var me = g.Current.Index;
        int actions = 0;
        foreach (var t in g.S.Towns.Where(t => t.Owner == me).ToList())
        {
            TryBuild(g, t);
            RecruitAll(g, t);
        }
        foreach (var h in g.S.Heroes.Where(h => h.Alive && h.Owner == me).ToList())
        {
            for (int step = 0; step < 6 && actions < MaxActionsPerTurn && h.Alive && !g.S.Finished; step++)
            {
                ResolvePending(g, battleSeedBase + (ulong)actions);
                while (h.PendingLevelUps.Count > 0) Send(g, new ChooseLevelUpCmd(h.Id, 0) { Player = me });
                var target = PickTarget(g, h);
                if (target == null) break;
                var r = Send(g, new MoveHeroCmd(h.Id, target.Value) { Player = me });
                actions++;
                if (!r.Ok) break;
                ResolvePending(g, battleSeedBase + (ulong)actions);
                while (h.Alive && h.PendingLevelUps.Count > 0) Send(g, new ChooseLevelUpCmd(h.Id, 0) { Player = me });
                if (h.InTown >= 0 && g.S.Town(h.InTown) is { } home && home.Owner == me) TakeGarrison(g, h, home);
                if (h.MovePoints < 100) break;
            }
        }
        ResolvePending(g, battleSeedBase + 999);
        foreach (var h in g.S.Heroes.Where(h => h.Alive && h.Owner == me))
            while (h.PendingLevelUps.Count > 0) Send(g, new ChooseLevelUpCmd(h.Id, 0) { Player = me });
        if (!g.S.Finished) Send(g, new EndTurnCmd { Player = me });
    }

    static void TakeGarrison(Adventure g, HeroState h, TownState t)
    {
        for (int from = 0; from < Army.Slots; from++)
        {
            if (t.Garrison[from] is not { Count: > 0 } s) continue;
            int to = Array.FindIndex(h.Army, x => x is { Count: > 0 } && x.Creature == s.Creature);
            if (to < 0) to = Array.FindIndex(h.Army, x => x is not { Count: > 0 });
            if (to < 0) break;
            Send(g, new TransferStackCmd(-1, t.Object, from, h.Id, -1, to, s.Count) { Player = h.Owner });
        }
    }

    static void ResolvePending(Adventure g, ulong seed)
    {
        for (int guard = 0; guard < 8 && (g.S.Battle != null || g.S.Choice != null); guard++)
        {
            if (g.S.Battle != null) Send(g, QuickCombat.Resolve(g, seed + (ulong)guard));
            else if (g.S.Choice is { } c)
                Send(g, new AnswerChoiceCmd(Accept: c.Kind != "join_paid" || g.Current.Purse[Purse.Gold] >= c.Gold * 2, Option: c.Kind == "chest" ? 0 : 0) { Player = g.Current.Index });
        }
    }

    static void TryBuild(Adventure g, TownState t)
    {
        if (t.BuiltToday) return;
        // Priority: town hall, dwellings low→high, fort, guild, tavern, market, rest.
        var order = new List<string> { "TB_TOWN_HALL" };
        for (int i = 1; i <= 7; i++) order.Add($"TB_DWELLING_{i}");
        order.AddRange(new[] { "TB_FORT", "TB_MAGIC_GUILD", "TB_TAVERN", "TB_MARKETPLACE", "TB_BLACKSMITH" });
        foreach (var b in order.Concat(t.MaxLevel.Keys.Except(order).OrderBy(k => k, StringComparer.Ordinal)))
            if (Send(g, new BuildCmd(t.Object, b) { Player = t.Owner }).Ok) return;
    }

    static void RecruitAll(Adventure g, TownState t)
    {
        for (int tier = 7; tier >= 1; tier--)
        {
            int variant = g.DwellingCreature(t, tier, 1) != null ? 1 : 0;
            var cr = g.DwellingCreature(t, tier, variant);
            if (cr == null || !g.Core.Creatures.TryGetValue(cr, out var cd) || t.Available[tier - 1] <= 0) continue;
            int gold = Math.Max(1, cd.Cost.Gold);
            int n = Math.Min(t.Available[tier - 1], g.Current.Purse[Purse.Gold] / gold);
            while (n > 0 && !Send(g, new RecruitCmd(t.Object, tier, n, variant) { Player = t.Owner }).Ok) n /= 2;
        }
    }

    static Tile? PickTarget(Adventure g, HeroState h)
    {
        double myPower = g.ArmyPower(h.Army.Live(), h);
        Tile? best = null;
        double bestScore = double.MaxValue;
        foreach (var o in g.Map.Objects)
        {
            if (o.Pos.Floor != h.Pos.Floor || o.Kind == ObjectKind.Static) continue;
            if (g.S.Objects.TryGetValue(o.Index, out var os) && os.Removed) continue;
            double value = o.Kind switch
            {
                ObjectKind.Treasure or ObjectKind.Artifact => 1,
                ObjectKind.Mine or ObjectKind.Dwelling when os == null || os.Owner != h.Owner => 1.5,
                ObjectKind.Monster when os != null && g.ArmyPower(os.Guards, null) * 1.2 < myPower => 2,
                ObjectKind.Town when g.S.Town(o.Index) is { } t && t.Owner != h.Owner => 3,
                // Go home to pick up recruits waiting in the garrison.
                ObjectKind.Town when g.S.Town(o.Index) is { } t && t.Owner == h.Owner && h.InTown != t.Object && t.Garrison.StackCount() > 0 => 2,
                _ => 0,
            };
            if (value <= 0) continue;
            if (os is { Guards.Count: > 0 } && o.Kind != ObjectKind.Monster && g.ArmyPower(os.Guards, null) * 1.5 > myPower) continue;
            var tile = o.Kind == ObjectKind.Monster ? o.Pos : o.ActiveTiles.FirstOrDefault(o.Pos);
            // Don't walk into the guard zone of a stack we can't beat.
            if (o.Kind != ObjectKind.Monster && g.GuardingMonster(tile) is { } guard &&
                g.S.Objects.TryGetValue(guard.Index, out var gs) && g.ArmyPower(gs.Guards, null) * 1.5 > myPower) continue;
            int d = Math.Max(Math.Abs(tile.X - h.Pos.X), Math.Abs(tile.Y - h.Pos.Y));
            if (d == 0 || d > 25) continue;
            double score = d / value;
            if (score < bestScore) { bestScore = score; best = tile; }
        }
        if (best == null) return null;
        return g.FindPath(h, best.Value) != null ? best : null;
    }
}
