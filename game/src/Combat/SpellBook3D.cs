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
    Character _book = null!;
    MeshInstance3D? _mesh;
    Camera3D _cam = null!;
    SubViewport _vp = null!;
    /// <summary>The bookmark index (+0x19c): 0 Combat tab, 1 Adventure tab, 2..6 PartSpellSchools[0..4] (006867c0).</summary>
    int _bookmark;
    int _page;
    bool _turning;
    readonly Dictionary<int, (Entry? E, StandardMaterial3D Mat)> _slots = new();
    int? _hoverPart;

    /// <summary>
    /// The book state a hero keeps between openings (006867c0 restores the bookmark +0x19c and the per-bookmark first-spell
    /// indices +0x190 from the hero, vfunc 0x238; on close 00683040 stores them back through a command, 0067fc80, when
    /// +0x244 is set; every caller sets it). Ours keeps it for the session, keyed by the caster.
    /// </summary>
    static readonly Dictionary<string, (int Bookmark, int First)> SavedState = new();
    readonly string? _casterKey;
    /// <summary>+0x246: the reversed level order, toggled by clicking the current bookmark again (00685f60).</summary>
    bool _reverse;

    public SpellBook3D(IReadOnlyCollection<string> knownSpells, IReadOnlyList<SpellOption> castable, int mana, int maxMana, bool warcries, bool adventure = false, string? casterKey = null, bool heroCaster = true)
    {
        _casterKey = casterKey;
        Name = "SpellBook3D";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
        _orcs = warcries;
        _d = LoadDesc(warcries ? OrcDesc : StandardDesc);
        _asset = new ModelLoader(Game.Instance.Xdb).Load(_d.Model);
        _mana = mana;
        _maxMana = maxMana;
        var byId = castable.ToDictionary(o => o.Spell);
        // The caster's whole spell list (hero vfunc 0xcc / creature vfunc 0x248); each bookmark filters it (00684830,
        // orc book 0067cd10) and sorts it (00689140).
        _all = knownSpells.Distinct()
            .Select(id => (id, def: Game.Instance.Db.Spell(id)))
            .Where(x => x.def != null)
            .Select(x => new Entry { Spell = x.id, Option = byId.GetValueOrDefault(x.id), Cost = byId.GetValueOrDefault(x.id)?.ManaCost ?? x.def!.ManaCost })
            .ToList();
        if (_orcs)
        {
            // Orc book ctor 0067e940: bookmark +0x190 = (adventure flag == 0), i.e. warcries (1) in combat, adventure
            // spells (0) on the map; not restored from the hero.
            _bookmark = adventure ? 0 : 1;
        }
        else if (!heroCaster)
        {
            // 006867c0: for the non-hero casters (the fourth and fifth caster casts, 0067f5b0 / 006878e0) the bookmark
            // is 0 (Combat tab) and every first-spell index 0.
            _bookmark = 0;
        }
        else if (casterKey != null && SavedState.TryGetValue(casterKey, out var saved))
        {
            // 006867c0: the hero's saved bookmark; the adventure flag forces the Adventure tab (1), otherwise a saved
            // Adventure tab becomes the Combat tab (0).
            _bookmark = adventure ? 1 : saved.Bookmark == 1 ? 0 : saved.Bookmark;
            _page = _bookmark == saved.Bookmark ? saved.First / 6 : 0;
            if (_page * 6 >= SchoolSpells().Count) _page = 0;
        }
        else
        {
            // GAP: the value a hero holds before its first opening is not traced: the adventure tab on the map, else the
            // first school bookmark holding a spell.
            _bookmark = adventure ? 1 : 2 + Math.Max(0, Array.FindIndex(SchoolOrder, sc => _all.Any(e => SchoolOf(e) == sc)));
        }
    }

    /// <summary>
    /// The spell sort (00689280 with comparator 00688d50): a merge over residue classes (strided sorted runs, the stride
    /// halved from the largest power of two below n down to 1, 00688e50), reproduced as is because the comparator is not a
    /// strict order. a before b: different schools by the table 00f38a94 (destructive 0, light 2, dark 1, summoning 3,
    /// adventure 4, special 7, runic 5, warcries 6, none 8); same school: by the level of the base spell (mass spells
    /// 00a33e20 and empowered spells 00a33f70 map to their base), lower first, higher first when <paramref name="reverse"/>
    /// (+0x246), then by the base's SpellID; same base: the base spell before its variant.
    /// </summary>
    static List<Entry> ExeSort(List<Entry> list, bool reverse)
    {
        int n = list.Count;
        if (n < 2) return list;
        var src = list.ToArray();
        var dst = new Entry[n];
        int w = 0x40000000;
        while (n <= w) w >>= 1;
        for (; w != 0; w >>= 1)
        {
            if (n < w * 2) Array.Copy(src, n - w, dst, n - w, w);
            int pairs = Math.Min(w, n - w);
            for (int k = 0; k < pairs; k++)
            {
                int l = k, r = k + w, o = k;
                while (true)
                {
                    if (!Before(src[l], src[r], reverse))
                    {
                        dst[o] = src[r]; o += w; r += 2 * w;
                        if (r >= n) { for (; l < n; l += 2 * w, o += w) dst[o] = src[l]; break; }
                    }
                    else
                    {
                        dst[o] = src[l]; o += w; l += 2 * w;
                        if (l >= n) { for (; r < n; r += 2 * w, o += w) dst[o] = src[r]; break; }
                    }
                }
            }
            (src, dst) = (dst, src);
        }
        return src.ToList();
    }

    static readonly int[] SchoolSortOrder = { 0, 2, 1, 3, 4, 7, 5, 6, 8 };

    static int SchoolNumber(string? school) => school switch
    {
        "MAGIC_SCHOOL_DESTRUCTIVE" => 0, "MAGIC_SCHOOL_DARK" => 1, "MAGIC_SCHOOL_LIGHT" => 2, "MAGIC_SCHOOL_SUMMONING" => 3,
        "MAGIC_SCHOOL_ADVENTURE" => 4, "MAGIC_SCHOOL_RUNIC" => 5, "MAGIC_SCHOOL_WARCRIES" => 6, "MAGIC_SCHOOL_SPECIAL" => 7,
        _ => 8,
    };

    /// <summary>00a33e20 (mass to base) / 00a33f70 (empowered to base), by SpellID name.</summary>
    static string? BaseOf(string spell) => spell switch
    {
        "SPELL_MASS_CURSE" => "SPELL_CURSE", "SPELL_MASS_DISRUPTING_RAY" => "SPELL_DISRUPTING_RAY", "SPELL_MASS_SLOW" => "SPELL_SLOW",
        "SPELL_MASS_FORGETFULNESS" => "SPELL_FORGETFULNESS", "SPELL_MASS_PLAGUE" => "SPELL_PLAGUE", "SPELL_MASS_WEAKNESS" => "SPELL_WEAKNESS",
        "SPELL_MASS_BLESS" => "SPELL_BLESS", "SPELL_MASS_DISPEL" => "SPELL_DISPEL", "SPELL_MASS_STONESKIN" => "SPELL_STONESKIN",
        "SPELL_MASS_DEFLECT_ARROWS" => "SPELL_DEFLECT_ARROWS", "SPELL_MASS_BLOODLUST" => "SPELL_BLOODLUST", "SPELL_MASS_HASTE" => "SPELL_HASTE",
        "SPELL_EMPOWERED_MAGIC_ARROW" => "SPELL_MAGIC_ARROW", "SPELL_EMPOWERED_MAGIC_FIST" => "SPELL_MAGIC_FIST",
        "SPELL_EMPOWERED_LIGHTNING_BOLT" => "SPELL_LIGHTNING_BOLT", "SPELL_EMPOWERED_ICE_BOLT" => "SPELL_ICE_BOLT",
        "SPELL_EMPOWERED_FIREBALL" => "SPELL_FIREBALL", "SPELL_EMPOWERED_FROST_RING" => "SPELL_FROST_RING",
        "SPELL_EMPOWERED_CHAIN_LIGHTNING" => "SPELL_CHAIN_LIGHTNING", "SPELL_EMPOWERED_METEOR_SHOWER" => "SPELL_METEOR_SHOWER",
        "SPELL_EMPOWERED_IMPLOSION" => "SPELL_IMPLOSION", "SPELL_EMPOWERED_ARMAGEDDON" => "SPELL_ARMAGEDDON",
        "SPELL_EMPOWERED_STONE_SPIKES" => "SPELL_STONE_SPIKES", "SPELL_EMPOWERED_DEEP_FREEZE" => "SPELL_DEEP_FREEZE",
        _ => null,
    };

    static bool Before(Entry a, Entry b, bool reverse)
    {
        var db = Game.Instance.Db;
        int sa = SchoolNumber(db.Spell(a.Spell)?.School), sb = SchoolNumber(db.Spell(b.Spell)?.School);
        if (sa != sb)
        {
            foreach (var s in SchoolSortOrder)
            {
                if (s == sa) return true;
                if (s == sb) return false;
            }
            return true;
        }
        string xa = BaseOf(a.Spell) ?? a.Spell, xb = BaseOf(b.Spell) ?? b.Spell;
        if (xa != xb)
        {
            int la = db.Spell(xa)?.Level ?? 0, lb = db.Spell(xb)?.Level ?? 0;
            if (la < lb) return !reverse;
            if (la > lb) return reverse;
            return (db.Spell(xa)?.Index ?? -1) < (db.Spell(xb)?.Index ?? -1);
        }
        if (BaseOf(a.Spell) != null) return false;
        // The last test reads the list entry's first field (a reference, 00688df1); it is only reached for a spell listed
        // twice, which our distinct list never holds.
        return true;
    }

    /// <summary>
    /// 00684830 (the list for bookmark <paramref name="bm"/>): SPELL_DEBUG_TELEPORT (0xd0) and SPELL_TIME_SHIFT (0xf0)
    /// never; spells outside the runic (5) and warcry (6) schools go to the Combat tab (0) unless they are adventure
    /// spells (school 4, 00a34580) and to the Adventure tab (1) if they are; bookmark 2 = destructive (0), 3 = light (2),
    /// 4 = dark (1), 5 = summoning (3), 6 = special (7), warcries (6) and runic (5).
    /// </summary>
    bool OnBookmark(int bm, string spell)
    {
        if (spell is "SPELL_DEBUG_TELEPORT" or "SPELL_TIME_SHIFT") return false;
        if (_orcs)
        {
            // 0067cd10: abilities (00a34610, not decoded; none in our lists) and warcries go to bookmark 1, adventure
            // spells (00a34580) to bookmark 0.
            var sc = Game.Instance.Db.Spell(spell)?.School;
            return bm == 1 ? sc == "MAGIC_SCHOOL_WARCRIES" : sc == "MAGIC_SCHOOL_ADVENTURE";
        }
        var school = Game.Instance.Db.Spell(spell)?.School ?? "MAGIC_SCHOOL_NONE";
        if (school is not ("MAGIC_SCHOOL_RUNIC" or "MAGIC_SCHOOL_WARCRIES"))
        {
            bool adv = school == "MAGIC_SCHOOL_ADVENTURE";
            if (bm == 0 && !adv || bm == 1 && adv) return true;
        }
        return bm switch
        {
            2 => school == "MAGIC_SCHOOL_DESTRUCTIVE",
            3 => school == "MAGIC_SCHOOL_LIGHT",
            4 => school == "MAGIC_SCHOOL_DARK",
            5 => school == "MAGIC_SCHOOL_SUMMONING",
            6 => school is "MAGIC_SCHOOL_SPECIAL" or "MAGIC_SCHOOL_WARCRIES" or "MAGIC_SCHOOL_RUNIC",
            _ => false,
        };
    }

    /// <summary>The book part of bookmark <paramref name="bm"/> (006867c0: Combat, Adventure, PartSpellSchools; orc book
    /// 0067e940: PartSpellsCombat carries 1, PartSpellsAdventure 0).</summary>
    int BookmarkPart(int bm) => _orcs ? bm switch { 0 => _d.Adventure, 1 => _d.Combat, _ => -1 } : bm switch { 0 => _d.Combat, 1 => _d.Adventure, _ => bm - 2 < _d.Schools.Length ? _d.Schools[bm - 2] : -1 };

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
        if (_book.Has(IdleKind)) _book.Play(IdleKind, loop: true);

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

    /// <summary>The current bookmark's list, sorted (00689140; the orc book always in the normal order, 0067cede).</summary>
    List<Entry> SchoolSpells() => ExeSort(_all.Where(e => OnBookmark(_bookmark, e.Spell)).ToList(), !_orcs && _reverse);

    // H5_Game.exe 00686020 / 00686080: the index of the first shown spell (+0x190[school], reset to 0 when the school is
    // picked, 00685f60);
    // a spread shows six spells and Next / Prev move it by six while spells remain / it is above 0.
    int PageCount => Math.Max(1, (SchoolSpells().Count + 5) / 6);

    void FillSet(int[] set, IReadOnlyList<Entry?> entries)
    {
        for (int i = 0; i < set.Length; i++)
        {
            if (!_slots.TryGetValue(set[i], out var slot)) continue;
            Entry? e = i < entries.Count ? entries[i] : null;
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

    /// <summary>The six spells of spread <paramref name="page"/> (null where there is none).</summary>
    Entry?[] Spread(int page)
    {
        var spells = SchoolSpells();
        return Enumerable.Range(page * 6, 6).Select(i => i < spells.Count ? spells[i] : null).ToArray();
    }

    void RefreshPage()
    {
        // 00685270 without a turn: the static pages (PartSpellsFirstPage) show the spread, the turning page
        // (PartSpellsSecondPage) is empty.
        if (_orcs)
        {
            // Orc book 0067d950: PartSpellsFirstPage takes the list from 0, PartSpellsSecondPage from 4 (0067d4a0);
            // no spreads.
            var list = SchoolSpells();
            FillSet(_d.First, list.Cast<Entry?>().ToList());
            FillSet(_d.Second, list.Skip(_d.First.Length).Cast<Entry?>().ToList());
            RefreshMarks();
            return;
        }
        FillSet(_d.First, Spread(_page));
        FillSet(_d.Second, Array.Empty<Entry?>());
        RefreshMarks();
    }

    /// <summary>The resting animation: idle00 (book); the orc book loops idle01 on bookmark 0 and idle02 on bookmark 1
    /// (0067d950: animation index +0x1b8 = 0 / 1, looped; set order idle01, idle02, page01, page02).</summary>
    string IdleKind => _orcs ? (_bookmark == 0 ? "idle01" : "idle02") : "idle00";

    /// <summary>
    /// Orc book bookmark change (0067e2a0 input_tab: to the other bookmark; 0067e2e0 input_next_page: 0 → 1;
    /// 0067e320 input_previous_page: 1 → 0; 0067e3a0 MB_Button_School: to the part's bookmark unless current): the turn
    /// animation +0x1b8 is page01 going to bookmark 1 and page02 going to 0, then the lists are refilled (0067d950).
    /// </summary>
    async Task OrcTurn(int bm)
    {
        if (bm == _bookmark) return;
        _turning = true;
        _bookmark = bm;
        var kind = bm == 1 ? "page01" : "page02";
        if (_book.Has(kind))
        {
            _book.Play(kind);
            await ToSignal(_book.Player, AnimationMixer.SignalName.AnimationFinished);
        }
        RefreshPage();
        if (_book.Has(IdleKind)) _book.Play(IdleKind, loop: true);
        _turning = false;
    }

    void RefreshMarks()
    {
        if (_slots.TryGetValue(_d.SP, out var sp))
        {
            var markup = (Text(_orcs ? "OrcSpellBookMark" : "SpellBookMark") is { Length: > 0 } t ? t : Text("SpellBookMark") ?? "")
                .Replace("<value=sp_current>", _mana.ToString()).Replace("<value=sp_maximum>", _maxMana.ToString());
            sp.Mat.AlbedoTexture = Outline(null, markup, centerText: true);
        }
        SetPartVisible(_d.Next, _page + 1 < PageCount);
        SetPartVisible(_d.Prev, _page > 0);
        // Selected school bookmark glows with SchoolHighlightColor.
        for (int bm = 0; bm < 7; bm++)
            if (BookmarkPart(bm) is >= 0 and var part) HighlightPart(part, bm == _bookmark ? _d.SchoolHighlight : null);
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

    /// <summary>Screen rectangle of a part in its flat, resting (bind pose) position.</summary>
    Rect2? ScreenRect(int part)
    {
        var (min, max) = Bounds(new[] { part });
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

    /// <summary>input_a2.cfg spellbook_screen: input_previous_page / input_next_page run the Prev / Next handlers
    /// (H5_Game.exe 00686080 / 00686020); input_bookmark_up / _down (00686160) step the bookmark index to (index + 6) mod 7 /
    /// (index + 1) mod 7 (006860c0 / 006860e0) through 00685f60. input_tab (TAB) is not registered by this book (00686160
    /// binds MB_Button_School, Next / Prev, close, mouse buttons, MB_CastSpell, confirm_action, the page and bookmark
    /// keys only), so it does nothing; the orc book (0067e3e0) binds it and the page keys to its two bookmarks.</summary>
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (_turning || e is not InputEventKey { Pressed: true, Echo: false } k) return;
        foreach (var a in Interaction.KeyBindings.Actions("spellbook_screen", k))
        {
            if (_orcs)
            {
                int? to = a switch
                {
                    "input_tab" => _bookmark == 0 ? 1 : 0,
                    "input_next_page" when _bookmark == 0 => 1,
                    "input_previous_page" when _bookmark == 1 => 0,
                    _ => null,
                };
                if (to is int bm) { _ = OrcTurn(bm); AcceptEvent(); return; }
                continue;
            }
            if (a == "input_next_page" && _page + 1 < PageCount) { _ = Turn(+1); AcceptEvent(); return; }
            if (a == "input_previous_page" && _page > 0) { _ = Turn(-1); AcceptEvent(); return; }
            if (!_orcs && a is "input_bookmark_up" or "input_bookmark_down")
            {
                SelectBookmark(a == "input_bookmark_up" ? (_bookmark + 6) % 7 : (_bookmark + 1) % 7);
                AcceptEvent();
                return;
            }
        }
    }

    /// <summary>
    /// 00685f60: another bookmark starts at its first spread and the page turns forward when it lies after the current
    /// one, backward otherwise, and the order flag +0x246 is cleared. The same bookmark toggles +0x246 (the reversed level
    /// order of the sort, 00689140) and rebuilds the list without a turn (+0x1c4 = 0), keeping the first-spell index.
    /// </summary>
    void SelectBookmark(int bm)
    {
        if (bm == _bookmark)
        {
            _reverse = !_reverse;
            RefreshPage();
            return;
        }
        _reverse = false;
        int from = _bookmark;
        var old = Spread(_page);
        _bookmark = bm;
        _ = Turn(bm > from ? +1 : -1, toPage: 0, oldSpread: old);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (_turning) return;
        if (e is InputEventMouseMotion mm)
        {
            var hover = PartAt(mm.Position, SpellParts);
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
        if (PartAt(pos, Enumerable.Range(0, 7).Select(BookmarkPart)) is int tab)
        {
            // The MB_Button_School parts (006867c0 / orc 0067e940) carry their bookmark index.
            int bm = Enumerable.Range(0, 7).First(b => BookmarkPart(b) == tab);
            if (_orcs) _ = OrcTurn(bm); else SelectBookmark(bm);
            return;
        }
        if (PartAt(pos, SpellParts) is int slotPart && _slots.TryGetValue(slotPart, out var slot) && slot.E?.Option is { } option)
        {
            Chosen?.Invoke(option);
            Close();
        }
    }

    /// <summary>The parts showing spells at rest: the static pages, and for the orc book both page part lists.</summary>
    int[] SpellParts => _orcs ? _d.First.Concat(_d.Second).ToArray() : _d.First;

    async Task Turn(int dir, int? toPage = null, Entry?[]? oldSpread = null)
    {
        _turning = true;
        int target = toPage ?? _page + dir;
        // 00685270 during a turn (verified against the model's page parts): PartSpellsFirstPage = static left page
        // (3) + the turning sheet's front (3, resting on the right), PartSpellsSecondPage = the sheet's back (3, lands
        // on the left) + the page under the sheet (3, right). Forward (page01): First = old spread, Second = new;
        // backward (page02, the sheet comes back from the left): First = new, Second = old.
        var (old, neu) = (oldSpread ?? Spread(_page), Spread(target));
        var kind = dir > 0 ? "page01" : "page02";
        if (dir > 0)
        {
            if (oldSpread != null) FillSet(_d.First, old);
            FillSet(_d.Second, neu);
        }
        else FillSet(_d.Second, old);
        if (_book.Has(kind)) _book.Play(kind);
        if (dir < 0) FillSet(_d.First, neu);
        if (_book.Has(kind)) await ToSignal(_book.Player, AnimationMixer.SignalName.AnimationFinished);
        _page = target;
        // Back at rest the static pages show the new spread; reuse the rendered pages instead of rendering again.
        if (dir > 0)
            for (int i = 0; i < _d.First.Length && i < _d.Second.Length; i++)
                if (_slots.TryGetValue(_d.Second[i], out var from) && _slots.TryGetValue(_d.First[i], out var to))
                {
                    to.Mat.AlbedoTexture = from.Mat.AlbedoTexture;
                    to.Mat.AlbedoColor = from.Mat.AlbedoColor;
                    _slots[_d.First[i]] = (from.E, to.Mat);
                }
        // The sheet keeps its faces: idle00 blends in from the turn's last pose, where they are still visible.
        RefreshMarks();
        if (_book.Has(IdleKind)) _book.Play(IdleKind, loop: true);
        _turning = false;
    }

    public void Close()
    {
        // 00683040 (+0x244 set): the bookmark and its first-spell index go back to the hero.
        if (_casterKey != null && !_orcs) SavedState[_casterKey] = (_bookmark, _page * 6);
        Closed?.Invoke();
        QueueFree();
    }
}
