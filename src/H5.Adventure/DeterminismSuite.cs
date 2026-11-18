using H5.Adventure.Data;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using H5.Combat.AI;
using H5.Core.Data;
using H5.Core.Numerics;

namespace H5.Adventure;

/// <summary>
/// Cross-platform determinism reference: seeded AI-vs-AI duels, a few AI adventure days and the math library functions
/// the rules use, with the results desktop x64 computes recorded below. Desktop and browser players (.NET on
/// WebAssembly) share network games, so every platform must reproduce them bit for bit. Desktop: DeterminismSuiteTests;
/// browser and any other build: the game's <c>--determinism-check</c> (tools/web/harness/smoke.mjs <c>--only determinism</c>).
/// A deliberate rules change that moves a hash re-records it here (desktop), and the browser must then match.
/// </summary>
public static class DeterminismSuite
{
    /// <summary>A duel; <paramref name="MapHeroes"/>: the heroes made as the game's duel screen makes them (Adventure.DuelHeroes).</summary>
    public sealed record Case(string Name, int PresetA, int PresetB, ulong Seed, string Ai, bool MapHeroes = false);

    /// <summary>Every player AI-controlled for <paramref name="Days"/> days (quick combat), as h5tool adv-sim.</summary>
    public sealed record AdvCase(string Name, string Map, int Days, ulong Seed);

    public static readonly Case[] Cases =
    {
        new("duel-1v2-s5-original", 1, 2, 5, BattleAiRegistry.Original),
        new("duel-0v3-s42-original", 0, 3, 42, BattleAiRegistry.Original),
        new("duel-9v12-s99-original", 9, 12, 99, BattleAiRegistry.Original),
        new("duel-4v7-s3-original", 4, 7, 3, BattleAiRegistry.Original),
        new("duel-1v2-s5-greedy", 1, 2, 5, BattleAiRegistry.Greedy),
        new("duel-5v6-s11-greedy", 5, 6, 11, BattleAiRegistry.Greedy),
        new("mapheroes-1v2-s5-original", 1, 2, 5, BattleAiRegistry.Original, MapHeroes: true),
        new("mapheroes-6v10-s7-original", 6, 10, 7, BattleAiRegistry.Original, MapHeroes: true),
    };

    public static readonly AdvCase[] AdvCases =
    {
        new("adv-A2M1-7d-s1", "A2M1", 7, 1),
    };

    /// <summary>Results per data target ("stock" = TotE 3.1, "mmh55" = the MMH55 5.5 install), recorded on desktop x64.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Expected =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["stock"] = new Dictionary<string, string>
            {
                ["duel-1v2-s5-original"] = "d53b961916b23aed",
                ["duel-0v3-s42-original"] = "e36e5eff4ab3779b",
                ["duel-9v12-s99-original"] = "aa8a8f0210675fec",
                ["duel-4v7-s3-original"] = "042252a9f687cfd7",
                ["duel-1v2-s5-greedy"] = "a12826cc15ec138d",
                ["duel-5v6-s11-greedy"] = "18361c51f4e988d3",
                ["mapheroes-1v2-s5-original"] = "c6cc95d102b08c80",
                ["mapheroes-6v10-s7-original"] = "4a9f9a29f4f9e56f",
                ["adv-A2M1-7d-s1"] = "7e14dad10703cf6d",
            },
            ["mmh55"] = new Dictionary<string, string>
            {
                ["duel-1v2-s5-original"] = "d62a5fd1f7e121bf",
                ["duel-0v3-s42-original"] = "f49d9212a0d3d713",
                ["duel-9v12-s99-original"] = "016494b6f63b8fd5",
                ["duel-4v7-s3-original"] = "9234471acdd4ea86",
                ["duel-1v2-s5-greedy"] = "db280a7319f6de11",
                ["duel-5v6-s11-greedy"] = "cb6ce33342b2f5ec",
                ["mapheroes-1v2-s5-original"] = "3b1e536c60a31b37",
                ["mapheroes-6v10-s7-original"] = "57f967bc9230ec04",
                ["adv-A2M1-7d-s1"] = "67fda05a77fae8a3",
            },
        };

    /// <summary><see cref="MathProbes"/> as desktop x64 computes them (data independent).</summary>
    public static readonly IReadOnlyDictionary<string, string> ExpectedMath = new Dictionary<string, string>
    {
        ["math-atan2f"] = "ef31d13c48e2e1fd",
        ["math-atan2"] = "b70aee2d8341521f",
        ["math-sincos"] = "02af9d78663af5d2",
        ["math-log"] = "a28e15f4ea0a333b",
        ["math-pow"] = "cd5bd1397f0d7f76",
        ["math-asin"] = "cd829fb552e74e3e",
    };

    /// <summary>One battle, one command per <see cref="Step"/>, so a single-threaded host can spread it over frames.</summary>
    public sealed class Runner
    {
        readonly Battle _b;
        readonly IBattleAI _ai0, _ai1;
        public Case Case { get; }
        public BattleState State => _b.State;
        public int Commands { get; private set; }
        public bool Done => _b.State.Phase == BattlePhase.Finished || Commands >= 5000;

        public Runner(AdventureDb db, Case c)
        {
            Case = c;
            BattleState s;
            if (c.MapHeroes)
            {
                var (a, b) = Engine.Adventure.DuelHeroes(db, c.PresetA, c.PresetB, c.Seed);
                s = BattleSetup.CreateDuel(db.Core, a, b, c.Seed);
            }
            else s = BattleSetup.CreateDuel(db.Core, c.PresetA, c.PresetB, c.Seed);
            _b = new Battle(db.Core, s);
            _ai0 = BattleAiRegistry.Create(c.Ai);
            _ai1 = BattleAiRegistry.Create(c.Ai);
            _b.Begin();
        }

        /// <summary>Decides and applies one command (as <see cref="BattleRunner.RunToEnd"/>); returns it.</summary>
        public BattleCommand Step()
        {
            var s = _b.State;
            int side = s.Phase == BattlePhase.Tactics ? (s.TacticsDone[0] ? 1 : 0) : _b.SideToAct ?? 0;
            var cmd = (side == 0 ? _ai0 : _ai1).Decide(_b, side);
            var r = _b.Apply(cmd);
            if (!r.Ok)
            {
                var la = _b.GetLegalActions();
                cmd = la != null && !la.IsHero ? new DefendCmd(la.Actor) { Side = side } : new WaitCmd(la?.Actor ?? Actor.Hero(side)) { Side = side };
                r = _b.Apply(cmd);
                if (!r.Ok) _b.Apply(cmd = new SurrenderCmd { Side = side });
            }
            Commands++;
            return cmd;
        }
    }

    /// <summary>An adventure case, one player turn per <see cref="Step"/>.</summary>
    public sealed class AdvRunner
    {
        readonly Engine.Adventure _g;
        readonly AI.OriginalAdventureAI _ai = new();
        public AdvCase Case { get; }
        public int Turns { get; private set; }
        public AdvState State => _g.S;
        public bool Done => _g.S.Finished || _g.S.Day > Case.Days || Turns >= Case.Days * 16;

        public AdvRunner(AdventureDb db, AdvMap map, AdvCase c)
        {
            Case = c;
            (_g, _) = Engine.Adventure.NewGame(db, map, Engine.GameSetup.AllPlayers(map, humans: 0, c.Seed));
        }

        public void Step()
        {
            _ai.PlayTurn(_g, Case.Seed * 1000003UL + (ulong)Turns);
            Turns++;
        }
    }

    public static string Run(AdventureDb db, Case c, Action<int, string, string>? trace = null)
    {
        var r = new Runner(db, c);
        trace?.Invoke(0, "", r.State.ComputeHash());
        while (!r.Done)
        {
            var cmd = r.Step();
            trace?.Invoke(r.Commands, MessageJson.Serialize(cmd), r.State.ComputeHash());
        }
        return r.State.ComputeHash();
    }

    public static string Run(AdventureDb db, AdvMap map, AdvCase c, Action<int, string>? trace = null)
    {
        var r = new AdvRunner(db, map, c);
        while (!r.Done) { r.Step(); trace?.Invoke(r.Turns, r.State.ComputeHash()); }
        return r.State.ComputeHash();
    }

    /// <summary>
    /// The transcendental functions the rules use (<see cref="DetMath"/>; combat, adventure, RMG) over the inputs they get
    /// (cell offsets, stack ratios, proc chances …), one hash per function. The platform's own Math.Sin/Atan2/Log/Pow
    /// (the Windows CRT, musl in the browser, glibc) differ in the last bit for about 0.2–18 % of such inputs, so the
    /// rules call DetMath (DeterminismSuiteTests.RulesUsePortableMath); a differing hash here is a runtime that evaluates
    /// plain double / float arithmetic differently.
    /// </summary>
    public static IEnumerable<(string Name, string Hash)> MathProbes()
    {
        yield return ("math-atan2f", H(h =>
        {
            for (int y = -64; y <= 64; y++) for (int x = -64; x <= 64; x++) h.F(DetMath.Atan2(y * 0.5f, x * 0.5f));
        }));
        yield return ("math-atan2", H(h =>
        {
            for (int y = -64; y <= 64; y++) for (int x = -64; x <= 64; x++) h.D(DetMath.Atan2(y * 0.5, x * 0.5));
            for (int y = -300; y <= 300; y += 3) for (int x = -300; x <= 300; x += 3) h.D(DetMath.Atan2(X87.Add(x, 0.01f), X87.Add(y, 0.01f)));
        }));
        yield return ("math-sincos", H(h =>
        {
            foreach (float k in new[] { 0.023809524f, 0.1f, 0.03448276f, 0.07692308f })
                for (int i = 0; i <= 2048; i++) { float a = X87.Mul(i, k); h.D(X87.Sin(a)); h.D(X87.Cos(a)); }
            for (int i = -40960; i <= 40960; i += 7) { h.D(DetMath.Sin(i * 0.01)); h.D(DetMath.Cos(i * 0.01)); }
        }));
        yield return ("math-log", H(h =>
        {
            for (int a = 1; a <= 400; a++) for (int b = 1; b <= 400; b += 7) { h.D(DetMath.Log10((double)a * 13 / b)); h.D(DetMath.Log2((float)a / b)); }
            for (int i = 1; i <= 20000; i++) { h.D(DetMath.Log10(10 + i)); h.D(DetMath.Log(X87.Mul(i, 10.0))); }
            h.D(DetMath.Log(600.0));
        }));
        yield return ("math-pow", H(h =>
        {
            for (int b = 0; b <= 100; b++) for (int f = 1; f <= 40; f++) h.D(DetMath.Pow((100 - b) * (double)0.01f, f * 0.125));
            for (int a = 1; a <= 100; a++) for (int d = -1; d <= 20; d++) h.F(DetMath.Pow(a * 0.05f, d));
        }));
        yield return ("math-asin", H(h => { for (int i = 0; i <= 4096; i++) h.D(DetMath.Asin(i / 4096.0)); }));
    }

    sealed class Fnv
    {
        public ulong V = 14695981039346656037UL;
        void U(ulong x) { for (int i = 0; i < 8; i++) { V ^= (byte)(x >> (8 * i)); V *= 1099511628211UL; } }
        public void D(double d) => U((ulong)BitConverter.DoubleToInt64Bits(d));
        public void F(float f) => U((uint)BitConverter.SingleToInt32Bits(f));
    }

    static string H(Action<Fnv> fill) { var h = new Fnv(); fill(h); return h.V.ToString("x16"); }
}
