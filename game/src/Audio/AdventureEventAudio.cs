using System.Xml.Linq;
using Godot;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Assets.Audio;
using H5.Assets.Model;
using H5Reborn.Adventure;
using H5Reborn.Render;

namespace H5Reborn.Audio;

/// <summary>
/// Adventure-map event sounds, called from the adventure screen's event playback with the
/// player who caused the event. The UIGameRoot named sounds are referenced by name at their
/// use sites in H5_Game.exe:
/// <list type="bullet">
/// <item>start of week: NEW_WEEK_NEUTRAL/GOOD/BAD_SOUND by the moon week's Type (009946e0);</item>
/// <item>level-up dialog: HERO_LEVEL_UP_SOUND, or HERO_RELEARNING_SOUND for a relearn (006a8f50);</item>
/// <item>neutrals offering to join or fleeing: HERO_JOIN_FLEE_SOUND (005a5d90);</item>
/// <item>grail found: HERO_FIND_GRAIL_MUSIC (005a5d90, 0076db30); grail building built:
/// BUILD_GRAIL_MUSIC (00615230) — both one-shot music over the current theme;</item>
/// <item>recruiting, hiring heroes, buying war machines: HIRE_CREATURES_SOUND (006d8ba0),
/// HIRE_HERO_SOUND (006350b0), BUY_WAR_MACHINE_SOUND (006ece20);</item>
/// <item>objectives: OBJECTIVE_ACTIVE/COMPLETED/FAILED_SOUND with the objective notice (00c5fec0).</item>
/// </list>
/// Visiting an object plays its descriptor's SoundEffect (the STABLES_VISIT_SOUND entry is the
/// same Stabels sound the Stables descriptor names); a finished town building plays the race's
/// UpgradeTownBuildingSound (TableRaceMusic, read at 0053a6f0).
/// </summary>
public static class AdventureEventAudio
{
    static Dictionary<string, string>? _weekTypes;

    public static void OnEvent(AdventureScreen screen, AdvEvent ev, int player)
    {
        var session = screen.Session;
        bool human = player == session.Human.Index;
        switch (ev)
        {
            case LevelUpOffered when human:
                AudioSystem.PlayNamed("HERO_LEVEL_UP_SOUND");
                break;
            case WeekStarted ws when session.IsHumanTurn && session.S.Players.Count > 0:
                AudioSystem.PlayNamed(WeekType(ws.MoonWeek) switch
                {
                    "WEEKTYPE_GOOD" => "NEW_WEEK_GOOD_SOUND",
                    "WEEKTYPE_BAD" => "NEW_WEEK_BAD_SOUND",
                    _ => "NEW_WEEK_NEUTRAL_SOUND",
                });
                break;
            case ChoiceOffered co when human && co.Kind.StartsWith("join", StringComparison.Ordinal):
                AudioSystem.PlayNamed("HERO_JOIN_FLEE_SOUND");
                break;
            case ObjectVisited { What: "monster_fled" } when human:
                AudioSystem.PlayNamed("HERO_JOIN_FLEE_SOUND");
                break;
            case ObjectVisited ov when human && ov.Object >= 0 && ov.Object < session.Map.Objects.Count:
                var o = session.Map.Objects[ov.Object];
                if (MapSoundEmitters.SharedSound(session, o.SharedPath, "SoundEffect") is { } fx)
                    MapSoundEmitters.PlayAt(screen, fx, o.Pos);
                else if (o.Shared.Type == "BUILDING_STABLES")
                    AudioSystem.PlayNamed("STABLES_VISIT_SOUND");
                break;
            case ObjectRemoved or_:
                MapSoundEmitters.Current?.Remove(or_.Object);
                break;
            case BuildingBuilt bb when human:
                if (bb.Building == "TB_GRAIL") AudioSystem.PlayNamedMusicOnce("BUILD_GRAIL_MUSIC");
                else if (session.S.Towns.Find(t => t.Object == bb.TownObject) is { } town
                         && AudioSystem.Data.Race(town.Type)?.UpgradeTownBuildingSound is { } up)
                    SoundLibrary.PlayUi(up);
                break;
            case ArtifactGained ag when human && AdventureSession.SharedDb.ArtifactClass.GetValueOrDefault(ag.Artifact) == "ARTF_CLASS_GRAIL":
                AudioSystem.PlayNamedMusicOnce("HERO_FIND_GRAIL_MUSIC");
                break;
            case CreaturesRecruited when human:
                AudioSystem.PlayNamed("HIRE_CREATURES_SOUND");
                break;
            case HeroHired hh when human && hh.TownObject >= 0:
                AudioSystem.PlayNamed("HIRE_HERO_SOUND");
                break;
        }
    }

    /// <summary>A command the local player issued was accepted (sounds without a dedicated event).</summary>
    public static void OnHumanCommand(AdvCommand cmd)
    {
        if (cmd is BuyWarMachineCmd) AudioSystem.PlayNamed("BUY_WAR_MACHINE_SOUND");
    }

    /// <summary>Objective state 2/3/4 → active/completed/failed sound (00c5fec0).</summary>
    public static void ObjectiveState(int state)
    {
        switch (state)
        {
            case 2: AudioSystem.PlayNamed("OBJECTIVE_ACTIVE_SOUND"); break;
            case 3: AudioSystem.PlayNamed("OBJECTIVE_COMPLETED_SOUND"); break;
            case 4: AudioSystem.PlayNamed("OBJECTIVE_FAILED_SOUND"); break;
        }
    }

    /// <summary>GameMechanics/RefTables/MoonWeeks.xdb Type of a moon week (WEEKTYPE_NEUTRAL/GOOD/BAD).</summary>
    public static string WeekType(string moonWeek)
    {
        if (_weekTypes == null)
        {
            _weekTypes = new(StringComparer.OrdinalIgnoreCase);
            const string path = "GameMechanics/RefTables/MoonWeeks.xdb";
            if (Game.Instance.Fs.Exists(path))
                foreach (var item in Game.Instance.Xdb.Load(path).Root?.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                    if (item.Element("ID")?.Value is { } id)
                        _weekTypes[id] = item.Element("obj")?.Element("Type")?.Value ?? "WEEKTYPE_NEUTRAL";
        }
        return _weekTypes.GetValueOrDefault(moonWeek, "WEEKTYPE_NEUTRAL");
    }
}

/// <summary>
/// Positional sounds on the adventure map:
/// <list type="bullet">
/// <item>objects whose descriptor names an AdventureSoundEffect (mines, waterwheels, arenas,
/// monoliths…): a looping 3D sound at the object, audible on its own floor only;</item>
/// <item>heroes walking: the hero's AdvMapAnimSet "move" clip Sound (e.g. Heroes/DemonLord/Move,
/// a looping 3D sound) runs while the move clip plays.</item>
/// </list>
/// 3D sounds follow FMOD's model: full volume within MinDistance, then Min/distance.
/// </summary>
public partial class MapSoundEmitters : Node3D
{
    public static MapSoundEmitters? Current { get; private set; }

    static readonly Dictionary<string, string?> SharedCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string?> MoveSoundCache = new(StringComparer.OrdinalIgnoreCase);

    readonly AdventureScreen _screen;
    readonly Dictionary<int, (Node3D Anchor, int Floor)> _objects = new();
    readonly Dictionary<int, Node> _heroLoops = new();
    int _floor = -1;

    public MapSoundEmitters(AdventureScreen screen)
    {
        _screen = screen;
        Name = "MapSounds";
    }

    /// <summary>A sound href named by a field of an object's shared descriptor (cached).</summary>
    public static string? SharedSound(AdventureSession session, string sharedPath, string field)
    {
        if (string.IsNullOrEmpty(sharedPath)) return null;
        var key = sharedPath + "|" + field;
        if (SharedCache.TryGetValue(key, out var cached)) return cached;
        string? href = null;
        try
        {
            var files = session.Files;
            if (files.Exists(sharedPath) && files.LoadXml(sharedPath).Root?.Element(field)?.Attribute("href")?.Value is { Length: > 0 } h)
                href = H5.Core.Xdb.XdbRef.Parse(h, sharedPath).Path;
        }
        catch (Exception e) { GD.PushWarning($"[H5][audio] {sharedPath}: {e.Message}"); }
        return SharedCache[key] = href;
    }

    /// <summary>Plays a (possibly 3D) one-shot at a map tile.</summary>
    public static void PlayAt(AdventureScreen screen, string soundHref, Tile at)
    {
        var anchor = new Node3D { Name = "SoundAt" };
        screen.World.AddChild(anchor);
        anchor.GlobalPosition = screen.World.TileCenter(at);
        var p = SoundLibrary.Play(soundHref, anchor);
        if (p == null) { anchor.QueueFree(); return; }
        AudioSystem.Trace($"object sound {soundHref} at {at}");
        p.TreeExited += () => { if (IsInstanceValid(anchor)) anchor.QueueFree(); };
    }

    public override void _Ready()
    {
        Current = this;
        var session = _screen.Session;
        foreach (var o in session.Map.Objects)
        {
            if (SharedSound(session, o.SharedPath, "AdventureSoundEffect") is not { } href) continue;
            var anchor = new Node3D { Name = $"Obj{o.Index}" };
            AddChild(anchor);
            anchor.Position = _screen.World.TileCenter(o.Pos);
            if (SoundLibrary.Play(href, anchor) != null) _objects[o.Index] = (anchor, o.Pos.Floor);
            else anchor.QueueFree();
        }
        AudioSystem.Trace($"map sounds: {_objects.Count} positional object sounds");
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }

    public void Remove(int objectIndex)
    {
        if (_objects.Remove(objectIndex, out var e)) e.Anchor.QueueFree();
    }

    public override void _Process(double delta)
    {
        var world = _screen.World;
        if (world.Floor != _floor)
        {
            _floor = world.Floor;
            foreach (var (anchor, floor) in _objects.Values)
                foreach (var c in anchor.GetChildren())
                    if (c is AudioStreamPlayer3D p) p.StreamPaused = floor != _floor;
        }
        UpdateHeroLoops();
    }

    void UpdateHeroLoops()
    {
        foreach (var h in _screen.Session.S.Heroes)
        {
            var node = _screen.World.HeroNode(h.Id);
            bool moving = node != null && node.CurrentKind == "move" && node.Player.IsPlaying() && h.Pos.Floor == _screen.World.Floor;
            _heroLoops.TryGetValue(h.Id, out var loop);
            if (loop != null && !IsInstanceValid(loop)) { _heroLoops.Remove(h.Id); loop = null; }
            if (moving && loop == null && MoveSound(node!) is { } href && SoundLibrary.Play(href, node!) is { } p)
            {
                _heroLoops[h.Id] = p;
                AudioSystem.Trace($"hero {h.Id} move sound {href}");
            }
            else if (!moving && loop != null)
            {
                loop.QueueFree();
                _heroLoops.Remove(h.Id);
            }
        }
    }

    /// <summary>The (BasicSkelAnim) Sound of the character's "move" clip.</summary>
    static string? MoveSound(Character c)
    {
        if (c.AnimSetPath is not { } set) return null;
        if (MoveSoundCache.TryGetValue(set, out var cached)) return cached;
        string? href = null;
        try
        {
            foreach (var e in new AnimSetLoader(Game.Instance.Xdb).ReadAnimSet(set))
                if (e.Kind == "move") { href = e.SoundHref; break; }
        }
        catch (Exception ex) { GD.PushWarning($"[H5][audio] anim set {set}: {ex.Message}"); }
        return MoveSoundCache[set] = href;
    }
}

/// <summary>The campaign's WinMissionMusic / LoseMissionMusic over the mission results window.</summary>
public partial class MissionResultsAudio : Node
{
    string _campaign = "";
    int _mission;
    bool _won;
    MusicManager.Handle? _handle;

    public static void Attach(Node host, string campaignPath, int mission, bool won) =>
        host.AddChild(new MissionResultsAudio { _campaign = campaignPath, _mission = mission, _won = won, Name = "MissionResultsAudio" });

    public override void _Ready()
    {
        var mm = AudioSystem.Data.Mission(_campaign, Math.Max(0, _mission));
        _handle = MusicManager.PlayOnce(_won ? "mission-won" : "mission-lost", _won ? mm.WinMission : mm.LoseMission);
    }

    public override void _ExitTree() => MusicManager.Pop(_handle);
}
