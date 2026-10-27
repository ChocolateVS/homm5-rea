using System.Xml.Linq;
using Godot;
using H5.Core.Xdb;

namespace H5Reborn.Combat;

/// <summary>
/// The original combat track cameras ("cinematic camera", setvar camera_cinematic = 1 in the stock
/// profile). For each fight / cast / death event the exe searches the arena's (CamerasSet) and then
/// _(CamerasSet)/Default for the first (CombatCamera) whose event filter matches (007da700): action
/// type, actor and target filters (007da530), spell, and azimuth (007d8420). A non-forced camera
/// plays only if the anti-repeater lets it (007d9280: probability MaxCount / (count + MaxCount + 1),
/// count increments per attempt). A random track (007d8520) then plays its segments: static
/// (Camera) poses placed relative to the actor (007d99d0, eye at the camera position, rod 0), each
/// starting at a key moment + offset, the track ending at EndMoment + EndOffset (007d8b30).
/// Animated (AnimSet) cameras (3 in the stock data) and the arrow-following camera are not
/// reproduced (docs/rules/CAMERA.md).
/// </summary>
public sealed class CinematicCameras
{
    public enum Action { None, Any, Melee, Ranged, SpellCast, SpellEffect, Move, Hit, Death, ScriptAnimation }
    enum CharType { NoMatter, Absent, Any, Specific, Hero, Machine, Building, Creature, Creature1x1, Creature2x2 }
    enum PosType { Absolute, Actor, Target }
    enum RotType { Absolute, Actor, ActorPoint, Target, TargetPoint }
    enum Moment { EventBegin, EventEnd, AttackStart, AttackHit, RetaliateStart, RetaliateHit, SpellHit }

    /// <summary>A participant: Pos is the engine-space ground position, Facing the engine facing angle (radians, 0 = −Y).</summary>
    public sealed record Participant(CharTypeKind Kind, string? VisualPath, int Size, Vector2I InternalCell, Vector3 Pos, float Facing);
    public enum CharTypeKind { Hero, Machine, Building, Creature }

    sealed record Filter(CharType Type, HashSet<string> Visuals, bool ZoneUse, Rect2I Zone);
    sealed record CameraPose(Vector3 Pos, float PitchDeg, float YawDeg, float FovDeg);
    sealed record Segment(CameraPose? Camera, Moment Start, float Offset, PosType Pos, RotType Rot);
    sealed record Track(List<Segment> Segments, Moment End, float EndOffset);
    sealed record Def(string Path, Action Action, Filter Actor, Filter Target, string Spell, bool AzimuthUse, float AzMin, float AzMax,
        List<Track> Tracks, bool Forced);

    const string DefaultSet = "_(CamerasSet)/Default.(CamerasSet).xdb";
    const string AntiRepeatPath = "ArenaObjects/_(CombatCamera)/CameraAntiRepeat.xdb";

    readonly CombatCamera _camera;
    readonly List<Def> _arenaDefs, _defaultDefs;
    readonly Dictionary<string, int> _repeatMax = new();
    readonly Dictionary<string, int> _repeatCount = new();
    // Visual-only choices: the exe uses its UI random generator, so these never touch battle state.
    readonly Random _rng = new();

    /// <summary>setvar camera_cinematic (stock profile: 1). Forced cameras play even when off.</summary>
    public static bool Enabled = true;

    Active? _active;

    sealed class Active
    {
        public required Track Track;
        public required Participant Actor;
        public Participant? Target;
        public Vector3 Point;
        public double Time;
        public double? EndAt;
        public int Segment = -1;
    }

    public CinematicCameras(CombatCamera camera, string? arenaCamerasSet)
    {
        _camera = camera;
        _defaultDefs = LoadSet(DefaultSet);
        var arenaPath = XdbRef.Parse(arenaCamerasSet).Path;
        _arenaDefs = !string.IsNullOrEmpty(arenaPath) && !arenaPath.Equals(DefaultSet, StringComparison.OrdinalIgnoreCase)
            ? LoadSet(arenaPath) : new List<Def>();
        LoadAntiRepeat();
    }

    // ---- loading -------------------------------------------------------------------------------

    static List<Def> LoadSet(string path)
    {
        var list = new List<Def>();
        var fs = Game.Instance.Fs;
        if (!fs.Exists(path)) return list;
        var root = Game.Instance.Xdb.Load(path).Root;
        foreach (var item in root?.Element("CombatCameras")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var r = XdbRef.Parse((string?)item.Attribute("href"), path);
            if (r.IsEmpty || !fs.Exists(r.Path)) continue;
            try { if (LoadDef(r.Path) is { } d) list.Add(d); }
            catch (Exception e) { GD.PushWarning($"[H5] combat camera {r.Path}: {e.Message}"); }
        }
        return list;
    }

    static Def? LoadDef(string path)
    {
        var root = Game.Instance.Xdb.Load(path).Root;
        var ev = root?.Element("Event");
        if (root == null || ev == null) return null;
        var az = ev.Element("AzimuthRange");
        var tracks = new List<Track>();
        foreach (var t in root.Element("Tracks")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var segs = new List<Segment>();
            foreach (var s in t.Element("Segments")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var start = s.Element("Start");
                segs.Add(new Segment(LoadPose((string?)s.Element("Camera")?.Attribute("href"), path),
                    ParseEnum<Moment>(start?.Element("KeyMoment"), "CAMERA_MOMENT_"), F(start?.Element("Offset")),
                    ParseEnum<PosType>(s.Element("PosType"), "CAMERAPOS_"), ParseEnum<RotType>(s.Element("RotType"), "CAMERAROT_")));
            }
            if (segs.Count > 0)
                tracks.Add(new Track(segs, ParseEnum<Moment>(t.Element("EndMoment"), "CAMERA_MOMENT_"), F(t.Element("EndOffset"))));
        }
        if (tracks.Count == 0) return null;
        return new Def(path, ParseEnum<Action>(ev.Element("ActionType"), "CAMERA_ACTION_"),
            LoadFilter(ev.Element("Actor"), path), LoadFilter(ev.Element("Target"), path),
            ((string?)ev.Element("SpellID") ?? "SPELL_NONE").Trim(),
            (string?)az?.Element("Use") == "true", F(az?.Element("Min")), F(az?.Element("Max")),
            tracks, (string?)root.Element("IsForced") == "true");
    }

    static Filter LoadFilter(XElement? e, string path)
    {
        var visuals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(XElement? v)
        {
            var r = XdbRef.Parse((string?)v?.Attribute("href"), path);
            if (!r.IsEmpty) visuals.Add(r.Path);
        }
        Add(e?.Element("Creature"));
        foreach (var c in e?.Element("Creatures")?.Elements("Item") ?? Enumerable.Empty<XElement>()) Add(c);
        var zone = e?.Element("Zone");
        var area = zone?.Element("Area");
        int I(string n) => (int)F(area?.Element(n));
        return new Filter(ParseEnum<CharType>(e?.Element("Type"), "CAMERA_CHARACTER_"), visuals,
            (string?)zone?.Element("Use") == "true",
            new Rect2I(I("x1"), I("y1"), I("x2") - I("x1"), I("y2") - I("y1")));
    }

    static CameraPose? LoadPose(string? href, string from)
    {
        var r = XdbRef.Parse(href, from);
        if (r.IsEmpty || !Game.Instance.Fs.Exists(r.Path)) return null;
        var c = Game.Instance.Xdb.Load(r.Path).Root;
        if (c == null) return null;
        Vector3 V(XElement? v) => new(F(v?.Element("x")), F(v?.Element("y")), F(v?.Element("z")));
        var rot = V(c.Element("Rot"));
        return new CameraPose(V(c.Element("Pos")), rot.X, rot.Y, F(c.Element("FOV")));
    }

    void LoadAntiRepeat()
    {
        if (!Game.Instance.Fs.Exists(AntiRepeatPath)) return;
        foreach (var e in Game.Instance.Xdb.Load(AntiRepeatPath).Root?.Element("Entries")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            _repeatMax[((string?)e.Element("EventType") ?? "").Trim()] = (int)F(e.Element("MaxCount"));
    }

    static float F(XElement? e) =>
        e != null && float.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;

    static T ParseEnum<T>(XElement? e, string prefix) where T : struct, Enum
    {
        var s = ((string?)e ?? "").Trim();
        if (s.StartsWith(prefix)) s = s[prefix.Length..];
        s = s.Replace("_", "");
        return Enum.TryParse<T>(s, ignoreCase: true, out var v) ? v : default;
    }

    // ---- matching (007da700) ---------------------------------------------------------------------

    Def? Find(Action action, Participant actor, Participant? target, string? spell, Vector3 point)
    {
        // Azimuth: direction actor → point in facing degrees, minus 90 (007da700 → 007d8420).
        float az = Mathf.RadToDeg(FacingTowards(actor.Pos, point)) - 90f;
        foreach (var list in new[] { _arenaDefs, _defaultDefs })
            foreach (var d in list)
            {
                if (d.Action != Action.Any && d.Action != action) continue;
                if (!Matches(d.Actor, actor) || !Matches(d.Target, target)) continue;
                if (d.Spell != "SPELL_NONE" && d.Spell != spell) continue;
                if (d.AzimuthUse && Wrap360(az - d.AzMin) > Wrap360(d.AzMax - d.AzMin)) continue;
                return d;
            }
        return null;
    }

    static float Wrap360(float a)
    {
        a %= 360f;
        return a < 0 ? a + 360f : a;
    }

    static bool Matches(Filter f, Participant? p)
    {
        bool ok = f.Type switch
        {
            CharType.Absent => p == null,
            CharType.Any => p != null,
            CharType.Specific => p?.VisualPath != null && f.Visuals.Contains(p.VisualPath),
            CharType.Hero => p?.Kind == CharTypeKind.Hero,
            CharType.Machine => p?.Kind == CharTypeKind.Machine,
            CharType.Building => p?.Kind == CharTypeKind.Building,
            CharType.Creature => p?.Kind == CharTypeKind.Creature,
            CharType.Creature1x1 => p?.Kind == CharTypeKind.Creature && p.Size == 1,
            CharType.Creature2x2 => p?.Kind == CharTypeKind.Creature && p.Size == 2,
            _ => true,
        };
        if (!ok) return false;
        // Zone (007d89b0): the participant's internal-grid cell lies in the inclusive rectangle.
        if (!f.ZoneUse) return true;
        if (p == null) return false;
        var c = p.InternalCell;
        return c.X >= f.Zone.Position.X && c.X <= f.Zone.End.X && c.Y >= f.Zone.Position.Y && c.Y <= f.Zone.End.Y;
    }

    /// <summary>The anti-repeat category of an event (CameraAntiRepeat.xdb EventType).</summary>
    static string RepeatType(Action a, Participant actor) => a switch
    {
        // Hero attacks use anti-repeat category 6 (007d90d0 / 007d9266), whether they strike or shoot.
        Action.Melee or Action.Ranged when actor.Kind == CharTypeKind.Hero => "CAMERA_REPEAT_HERO_ABILITY",
        Action.Melee => "CAMERA_REPEAT_MELEE",
        Action.Ranged => actor.Kind == CharTypeKind.Machine ? "CAMERA_REPEAT_BALLISTA_SHOOT" : "CAMERA_REPEAT_RANGED",
        Action.SpellCast => actor.Kind == CharTypeKind.Hero ? "CAMERA_REPEAT_HERO_ABILITY" : "CAMERA_REPEAT_CAST",
        Action.Death => "CAMERA_REPEAT_CREATURE_DEATH",
        _ => "CAMERA_REPEAT_CREATURE_ABILITY",
    };

    bool AntiRepeatAllows(string type)
    {
        if (!_repeatMax.TryGetValue(type, out var max)) return true;
        int count = _repeatCount.GetValueOrDefault(type);
        bool allow = _rng.Next(count + max + 1) < max;
        _repeatCount[type] = count + 1;
        return allow;
    }

    // ---- playback ------------------------------------------------------------------------------

    /// <summary>Starts the track camera for an event, if one matches. Returns true when a cinematic began.</summary>
    public bool Begin(Action action, Participant actor, Participant? target, string? spell, Vector3? point = null)
    {
        End();
        var at = point ?? target?.Pos ?? actor.Pos;
        var d = Find(action, actor, target, spell, at);
        if (d == null) return false;
        if (!d.Forced && (!Enabled || !AntiRepeatAllows(RepeatType(action, actor)))) return false;
        var track = d.Tracks[d.Tracks.Count == 1 ? 0 : _rng.Next(d.Tracks.Count)];
        _active = new Active { Track = track, Actor = actor, Target = target, Point = at };
        if (track.End != Moment.EventEnd) _active.EndAt = MomentTime(track.End) + track.EndOffset;
        Process(0);
        return true;
    }

    /// <summary>The event finished playing: tracks that end at EVENT_END end now (+ offset).</summary>
    public void EventEnded()
    {
        if (_active == null) return;
        if (_active.Track.End == Moment.EventEnd) _active.EndAt = _active.Time + _active.Track.EndOffset;
        if (_active.EndAt is { } e && _active.Time >= e) End();
    }

    public void End()
    {
        if (_active == null) return;
        _active = null;
        _camera.EndCinematic();
        H5Reborn.Audio.AudioSystem.PlayNamed("WHOOSH_SOUND"); // back to the battle camera (0054a9e0 → 0053e980)
    }

    public bool Playing => _active != null;

    /// <summary>Begin and attack start coincide in this client's event playback; later moments aren't used by the stock data.</summary>
    static double MomentTime(Moment m) => 0;

    public void Process(double delta)
    {
        var a = _active;
        if (a == null) return;
        a.Time += delta;
        if (a.EndAt is { } end && a.Time >= end) { End(); return; }
        // The current segment is the last whose start has passed (each lasts until the next starts).
        int seg = -1;
        for (int i = 0; i < a.Track.Segments.Count; i++)
            if (a.Time >= MomentTime(a.Track.Segments[i].Start) + a.Track.Segments[i].Offset) seg = i;
        if (seg < 0 || seg == a.Segment) return;
        a.Segment = seg;
        if (Pose(a, a.Track.Segments[seg]) is { } pose) _camera.SetCinematic(pose);
    }

    /// <summary>007d99d0: camera position rotated by the RotType angle about Z, added to the PosType base.</summary>
    static CombatCamera.CinematicPose? Pose(Active a, Segment s)
    {
        if (s.Camera is not { } c) return null;
        float angle = s.Rot switch
        {
            RotType.Actor => a.Actor.Facing,
            RotType.ActorPoint => FacingTowards(a.Actor.Pos, a.Target?.Pos ?? a.Point),
            RotType.Target => a.Target?.Facing ?? 0f,
            RotType.TargetPoint => a.Target != null ? FacingTowards(a.Target.Pos, a.Actor.Pos) : 0f,
            _ => 0f,
        };
        var p = c.Pos;
        float cs = Mathf.Cos(angle), sn = Mathf.Sin(angle);
        var rotated = new Vector3(p.X * cs - p.Y * sn, p.X * sn + p.Y * cs, p.Z);
        var basePos = s.Pos switch
        {
            PosType.Actor => a.Actor.Pos,
            PosType.Target => a.Target?.Pos ?? a.Point,
            _ => Vector3.Zero,
        };
        return new CombatCamera.CinematicPose(basePos + rotated, Mathf.DegToRad(c.PitchDeg), Mathf.DegToRad(c.YawDeg) + angle, c.FovDeg);
    }

    /// <summary>007d8e40: atan2 of the direction plus π/2 — the facing angle (0 = −Y) of looking from a to b.</summary>
    public static float FacingTowards(Vector3 a, Vector3 b) => Mathf.Atan2(b.Y - a.Y, b.X - a.X) + Mathf.Pi / 2;
}
