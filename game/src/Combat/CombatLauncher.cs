using Godot;
using H5.Combat;

namespace H5Reborn.Combat;

/// <summary>
/// Entry points into combat. Dev args (after `--`):
/// `--combat-vs-ai=A,B` local duel of preset A (you) against preset B (AI);
/// `--combat-seed=N` fixes the battle seed; `--autoplay` lets the AI play your side too;
/// `--quit-when-done` exits after the results screen; `--results-screenshot=PATH`;
/// `--combat-speed=N` plays animations N times faster; `--dump-cursors=DIR` writes the
/// original cursors as PNGs.
/// </summary>
public static class CombatLauncher
{
    static ulong? _seed;
    public static bool QuitWhenDone;
    /// <summary>`--results-screenshot=PATH`: saves the screen once the results are shown.</summary>
    public static string? ResultsScreenshot;
    /// <summary>`--fake-mouse=x,y`: hover as if the mouse were there, drawing the cursor in-frame (screenshots).</summary>
    public static Vector2? FakeMouse;
    /// <summary>`--skip-tactics`: the local player ends the tactics phase immediately.</summary>
    public static bool SkipTactics;
    /// <summary>`--attack-screenshot=N:PATH`: saves the screen at the hit moment of the Nth attack.</summary>
    public static (int N, string Path)? AttackScreenshot;
    /// <summary>`--sign-screenshot=N:path`: capture 0.4 s after the Nth casualty sign appears.</summary>
    public static (int N, string Path)? SignScreenshot;
    /// <summary>`--shot-screenshot=N:path` / `--spell-screenshot=N:path`: capture mid-flight / mid-visual.</summary>
    public static (int N, string Path)? ShotScreenshot, SpellScreenshot;
    /// <summary>`--open-spellbook`: autoplay the local side until its hero acts, then open the spellbook.</summary>
    public static bool OpenSpellbook;
    /// <summary>`--move-frames=N:PREFIX`: frame sequence from the start of the Nth move.</summary>
    public static (int N, string Prefix, int Count, double Interval)? MoveFrames;
    static string SiegeTown = "TOWN_HEAVEN";
    static int SiegeFort = 3;
    static (int A, int B) SiegePresets = (0, 3);

    static void StartSiegeFromArgs()
    {
        var db = Game.Instance.Db;
        var seed = _seed ?? 1UL;
        var (a, b) = SiegePresets;
        var state = BattleSetup.Create(db, ArmySetup.FromHero(db.DuelPresets[a].Hero), ArmySetup.FromHero(db.DuelPresets[b].Hero), seed,
            siege: new SiegeSetup { Town = SiegeTown, FortLevel = SiegeFort, TownLevel = 20, TownMaxLevel = 36 });
        GD.Print($"[H5] siege {SiegeTown} fort {SiegeFort}, presets {a} vs {b}, seed {seed}");
        CombatScreen.Open(new LocalCombatSession(db, state, a, b, seed, CombatScreen.Autoplay ? Array.Empty<int>() : new[] { 0 }));
    }

    static (int, string) ParseShot(string v)
    {
        int colon = v.IndexOf(':');
        return (int.Parse(v[..colon]), v[(colon + 1)..]);
    }
    /// <summary>`--autoplay-turns=N`: the AI plays the local side's first N commands, then hands over.</summary>
    public static int AutoplayTurns;
    /// <summary>`--hover-target=melee|shoot`: point the simulated mouse at the first such legal target.</summary>
    public static string? HoverTarget;
    /// <summary>`--info-unit=ID`: opens the creature info screen for that unit at start.</summary>
    public static int? InfoUnit;

    public static bool HandleArg(string arg)
    {
        if (arg.StartsWith("--combat-vs-ai="))
        {
            var parts = arg["--combat-vs-ai=".Length..].Split(',');
            int a = int.Parse(parts[0]), b = parts.Length > 1 ? int.Parse(parts[1]) : 1;
            Callable.From(() => StartVsAi(a, b, _seed ?? (ulong)Time.GetTicksUsec())).CallDeferred();
            return true;
        }
        if (arg.StartsWith("--combat-seed=")) { _seed = ulong.Parse(arg["--combat-seed=".Length..]); return true; }
        if (arg.StartsWith("--camera-script=")) { CombatCamera.DevScript = arg["--camera-script=".Length..]; return true; }
        if (arg == "--no-cinematic") { CinematicCameras.Enabled = false; return true; }
        if (arg.StartsWith("--move-frames="))
        {
            // --move-frames=N:PREFIX : frames from the start of the Nth move (count/interval below).
            var parts = arg["--move-frames=".Length..].Split(':', 2);
            MoveFrames = (int.Parse(parts[0]), parts[1], MoveFrames?.Count ?? 8, MoveFrames?.Interval ?? 0.1);
            return true;
        }
        if (arg.StartsWith("--move-frames-count=") && MoveFrames is { } mfc)
        { MoveFrames = mfc with { Count = int.Parse(arg["--move-frames-count=".Length..]) }; return true; }
        if (arg.StartsWith("--move-frames-interval=") && MoveFrames is { } mfi)
        { MoveFrames = mfi with { Interval = double.Parse(arg["--move-frames-interval=".Length..], System.Globalization.CultureInfo.InvariantCulture) }; return true; }
        if (arg == "--autoplay") { CombatScreen.Autoplay = true; return true; }
        if (arg.StartsWith("--fake-mouse="))
        {
            var xy = arg["--fake-mouse=".Length..].Split(',');
            FakeMouse = new Vector2(float.Parse(xy[0]), float.Parse(xy[1]));
            return true;
        }
        if (arg.StartsWith("--autoplay-turns=")) { AutoplayTurns = int.Parse(arg["--autoplay-turns=".Length..]); return true; }
        if (arg.StartsWith("--hover-target=")) { HoverTarget = arg["--hover-target=".Length..]; return true; }
        if (arg.StartsWith("--info-unit=")) { InfoUnit = int.Parse(arg["--info-unit=".Length..]); return true; }
        if (arg == "--skip-tactics") { SkipTactics = true; return true; }
        if (arg.StartsWith("--attack-screenshot="))
        {
            var v = arg["--attack-screenshot=".Length..];
            int colon = v.IndexOf(':');
            AttackScreenshot = (int.Parse(v[..colon]), v[(colon + 1)..]);
            return true;
        }
        if (arg.StartsWith("--combat-siege="))
        {
            // --combat-siege=TOWN_HEAVEN,3[,presetA,presetB]: local siege, attacker = player.
            var parts = arg["--combat-siege=".Length..].Split(',');
            SiegeTown = parts[0];
            SiegeFort = parts.Length > 1 ? int.Parse(parts[1]) : 3;
            if (parts.Length > 3) { SiegePresets = (int.Parse(parts[2]), int.Parse(parts[3])); }
            Callable.From(StartSiegeFromArgs).CallDeferred();
            return true;
        }
        if (arg == "--open-spellbook") { OpenSpellbook = true; return true; }
        if (arg.StartsWith("--shot-screenshot=")) { ShotScreenshot = ParseShot(arg["--shot-screenshot=".Length..]); return true; }
        if (arg.StartsWith("--spell-screenshot=")) { SpellScreenshot = ParseShot(arg["--spell-screenshot=".Length..]); return true; }
        if (arg.StartsWith("--sign-screenshot="))
        {
            var v = arg["--sign-screenshot=".Length..];
            int colon = v.IndexOf(':');
            SignScreenshot = (int.Parse(v[..colon]), v[(colon + 1)..]);
            return true;
        }
        if (arg.StartsWith("--results-screenshot=")) { ResultsScreenshot = arg["--results-screenshot=".Length..]; return true; }
        if (arg.StartsWith("--combat-speed="))
        {
            // Speeds up animation playback for automated runs.
            Engine.TimeScale = double.Parse(arg["--combat-speed=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        if (arg == "--quit-when-done") { QuitWhenDone = true; CombatScreen.QuitWhenDone = true; return true; }
        if (arg == "--check-preset-models") { Callable.From(CheckPresetModels).CallDeferred(); return true; }
        if (arg.StartsWith("--dump-cursors="))
        {
            var dir = arg["--dump-cursors=".Length..];
            Callable.From(() => CombatCursors.DumpAll(dir)).CallDeferred();
            return true;
        }
        return false;
    }

    /// <summary>Dev check: builds every hero and creature character used by the duel presets, then quits.</summary>
    static void CheckPresetModels()
    {
        var db = Game.Instance.Db;
        var seen = new HashSet<string>();
        int ok = 0, failed = 0;
        void Try(string what, string? path)
        {
            if (path == null || !seen.Add(path)) return;
            try { var c = Render.CharacterBuilder.Build(path); c.Free(); ok++; }
            catch (Exception e) { failed++; GD.PushError($"[H5] model check {what} {path}: {e.Message}"); }
        }
        foreach (var p in db.DuelPresets)
        {
            foreach (var slot in p.Hero.Army) Try(slot.Creature, Arena.CreatureModels.CharacterPathFor(slot.Creature));
            var shared = p.Hero.Shared;
            if (shared != null && Game.Instance.Fs.Exists(shared.SourcePath))
            {
                var r = H5.Core.Xdb.XdbRef.Parse((string?)Game.Instance.Xdb.Load(shared.SourcePath).Root!.Element("HeroCharacterArena")?.Attribute("href"), shared.SourcePath);
                if (!r.IsEmpty) Try(shared.Id, r.Path);
            }
        }
        GD.Print($"[H5] model check: {ok} built, {failed} failed");
        ((SceneTree)Engine.GetMainLoop()).Quit();
    }

    /// <summary>Hot-seat duel: both sides are commanded on this machine (presets picked at random for now).</summary>
    public static void StartHotseat()
    {
        var seed = (ulong)Time.GetTicksUsec();
        var (a, b) = RandomPresets(seed);
        var session = new LocalCombatSession(Game.Instance.Db, a, b, seed, new[] { 0, 1 });
        GD.Print($"[H5] hot-seat duel {a} vs {b}, seed {seed}");
        CombatScreen.Open(session);
    }

    public static void StartVsAiRandom()
    {
        var seed = (ulong)Time.GetTicksUsec();
        var (a, b) = RandomPresets(seed);
        StartVsAi(a, b, seed);
    }

    static (int, int) RandomPresets(ulong seed)
    {
        int n = Math.Max(2, Game.Instance.Db.DuelPresets.Count);
        int a = (int)(seed % (ulong)n), b = (int)((seed / 7 + 1) % (ulong)n);
        if (b == a) b = (a + 1) % n;
        return (a, b);
    }

    /// <summary>A local duel: the player commands side 0, the built-in AI side 1.</summary>
    public static void StartVsAi(int presetA, int presetB, ulong seed)
    {
        var session = new LocalCombatSession(Game.Instance.Db, presetA, presetB, seed, new[] { 0 });
        GD.Print($"[H5] local duel {presetA} vs {presetB}, seed {seed}");
        CombatScreen.Open(session);
    }
}
