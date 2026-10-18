using System.Globalization;
using System.Xml.Linq;
using Godot;
using H5.Assets.Model;
using H5.Combat;
using H5.Core.Xdb;
using H5Reborn.Render;
using H5Reborn.Ui;

namespace H5Reborn.Combat;

/// <summary>
/// The original 3D spellbook, driven by UI/SpellBook/SpellBook.(SpellBookDesc).xdb
/// (or UI/OrcsSpellBook for warcry heroes): the animated book model with its anim set
/// (idle00, page01/page02 page turns), slot parts that receive a rendered "outliner"
/// texture per spell (icon in SpellIcon, name and cost at SpellTextPosition in the
/// UI/SpellBook texts SpellInBookCanCast / CanNotCast), school bookmarks, combat /
/// adventure tabs, page arrows, close part and the mana bookmark (SpellBookMark).
/// The battlefield behind fades to BackFadeValue over BackFadeDuration.
/// </summary>
public partial class SpellBook3D : Control
{
    public const string StandardDesc = "UI/SpellBook/SpellBook.(SpellBookDesc).xdb";
    public const string OrcDesc = "UI/OrcsSpellBook/OrcsSpellBook.(SpellBookDesc).xdb";
    const string Texts = "UI/SpellBook/SpellBookTexts.(WindowRelatedTexts).xdb";

    // Bookmark order in PartSpellSchools (by bookmark art: fire, sun, purple, green, helmet).
    static readonly string[] SchoolOrder =
        { "MAGIC_SCHOOL_DESTRUCTIVE", "MAGIC_SCHOOL_LIGHT", "MAGIC_SCHOOL_DARK", "MAGIC_SCHOOL_SUMMONING", "MAGIC_SCHOOL_RUNIC" };

    public event Action<SpellOption>? Chosen;
    public event Action? Closed;

    sealed record Desc(string Model, string AnimSet, string? Light, int[] First, int[] Second, int[] Schools,
        int Combat, int Adventure, int SP, int Close, int Next, int Prev, Vector2I Outliner, Rect2 IconRect, Vector2 TextPos,
        Color Highlight, float OverBright, Color SchoolHighlight, float BackFadeDuration, float BackFadeValue);

    sealed class Entry
    {
        public required string Spell;
        public SpellOption? Option;   // null when the hero can't cast it now
        public int Cost;
    }

    readonly Desc _d;
    readonly ModelAsset _asset;
    readonly List<Entry> _all;
    readonly int _mana, _maxMana;
    readonly bool _orcs;
    /// <summary>Adventure-map book: shows the MAGIC_SCHOOL_ADVENTURE spells under the adventure tab.</summary>
    readonly bool _adventure;
    Character _book = null!;
    MeshInstance3D? _mesh;
    Camera3D _cam = null!;
    SubViewport _vp = null!;
    string _school;
    int _page;
    bool _turning;
    readonly Dictionary<int, (Entry? E, StandardMaterial3D Mat)> _slots = new();
    int? _hoverPart;

    public SpellBook3D(IReadOnlyCollection<string> knownSpells, IReadOnlyList<SpellOption> castable, int mana, int maxMana, bool warcries, bool adventure = false)
    {
        Name = "SpellBook3D";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
        _orcs = warcries;
        _adventure = adventure;
        _d = LoadDesc(warcries ? OrcDesc : StandardDesc);
        _asset = new ModelLoader(Game.Instance.Xdb).Load(_d.Model);
        _mana = mana;
        _maxMana = maxMana;
        var byId = castable.ToDictionary(o => o.Spell);
        _all = knownSpells.Distinct()
            .Select(id => (id, def: Game.Instance.Db.Spell(id)))
            .Where(x => x.def != null && (adventure ? x.def.School == "MAGIC_SCHOOL_ADVENTURE"
                        : x.def.School is not ("MAGIC_SCHOOL_ADVENTURE" or "MAGIC_SCHOOL_NONE" or "MAGIC_SCHOOL_SPECIAL")
                          && (warcries ? x.def.School == "MAGIC_SCHOOL_WARCRIES" : x.def.School != "MAGIC_SCHOOL_WARCRIES")))
            .OrderBy(x => x.def!.Level).ThenBy(x => x.def!.Name, StringComparer.Ordinal)
            .Select(x => new Entry { Spell = x.id, Option = byId.GetValueOrDefault(x.id), Cost = byId.GetValueOrDefault(x.id)?.ManaCost ?? x.def!.ManaCost })
            .ToList();
        _school = warcries ? "MAGIC_SCHOOL_WARCRIES" : SchoolOrder.FirstOrDefault(s => _all.Any(e => SchoolOf(e) == s)) ?? SchoolOrder[0];
    }

    static string SchoolOf(Entry e) => Game.Instance.Db.Spell(e.Spell)?.School ?? "";

    static Desc LoadDesc(string path)
    {
        var r = Game.Instance.Xdb.Load(path).Root!;
        string H(string n) => XdbRef.Parse((string?)r.Element(n)?.Attribute("href"), path).Path;
        int[] L(string n) => r.Element(n)?.Elements("Item").Select(i => int.Parse(i.Value, CultureInfo.InvariantCulture)).ToArray() ?? Array.Empty<int>();
        int I(string n) => int.TryParse(r.Element(n)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
        float F(XElement? e) => e == null ? 0 : float.Parse(e.Value, CultureInfo.InvariantCulture);
        Color C(string n) => new(F(r.Element(n)?.Element("x")), F(r.Element(n)?.Element("y")), F(r.Element(n)?.Element("z")));
        var icon = r.Element("SpellIcon");
        return new Desc(H("Model"), H("AnimSet"), H("Light"), L("PartSpellsFirstPage"), L("PartSpellsSecondPage"), L("PartSpellSchools"),
            I("PartSpellsCombat"), I("PartSpellsAdventure"), I("PartSP"), I("PartClose"), I("PartNextPage"), I("PartPrevPage"),
            new Vector2I((int)F(r.Element("OutlinerSize")?.Element("x")), (int)F(r.Element("OutlinerSize")?.Element("y"))),
            new Rect2(F(icon?.Element("x1")), F(icon?.Element("y1")), F(icon?.Element("x2")) - F(icon?.Element("x1")), F(icon?.Element("y2")) - F(icon?.Element("y1"))),
            new Vector2(F(r.Element("SpellTextPosition")?.Element("x")), F(r.Element("SpellTextPosition")?.Element("y"))),
            C("SpellHighlightMaterial"), F(r.Element("SpellHighlightMaterialOverBright")), C("SchoolHighlightColor"),
            F(r.Element("BackFadeDuration")), F(r.Element("BackFadeValue")));
    }

    static string? Text(string name)
    {
        var root = Game.Instance.Xdb.Load(Texts).Root!;
        var item = root.Descendants("Item").FirstOrDefault(i => (string?)i.Element("TextName") == name);
        return item == null ? null : Game.Instance.Xdb.ReadText((string?)item.Element("text")?.Attribute("href"), Texts);
    }

    public override void _Ready()
    {
        // Battlefield fade (BackFadeValue is the remaining brightness).
        var fade = new ColorRect { Color = new Color(0, 0, 0, 0), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(fade);
        CreateTween().TweenProperty(fade, "color:a", 1f - _d.BackFadeValue, _d.BackFadeDuration);

        var container = new SubViewportContainer { AnchorRight = 1, AnchorBottom = 1, Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
        _vp = new SubViewport { OwnWorld3D = true, TransparentBg = true, HandleInputLocally = false };
        container.AddChild(_vp);
        AddChild(container);

        var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.ClearColor, AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.55f, 0.55f) };
        var sun = new DirectionalLight3D { LightEnergy = 0.9f, ShadowEnabled = false };
        if (_d.Light is { Length: > 0 } lp && Game.Instance.Fs.Exists(lp))
        {
            var l = Game.Instance.Xdb.Load(lp).Root!;
            Color V(string n, Color def) => l.Element(n) is { } e
                ? new Color(float.Parse(e.Element("x")!.Value, CultureInfo.InvariantCulture), float.Parse(e.Element("y")!.Value, CultureInfo.InvariantCulture), float.Parse(e.Element("z")!.Value, CultureInfo.InvariantCulture))
                : def;
            env.AmbientLightColor = V("AmbientColor", env.AmbientLightColor);
            sun.LightColor = V("LightColor", Colors.White);
        }
        _vp.AddChild(new WorldEnvironment { Environment = env });
        _vp.AddChild(sun);
        sun.RotationDegrees = new Vector3(-70, 20, 0);

        // The book is drawn in its own viewport over a live battle/map, so it keeps Godot-lit
        // materials instead of publishing its light to the shared H5Lit globals.
        bool original = ModelBuilder.UseOriginalLighting;
        ModelBuilder.UseOriginalLighting = false;
        try { _book = CharacterBuilder.BuildFromModel(_d.Model, _d.AnimSet, "SpellBook"); }
        finally { ModelBuilder.UseOriginalLighting = original; }
        _vp.AddChild(_book);
        _mesh = _book.FindChild("Mesh", recursive: true, owned: false) as MeshInstance3D;
        if (_book.Has("idle00")) _book.Play("idle00", loop: true);

        // Top-down camera framing the book (model Z-up → Godot Y-up; page top is model +Y = Godot −Z).
        var (min, max) = Bounds(Enumerable.Range(0, _asset.Mesh.Parts.Count));
        var center = (min + max) / 2;
        _cam = new Camera3D { Fov = 40, Near = 0.5f, Far = 200f, KeepAspect = Camera3D.KeepAspectEnum.Width };
        _vp.AddChild(_cam);
        float width = (max.X - min.X) * 1.06f;
        float dist = width / 2 / (Mathf.Tan(Mathf.DegToRad(_cam.Fov) / 2));
        _cam.LookAtFromPosition(center + new Vector3(0, dist, 0), center, new Vector3(0, 0, -1));

        BuildSlots();
        RefreshPage();
        if (Game.Instance.Fs.Exists("Sounds/_(Sound)/Interface/spellbook.xdb")) Audio.SoundLibrary.Play("Sounds/_(Sound)/Interface/spellbook.xdb", this);
    }

    (Vector3 Min, Vector3 Max) Bounds(IEnumerable<int> parts)
    {
        var min = new Vector3(1e9f, 1e9f, 1e9f);
        var max = -min;
        foreach (var i in parts)
        {
            if (i < 0 || i >= _asset.Mesh.Parts.Count) continue;
            foreach (var p in _asset.Mesh.Parts[i].Positions)
            {
                var g = ModelBuilder.ToGodot(p);
                min = min.Min(g);
                max = max.Max(g);
            }
        }
        return (min, max);
    }

    // ---------------------------------------------------------------- slot textures

    void BuildSlots()
    {
        if (_mesh?.Mesh is not ArrayMesh mesh) return;
        foreach (var part in _d.First.Concat(_d.Second).Append(_d.SP))
        {
            if (part < 0 || part >= mesh.GetSurfaceCount()) continue;
            var mat = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Roughness = 1,
            };
            _mesh.SetSurfaceOverrideMaterial(part, mat);
            _slots[part] = (null, mat);
        }
    }

    /// <summary>Renders an outliner texture: icon + markup text, transparent elsewhere.</summary>
    Texture2D Outline(Texture2D? icon, string? markup, bool centerText = false)
    {
        var vp = new SubViewport { Size = _d.Outliner, TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Once, Disable3D = true };
        var root = new Control { Size = _d.Outliner };
        vp.AddChild(root);
        if (icon != null)
            root.AddChild(new TextureRect { Texture = icon, Position = _d.IconRect.Position, Size = _d.IconRect.Size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale });
        if (!string.IsNullOrEmpty(markup))
        {
            // Spell slots put the text at SpellTextPosition; the mana bookmark centres it.
            var text = centerText
                ? new BitmapText { Markup = markup, Position = Vector2.Zero, Size = _d.Outliner, VerticalAlign = H5.Core.Ui.UiAlign.Center }
                : new BitmapText { Markup = markup, Position = _d.TextPos - new Vector2(0, 8), Size = new Vector2(_d.Outliner.X - _d.TextPos.X, _d.Outliner.Y - _d.TextPos.Y + 8) };
            root.AddChild(text);
        }
        AddChild(vp);
        return vp.GetTexture();
    }

    List<Entry> SchoolSpells() => _all.Where(e => _orcs || _adventure || SchoolOf(e) == _school).ToList();

    int PageCount => Math.Max(1, (SchoolSpells().Count + 5) / 6);

    int[] SetFor(int page) => page % 2 == 0 ? _d.First : _d.Second;

    void FillSet(int[] set, int page)
    {
        var spells = SchoolSpells().Skip(page * 6).Take(6).ToList();
        for (int i = 0; i < set.Length; i++)
        {
            if (!_slots.TryGetValue(set[i], out var slot)) continue;
            Entry? e = i < spells.Count ? spells[i] : null;
            Texture2D? tex = null;
            if (e != null)
            {
                var def = Game.Instance.Db.Spell(e.Spell)!;
                var iconHref = (string?)Game.Instance.Xdb.Load(def.SourcePath).Root?.Element("Texture")?.Attribute("href");
                var icon = iconHref != null ? UiResources.Instance.Texture(XdbRef.Parse(iconHref, def.SourcePath).Path) : null;
                var cost = (Text("mana") ?? "Mana: <value=cost>").Replace("<value=cost>", e.Cost.ToString());
                var markup = (Text(e.Option != null ? "SpellInBookCanCast" : "SpellInBookCanNotCast") ?? "<center><value=spell_name><br><value=spell_cost>")
                    .Replace("<value=spell_name>", CombatLogText.Escape(def.Name)).Replace("<value=spell_cost>", cost);
                tex = Outline(icon, markup);
            }
            slot.Mat.AlbedoTexture = tex;
            slot.Mat.AlbedoColor = tex == null ? new Color(1, 1, 1, 0) : Colors.White;
            _slots[set[i]] = (e, slot.Mat);
        }
    }

    void RefreshPage()
    {
        FillSet(SetFor(_page), _page);
        if (_slots.TryGetValue(_d.SP, out var sp))
        {
            var markup = (Text(_orcs ? "OrcSpellBookMark" : "SpellBookMark") is { Length: > 0 } t ? t : Text("SpellBookMark") ?? "")
                .Replace("<value=sp_current>", _mana.ToString()).Replace("<value=sp_maximum>", _maxMana.ToString());
            sp.Mat.AlbedoTexture = Outline(null, markup, centerText: true);
        }
        SetPartVisible(_d.Next, _page + 1 < PageCount);
        SetPartVisible(_d.Prev, _page > 0);
        // Selected school bookmark glows with SchoolHighlightColor.
        for (int i = 0; i < _d.Schools.Length && i < SchoolOrder.Length; i++)
            HighlightPart(_d.Schools[i], !_adventure && SchoolOrder[i] == _school ? _d.SchoolHighlight : null);
        HighlightPart(_adventure ? _d.Adventure : _d.Combat, _d.SchoolHighlight);
    }

    readonly Dictionary<int, Material?> _baseMaterials = new();

    void SetPartVisible(int part, bool visible)
    {
        if (_mesh?.Mesh is not ArrayMesh mesh || part < 0 || part >= mesh.GetSurfaceCount()) return;
        if (visible) { if (_baseMaterials.Remove(part, out var m)) _mesh.SetSurfaceOverrideMaterial(part, m); }
        else
        {
            _baseMaterials.TryAdd(part, _mesh.GetSurfaceOverrideMaterial(part));
            _mesh.SetSurfaceOverrideMaterial(part, new StandardMaterial3D { Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0, 0, 0, 0) });
        }
    }

    void HighlightPart(int part, Color? glow)
    {
        if (_mesh?.Mesh is not ArrayMesh mesh || part < 0 || part >= mesh.GetSurfaceCount()) return;
        if (_slots.TryGetValue(part, out var slot))
        {
            slot.Mat.EmissionEnabled = glow != null;
            if (glow is { } g) slot.Mat.Emission = g;
            return;
        }
        if (mesh.SurfaceGetMaterial(part) is not StandardMaterial3D baseMat) return;
        var m = (StandardMaterial3D)baseMat.Duplicate();
        m.EmissionEnabled = glow != null;
        if (glow is { } c) { m.Emission = c; m.EmissionEnergyMultiplier = 0.12f; }
        _mesh.SetSurfaceOverrideMaterial(part, m);
    }

    // ---------------------------------------------------------------- input

    /// <summary>Screen rectangle of a part's flat, resting position (bind pose, or the page's resting side).</summary>
    Rect2? ScreenRect(int part)
    {
        int index = Array.IndexOf(SetFor(_page), part);
        // Turning-page slots rest over the book's right/left page: use those parts' rectangles.
        IEnumerable<int> geometry = index >= 0
            ? new[] { index < 3 ? _d.First[index] : _d.Second[index] }
            : new[] { part };
        var (min, max) = Bounds(geometry);
        if (min.X > max.X) return null;
        Rect2? r = null;
        foreach (var x in new[] { min.X, max.X })
        foreach (var z in new[] { min.Z, max.Z })
        {
            var s = _cam.UnprojectPosition(new Vector3(x, max.Y, z));
            r = r is { } rr ? rr.Expand(s) : new Rect2(s, Vector2.Zero);
        }
        return r;
    }

    int? PartAt(Vector2 pos, IEnumerable<int> parts) =>
        parts.Where(p => p >= 0).Cast<int?>().FirstOrDefault(p => ScreenRect(p!.Value) is { } r && r.HasPoint(pos));

    public override void _GuiInput(InputEvent e)
    {
        if (_turning) return;
        if (e is InputEventMouseMotion mm)
        {
            var hover = PartAt(mm.Position, SetFor(_page));
            if (hover != _hoverPart)
            {
                if (_hoverPart is int old && _slots.TryGetValue(old, out var o)) o.Mat.EmissionEnabled = false;
                _hoverPart = hover;
                if (hover is int h && _slots.TryGetValue(h, out var s) && s.E?.Option != null)
                {
                    s.Mat.EmissionEnabled = true;
                    s.Mat.Emission = _d.Highlight;
                    s.Mat.EmissionEnergyMultiplier = _d.OverBright * 0.15f;
                }
            }
            return;
        }
        if (e is not InputEventMouseButton { Pressed: true } mb) return;
        AcceptEvent();
        if (mb.ButtonIndex == MouseButton.Right) { Close(); return; }
        if (mb.ButtonIndex != MouseButton.Left) return;
        var pos = mb.Position;
        if (PartAt(pos, new[] { _d.Close }) != null) { Close(); return; }
        if (PartAt(pos, new[] { _d.Next }) != null && _page + 1 < PageCount) { _ = Turn(+1); return; }
        if (PartAt(pos, new[] { _d.Prev }) != null && _page > 0) { _ = Turn(-1); return; }
        if (PartAt(pos, _d.Schools) is int tab)
        {
            int i = Array.IndexOf(_d.Schools, tab);
            if (i >= 0 && i < SchoolOrder.Length && SchoolOrder[i] != _school)
            {
                _school = SchoolOrder[i];
                if (_page % 2 == 1) { _page = 0; _ = Turn(-1, toPage: 0); }
                else { _page = 0; RefreshPage(); }
            }
            return;
        }
        if (PartAt(pos, SetFor(_page)) is int slotPart && _slots.TryGetValue(slotPart, out var slot) && slot.E?.Option is { } option)
        {
            Chosen?.Invoke(option);
            Close();
        }
    }

    async Task Turn(int dir, int? toPage = null)
    {
        _turning = true;
        int target = toPage ?? _page + dir;
        // The page being revealed is filled before the turn so it shows during the animation.
        FillSet(SetFor(target), target);
        var kind = dir > 0 ? "page01" : "page02";
        if (_book.Has(kind))
        {
            _book.Play(kind);
            await ToSignal(_book.Player, AnimationMixer.SignalName.AnimationFinished);
        }
        _page = target;
        RefreshPage();
        if (_book.Has("idle00")) _book.Play("idle00", loop: true);
        _turning = false;
    }

    public void Close()
    {
        Closed?.Invoke();
        QueueFree();
    }
}
