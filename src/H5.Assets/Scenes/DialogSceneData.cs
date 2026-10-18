using System.Globalization;
using System.Xml.Linq;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Assets.Scenes;

/// <summary>A hero or monster standing in a dialog scene (AdvMapHero / AdvMapMonster in the scene file).</summary>
public sealed class SceneActor
{
    public string Id = "";
    public bool IsHero;
    /// <summary>AdvMapHeroShared / AdvMapMonsterShared descriptor path.</summary>
    public string SharedPath = "";
    /// <summary>Map position in tiles (heroes may be fractional) and rotation in radians about +Z.</summary>
    public float X, Y, Z, Rot;
    public int Floor;
    public string Name = "";
}

/// <summary>A dialog camera: a <see cref="CameraPos"/> plus whether it is absolute or relative to the speaker.</summary>
public sealed class SceneCamera
{
    public CameraPos Pos = CameraPos.Default;
    public bool Absolute = true;
    /// <summary>Extra yaw (radians) for relative cameras.</summary>
    public float Rot;
    public string Name = "";
}

/// <summary>DSceneCameraSet: the camera moves from Start to Finish during a sentence.</summary>
public sealed class SceneCameraSet
{
    public SceneCamera Start = new(), Finish = new();
    public System.Numerics.Vector3 StartDiff, FinishDiff;
    public float StartCorrectionRot, FinishCorrectionRot;
    public bool UniformMovement, IgnoreYawDiff;
    public int Direction, Circles;
    public string Name = "";
}

public sealed record SceneSound(float Delay, string SoundHref, float Duration);
public sealed record SceneEffect(float Delay, string EffectHref, System.Numerics.Vector3 Pos, float Rot, float MovementSpeed,
    IReadOnlyList<System.Numerics.Vector2> MovePoints, bool ConstantPitch);
/// <summary>An actor animation cue: play <see cref="AnimName"/> after <see cref="Delay"/>, walking through MovePoints (tiles) if any.</summary>
public sealed record SceneAnimation(string ActorId, int AnimationIndex, float Delay, string AnimName,
    IReadOnlyList<System.Numerics.Vector2> MovePoints, float FinalAngleDeg, float MovementSpeed);

public sealed class SceneSentence
{
    public string Text = "";
    public string? VoiceHref;
    public string SpeakerId = "";
    /// <summary>MonsterCustomName text, shown instead of the speaker's own name.</summary>
    public string CustomName = "";
    public SceneCameraSet? Camera;
    public bool DynamicCamera = true, StopAmbient, StopMusic;
    /// <summary>Seconds; for voiced lines the time added after the voice.</summary>
    public float Duration, SoundDuration;
    public string? CustomAmbientLight;
    public int ActorAnimationIndex = -1;
    public string AnimName = "";
    public float AnimationDelay;
    public List<SceneSound> Sounds = new();
    public List<SceneEffect> Effects = new();
    public List<SceneAnimation> Animations = new();
}

public sealed class DialogSceneDef
{
    public string Path = "", MapPath = "";
    public string? SoundSet, Music, AmbientLight;
    public float MusicVolume = 100, CameraShiftZ, Anim3DSoundVolume = 30;
    public Dictionary<string, SceneActor> Actors = new(StringComparer.Ordinal);
    /// <summary>Actors listed in the scene's objects (present from the start), in file order.</summary>
    public List<string> PresentActors = new();
    public List<SceneSentence> Sentences = new();
}

/// <summary>
/// Loads DialogScenes/**/DialogScene.xdb: a small map (Maps/SmallSpecialArenas/…) plus a
/// list of sentences, each with subtitle text, voice, speaker, camera set and timed
/// sounds/effects/actor animations. Actors are AdvMapHero/AdvMapMonster objects declared
/// inline anywhere in the file and referenced by id (#xpointer(id(item_…)/AdvMapHero)).
/// </summary>
public static class DialogSceneLoader
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static DialogSceneDef Load(XdbLoader xdb, string href)
    {
        var path = XdbRef.Parse(href).Path;
        var root = xdb.Load(path).Root ?? throw new InvalidDataException($"{path}: empty");
        var d = new DialogSceneDef
        {
            Path = path,
            MapPath = CampaignLoader.Href(root.Element("Map"), path) ?? "",
            SoundSet = CampaignLoader.Href(root.Element("SoundSet"), path),
            Music = CampaignLoader.Href(root.Element("Music"), path),
            AmbientLight = CampaignLoader.Href(root.Element("CustomSceneAmbientLight"), path),
            MusicVolume = F(root.Element("MusicVolume"), 100),
            CameraShiftZ = F(root.Element("CameraShiftZ"), 0),
            Anim3DSoundVolume = F(root.Element("Anim3DSoundVolume"), 30),
        };

        // Every element carrying an id attribute can be referenced by #xpointer(id(...)).
        var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.Descendants())
            if ((string?)e.Attribute("id") is { Length: > 0 } id) byId[id] = e;

        SceneActor? Actor(XElement? link)
        {
            if (link == null) return null;
            XElement? holder = null;
            var href = (string?)link.Attribute("href") ?? "";
            if (href.StartsWith("#n:inline")) holder = link;
            else if (href.Contains("id(") && href.IndexOf("id(", StringComparison.Ordinal) is var i and >= 0)
            {
                var end = href.IndexOf(')', i);
                var id = href[(i + 3)..end];
                byId.TryGetValue(id, out holder);
            }
            if (holder == null) return null;
            var obj = holder.Elements().FirstOrDefault(e => e.Name.LocalName is "AdvMapHero" or "AdvMapMonster");
            var key = (string?)holder.Attribute("id") ?? "";
            if (obj == null || key.Length == 0) return null;
            if (d.Actors.TryGetValue(key, out var existing)) return existing;
            var pos = obj.Element("Pos");
            var a = new SceneActor
            {
                Id = key,
                IsHero = obj.Name.LocalName == "AdvMapHero",
                SharedPath = CampaignLoader.Href(obj.Element("Shared"), path) ?? "",
                X = F(pos?.Element("x"), 0), Y = F(pos?.Element("y"), 0), Z = F(pos?.Element("z"), 0),
                Rot = F(obj.Element("Rot"), 0),
                Floor = (int)F(obj.Element("Floor"), 0),
                Name = (string?)obj.Element("Name") ?? "",
            };
            d.Actors[key] = a;
            return a;
        }

        foreach (var item in root.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            if (Actor(item) is { } a && !d.PresentActors.Contains(a.Id)) d.PresentActors.Add(a.Id);

        foreach (var s in root.Element("sentences")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var speaker = Actor(HasLink(s.Element("heroLink")) ? s.Element("heroLink") : s.Element("monsterLink"));
            var sentence = new SceneSentence
            {
                Text = xdb.ReadText((string?)s.Element("text")?.Attribute("href"), path)?.Trim() ?? "",
                VoiceHref = CampaignLoader.Href(s.Element("sound"), path),
                SpeakerId = speaker?.Id ?? "",
                CustomName = xdb.ReadText((string?)s.Element("MonsterCustomName")?.Attribute("href"), path)?.Trim() ?? "",
                DynamicCamera = (string?)s.Element("DynamicCamera") != "false",
                StopAmbient = (string?)s.Element("StopAmbient") == "true",
                StopMusic = (string?)s.Element("StopMusic") == "true",
                Duration = F(s.Element("duration"), 0),
                SoundDuration = F(s.Element("soundDuration"), 0),
                CustomAmbientLight = CampaignLoader.Href(s.Element("CustomAmbientLight"), path),
                ActorAnimationIndex = (int)F(s.Element("ActorAnimationIndex"), -1),
                AnimName = (string?)s.Element("AnimName") ?? "",
                AnimationDelay = F(s.Element("AnimationDelay"), 0),
            };
            var camHref = CampaignLoader.Href(s.Element("NewCameraSet"), path) ?? CampaignLoader.Href(s.Element("cameraSet"), path);
            if (camHref != null && xdb.FileSystem.Exists(camHref)) sentence.Camera = LoadCameraSet(xdb, camHref);
            foreach (var cs in s.Element("CustomSounds")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                if (CampaignLoader.Href(cs.Element("sound"), path) is { } sh)
                    sentence.Sounds.Add(new SceneSound(F(cs.Element("Delay"), 0), sh, F(cs.Element("Duration"), 0)));
            foreach (var ce in s.Element("CustomEffects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                if (CampaignLoader.Href(ce.Element("Effect"), path) is { } eh)
                    sentence.Effects.Add(new SceneEffect(F(ce.Element("Delay"), 0), eh, V3(ce.Element("Pos")), F(ce.Element("Rot"), 0),
                        F(ce.Element("MovementSpeed"), 0), Points(ce.Element("MovePoints")), (string?)ce.Element("ConstantPitch") == "true"));
            foreach (var ca in s.Element("CustomAnimations")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var actor = Actor(HasLink(ca.Element("heroLink")) ? ca.Element("heroLink") : ca.Element("monsterLink"));
                if (actor == null) continue;
                sentence.Animations.Add(new SceneAnimation(actor.Id, (int)F(ca.Element("AnimationIndex"), 0), F(ca.Element("AnimationDelay"), 0),
                    (string?)ca.Element("AnimName") ?? "", Points(ca.Element("MovePoints")), F(ca.Element("FinalAngle"), 0), F(ca.Element("MovementSpeed"), 0)));
            }
            d.Sentences.Add(sentence);
        }
        return d;
    }

    static bool HasLink(XElement? e) => e != null && (e.HasElements || !string.IsNullOrEmpty((string?)e.Attribute("href")));

    public static SceneCameraSet LoadCameraSet(XdbLoader xdb, string path)
    {
        var r = xdb.Load(path).Root!;
        return new SceneCameraSet
        {
            Start = LoadCamera(xdb, CampaignLoader.Href(r.Element("StartCamera"), path)),
            Finish = LoadCamera(xdb, CampaignLoader.Href(r.Element("FinishCamera"), path)),
            StartDiff = V3(r.Element("StartCameraDiff")),
            FinishDiff = V3(r.Element("FinishCameraDiff")),
            StartCorrectionRot = F(r.Element("StartCorrectionRot"), 0),
            FinishCorrectionRot = F(r.Element("FinishCorrectionRot"), 0),
            UniformMovement = F(r.Element("UniformCameraMovement"), 0) != 0,
            IgnoreYawDiff = F(r.Element("IgnoreYawDiff"), 0) != 0,
            Direction = (int)F(r.Element("Direction"), 0),
            Circles = (int)F(r.Element("Circles"), 0),
            Name = xdb.ReadText((string?)r.Element("NameFileRef")?.Attribute("href"), path)?.Trim() ?? "",
        };
    }

    static SceneCamera LoadCamera(XdbLoader xdb, string? path)
    {
        if (path == null || !xdb.FileSystem.Exists(path)) return new SceneCamera();
        XElement r;
        try { r = xdb.Load(path).Root!; }
        catch (System.Xml.XmlException) { return new SceneCamera(); } // one stock camera file is corrupt
        return new SceneCamera
        {
            Pos = CameraPos.Read(r.Element("Pos")),
            Absolute = (string?)r.Element("Absolute") != "false",
            Rot = F(r.Element("Rot"), 0),
            Name = xdb.ReadText((string?)r.Element("NameFileRef")?.Attribute("href"), path)?.Trim() ?? "",
        };
    }

    static float F(XElement? e, float d) =>
        e != null && float.TryParse(e.Value, NumberStyles.Float, Inv, out var v) ? v : d;

    static System.Numerics.Vector3 V3(XElement? e) =>
        new(F(e?.Element("x"), 0), F(e?.Element("y"), 0), F(e?.Element("z"), 0));

    static List<System.Numerics.Vector2> Points(XElement? e) =>
        (e?.Elements("Item") ?? Enumerable.Empty<XElement>()).Select(p => new System.Numerics.Vector2(F(p.Element("x"), 0), F(p.Element("y"), 0))).ToList();

    /// <summary>All DialogScene descriptors in the archives (from the stock list when present).</summary>
    public static List<string> ListAll(PakFileSystem fs) =>
        fs.Enumerate("DialogScenes/").Where(p => p.EndsWith("DialogScene.xdb", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>A startup/outro movie list (video/intro_a1.xml): movie files with skip rules.</summary>
public sealed record MovieEntry(string FileName, bool CanSkipMovie, bool CanSkipSequence, bool SlideShowMode);

public static class MovieList
{
    /// <summary>Parses intro_a1.xml-style lists. FileName is relative to the game's bin folder ("../video/logo-nival").</summary>
    public static List<MovieEntry> Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        var list = new List<MovieEntry>();
        foreach (var item in root?.Element("Movies")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            static bool B(XElement? e) => e != null && (e.Value.Trim() == "1" || e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            var name = (string?)item.Element("FileName");
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new MovieEntry(name.Trim(), B(item.Element("CanSkipMovie")), B(item.Element("CanSkipSequence")), B(item.Element("SlideShowMode"))));
        }
        return list;
    }

    /// <summary>
    /// Resolves a movie name to an existing file in the install: the original appends ".ogg"
    /// to the name relative to bin/. Missing files (e.g. the GOG build has no "claim" movie)
    /// are skipped, like the original does when a file can't be opened.
    /// </summary>
    public static string? Resolve(GameInstall install, string fileName)
    {
        var rel = fileName.Replace('\\', '/');
        var full = Path.GetFullPath(Path.Combine(install.Root, "bin", rel));
        foreach (var cand in new[] { full, full + ".ogg", full + ".ogv" })
            if (File.Exists(cand)) return cand;
        return null;
    }
}
