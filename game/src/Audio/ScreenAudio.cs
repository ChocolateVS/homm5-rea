using Godot;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Assets.Audio;
using H5Reborn.Adventure;

namespace H5Reborn.Audio;

// Per-screen sound scenes (NSound::CSoundScreen family). Each is a child node attached by a
// one-line hook in the screen's _Ready; it pushes the screen's music player on _Ready and
// pops it when the screen leaves the tree, so screens stack exactly like the original's
// CMusicPlayerStack.

/// <summary>Front-end music from UIGameRoot musics: MAIN_MENU_MUSIC_ID, MP_MAIN_MENU_MUSIC_ID, CREDITS_SCREEN_MUSIC_ID.</summary>
public partial class MenuAudio : Node
{
    string _id = "";
    bool _overlay;
    MusicManager.Handle? _handle;

    /// <summary>A front-end root screen: replaces whatever was playing.</summary>
    public static void Attach(Node host, string musicId) => host.AddChild(new MenuAudio { _id = musicId, Name = "MenuAudio" });

    /// <summary>
    /// A screen opened over the menu (credits, 00793d20): its music is pushed on the player
    /// stack and popped when the screen closes, so the menu theme resumes where it paused.
    /// </summary>
    public static void AttachOverlay(Node host, string musicId) =>
        host.AddChild(new MenuAudio { _id = musicId, _overlay = true, Name = "MenuAudio" });

    public override void _Ready()
    {
        AudioSystem.EnsureInstalled();
        var calc = new ConstantMusicCalculator(AudioSystem.Data.UiMusic(_id));
        if (_overlay) _handle = MusicManager.Push(_id, calc);
        else MusicManager.SetRoot(_id, calc);
    }

    public override void _ExitTree() => MusicManager.Pop(_handle);
}

/// <summary>
/// Adventure map: music from the hero/terrain/campaign calculators (FUN_00815d60), the
/// race's wait music while other players move (FUN_008162d0), and terrain ambience from
/// the 3×3 tiles around the view centre (CAdvAmbientSound, 00815400).
/// </summary>
public partial class AdventureAudio : Node
{
    AdventureScreen _screen = null!;
    MusicManager.Handle? _music, _wait;
    AmbientPlayer _ambient = null!;
    double _ambientTimer;
    MissionMusic? _mission;
    readonly Random _rng = new();

    public static void Attach(AdventureScreen screen) => screen.AddChild(new AdventureAudio { _screen = screen, Name = "AdventureAudio" });

    public override void _Ready()
    {
        AudioSystem.EnsureInstalled();
        var data = AudioSystem.Data;
        if (Campaign.CampaignFlow.Active is { } am && am.Session == _screen.Session)
            _mission = data.Mission(am.Campaign.Path, am.Mission.Index);

        var list = new List<IMusicCalculator>
        {
            new FuncMusicCalculator(HeroMusic),
            new FuncMusicCalculator(TerrainMusic),
            new ConstantMusicCalculator(_mission?.CampaignAdventure),
        };
        IMusicCalculator calc;
        if (_mission != null)
        {
            list.Add(CompositeMusicCalculator.Of(_mission.AdditionalAdventure));
            calc = new StartThenMusicCalculator(CompositeMusicCalculator.Of(_mission.Start), new CompositeMusicCalculator(list));
        }
        else calc = new CompositeMusicCalculator(list);
        _music = MusicManager.Push("adventure", calc);

        _ambient = new AmbientPlayer { Name = "Ambient" };
        AddChild(_ambient);
    }

    bool _emitters;

    public override void _ExitTree()
    {
        MusicManager.Pop(_wait);
        MusicManager.Pop(_music);
    }

    HeroState? Selected => _screen.SelectedHeroId >= 0 ? _screen.Session.S.Hero(_screen.SelectedHeroId) : null;

    MusicTrack? HeroMusic()
    {
        if (Selected is not { } h) return null;
        var def = Game.Instance.Db.Heroes.GetValueOrDefault(h.DefId);
        return def == null ? null : AudioSystem.Data.HeroMusic(def.SourcePath);
    }

    MusicTrack? TerrainMusic()
    {
        if (Selected is not { } h) return null;
        return TilePath(h.Pos.Floor, h.Pos.X, h.Pos.Y) is { } p ? AudioSystem.Data.Tile(p).Music : null;
    }

    /// <summary>The (AdvMapTile) of a tile: the topmost terrain layer covering at least half of it (as the map loader decides tile types).</summary>
    string? TilePath(int floor, int x, int y)
    {
        var f = _screen.Session.Map.Floor(floor);
        if (f?.Data is not { } t || t.Layers.Count == 0 || x < 0 || y < 0 || x >= f.Width || y >= f.Height) return null;
        for (int li = t.Layers.Count - 1; li > 0; li--)
        {
            var g = t.Layers[li].Weights;
            if (g[x, y] + g[x + 1, y] + g[x, y + 1] + g[x + 1, y + 1] >= 4 * 128) return t.Layers[li].TilePath;
        }
        return t.Layers[0].TilePath;
    }

    public override void _Process(double delta)
    {
        // Positional object sounds and hero footsteps live in the 3D world (the camera listens);
        // the screen builds its world after this node is attached.
        if (!_emitters && _screen.World is { } w && w.IsInsideTree()) { _emitters = true; w.AddChild(new MapSoundEmitters(_screen)); }
        UpdateWaitMusic();
        _ambientTimer -= delta;
        if (_ambientTimer > 0) return;
        _ambientTimer = 0.25;
        UpdateAmbient();
    }

    void UpdateWaitMusic()
    {
        var s = _screen.Session;
        bool waiting = !s.IsHumanTurn && !s.S.Finished;
        if (waiting && _wait == null)
        {
            var race = AudioSystem.Data.Race(s.Human.Race);
            var tracks = new List<MusicTrack?> { race?.Wait };
            if (_mission != null) tracks.AddRange(_mission.AdditionalWait);
            _wait = MusicManager.Push("wait", CompositeMusicCalculator.Of(tracks));
        }
        else if (!waiting && _wait != null) { MusicManager.Pop(_wait); _wait = null; }
    }

    void UpdateAmbient()
    {
        if (Town.TownScreen.Current != null) { _ambient.SetAmbient(null); return; }
        var world = _screen.World;
        var f = _screen.Session.Map.Floor(world.Floor);
        if (f == null) return;
        var target = world.Camera.Target;
        int cx = (int)Math.Floor(target.X), cy = (int)Math.Floor(target.Y), floor = world.Floor;
        var set = AdvAmbientPicker.Pick(cx, cy, f.Width, f.Height,
            (x, y) => TilePath(floor, x, y) is { } p ? AudioSystem.Data.Tile(p).Ambient : null,
            (x, y) => world.IsExplored(new Tile(x, y, floor)),
            AudioSystem.Data.UiAmbient("INVISIBLE_AMBIENT_SOUND"), _rng);
        _ambient.SetAmbient(set);
    }
}

/// <summary>
/// Combat: both armies' race combat themes plus campaign extras (FUN_008186b0); sieges play
/// the town race's SiegeMusic first (CSiegeMusicCalculator); the arena's AmbientSoundSet;
/// and the local side's Win/Loose/RetreatCombatMusic when the battle ends.
/// </summary>
public partial class CombatAudio : Node
{
    Combat.CombatScreen _screen = null!;
    MusicManager.Handle? _music;
    bool _ended;

    public static void Attach(Combat.CombatScreen screen) => screen.AddChild(new CombatAudio { _screen = screen, Name = "CombatAudio" });

    static string SideRace(H5.Combat.BattleState s, int side)
    {
        var h = s.Heroes[side];
        if (h.Present && !string.IsNullOrEmpty(h.Town)) return h.Town;
        return s.Units.FirstOrDefault(u => u.Side == side && !string.IsNullOrEmpty(u.Town))?.Town ?? "TOWN_NO_TYPE";
    }

    public override void _Ready()
    {
        AudioSystem.EnsureInstalled();
        var data = AudioSystem.Data;
        var s = _screen.Session.Battle.State;
        var tracks = new List<MusicTrack?>();
        foreach (var race in new[] { SideRace(s, 0), SideRace(s, 1) }.Distinct())
            if (data.Race(race) is { } rm) tracks.AddRange(rm.Combat);
        if (Campaign.CampaignFlow.Active is { } am)
            tracks.AddRange(data.Mission(am.Campaign.Path, am.Mission.Index).AdditionalCombat);
        IMusicCalculator calc = CompositeMusicCalculator.Of(tracks);
        if (s.Siege is { } siege) calc = new SiegeMusicCalculator(data.Race(siege.Town)?.Siege, calc);
        _music = MusicManager.Push("combat", calc);

        var ambient = new AmbientPlayer { Name = "Ambient" };
        AddChild(ambient);
        ambient.SetAmbient(_screen.Arena?.Definition?.AmbientSoundSet);
    }

    public override void _Process(double delta)
    {
        if (_ended || !_screen.Session.Ended) return;
        _ended = true;
        var (winner, reason) = _screen.Session.EndResult!.Value;
        var s = _screen.Session.Battle.State;
        int me = _screen.Session.ViewSide;
        var rm = AudioSystem.Data.Race(SideRace(s, me));
        bool fled = reason.Contains("flee", StringComparison.OrdinalIgnoreCase) || reason.Contains("surrender", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("retreat", StringComparison.OrdinalIgnoreCase);
        var sting = winner == me ? rm?.WinCombat : fled ? rm?.RetreatCombat : rm?.LoseCombat;
        AudioSystem.Trace($"combat over: winner {winner?.ToString() ?? "none"} ({reason}) → {sting?.Path ?? "no sting"}");
        MusicManager.Pop(_music);
        _music = null;
        MusicManager.PlayOnce("combat-result", sting);
    }

    public override void _ExitTree() => MusicManager.Pop(_music);
}

/// <summary>
/// Town screen: the race's TownMusic and TownAmbientSoundSet (TableRaceMusic), with
/// TavernMusic over the tavern and DwellingMusic over the creature recruitment screen.
/// </summary>
public partial class TownAudio : Node
{
    Town.TownScreen _screen = null!;
    MusicManager.Handle? _music, _sub;
    string? _subKind, _buildingSound;
    RaceMusic? _race;
    double _poll;

    public static void Attach(Town.TownScreen screen) => screen.AddChild(new TownAudio { _screen = screen, Name = "TownAudio" });

    public override void _Ready()
    {
        AudioSystem.EnsureInstalled();
        _race = AudioSystem.Data.Race(_screen.Town.Type);
        _music = MusicManager.Push("town", new ConstantMusicCalculator(_race?.Town));
        var ambient = new AmbientPlayer { Name = "Ambient" };
        AddChild(ambient);
        ambient.SetAmbient(_race?.TownAmbientSoundSet);
    }

    public override void _Process(double delta)
    {
        if ((_poll -= delta) > 0) return;
        _poll = 0.25;
        string? kind = null, building = null;
        foreach (var n in _screen.FindChildren("*", "", recursive: true, owned: false))
        {
            if (n is Town.TavernScreen) { kind = "tavern"; break; }
            if (n is Town.TownHireScreen) { kind = "dwelling"; break; }
            building ??= n switch
            {
                Town.MageGuildScreen => _race?.MagicGuildSound,
                Town.MarketScreen => _race?.MarketplaceSound,
                Town.BlacksmithScreen => _race?.BlacksmithSound,
                Town.TownBuildScreen => _race?.TownHallSound,
                _ => null,
            };
        }
        // Opening a building's screen plays the race's building sound once (TableRaceMusic
        // MagicGuildSound / MarketplaceSound / BlacksmithSound / TownHallSound, read at 0053a6f0).
        if (building != _buildingSound)
        {
            _buildingSound = building;
            if (building != null) { AudioSystem.Trace($"town building sound {building}"); SoundLibrary.PlayUi(building); }
        }
        if (kind == _subKind) return;
        MusicManager.Pop(_sub);
        _sub = null;
        _subKind = kind;
        if (kind == "tavern") _sub = MusicManager.Push("tavern", new ConstantMusicCalculator(_race?.Tavern));
        else if (kind == "dwelling") _sub = MusicManager.Push("dwelling", new ConstantMusicCalculator(_race?.Dwelling));
    }

    public override void _ExitTree()
    {
        MusicManager.Pop(_sub);
        MusicManager.Pop(_music);
    }
}
