using Godot;
using H5.Adventure.State;
using H5.Core.Data;
using H5.Core.Xdb;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// Modal boxes built from the original UI/CommonBoxes windows (UIGameRoot ids
/// N_BOX0P/N_BOX0P2/N_BOX2P_WINDOW_ID) and the level-up box (UI/LevelUPBox).
/// Each is centred over the adventure screen with an input blocker behind it.
/// </summary>
public static partial class AdvDialogs
{
    public const string Box0p = "UI/CommonBoxes/box0p_windowsimple.xdb";
    public const string Box0p2 = "UI/CommonBoxes/box0p2_windowsimple.xdb";
    public const string Box2p = "UI/CommonBoxes/Box2p_WindowSimple.xdb";
    public const string LevelUpBox = "UI/LevelUPBox/LevelUpBox.(WindowSimple).xdb";

    /// <summary>Hosts a box window: blocker + centred UiScreen. Returns the screen.</summary>
    static UiScreen Open(Control parent, string window, Action<Control> registerClose)
    {
        var blocker = new Control { Name = "Modal", AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Stop };
        var screen = new UiScreen(window);
        var size = new Vector2(screen.Root.Rect.W, screen.Root.Rect.H);
        screen.AnchorRight = 0;
        screen.AnchorBottom = 0;
        screen.Size = size;
        blocker.AddChild(screen);
        parent.AddChild(blocker);
        void Center() => screen.Position = ((blocker.Size - size) * 0.5f).Floor();
        blocker.Resized += Center;
        Callable.From(Center).CallDeferred();
        registerClose(blocker);
        // MsgText sits inside the ScrollableText container in the original engine.
        if (screen.Find("MsgText") is { } msg && screen.Find("ScrollableText") is { } area)
        {
            msg.GetParent()?.RemoveChild(msg);
            area.AddChild(msg);
            msg.Position = Vector2.Zero;
            msg.Size = area.Size;
            area.Resized += () => msg.Size = area.Size;
        }
        return screen;
    }

    static void SetFace(UiScreen s, string node, Texture2D? tex)
    {
        if (s.Find(node) is not { } face || tex == null) return;
        face.AddChild(new TextureRect
        {
            Texture = tex, AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        });
    }

    /// <summary>A message with an OK button.</summary>
    public static void Message(Control parent, string title, string text, Texture2D? face, Action? onClose = null)
    {
        Control? blocker = null;
        var s = Open(parent, Box0p, b => blocker = b);
        s.Find("Title")?.SetText($"<h3_header><center>{title}");
        s.Find("MsgText")?.SetValue("text", text);
        SetFace(s, "Face", face);
        EnableButtons(s, "ButtonOk");
        bool closed = false;
        void Close() { if (closed) return; closed = true; blocker?.QueueFree(); onClose?.Invoke(); }
        s.Bus.On("confirm", Close);
        Bind(s, Close, Close);
    }

    /// <summary>
    /// A question box. The exe's question helpers (00677d80 / 00677e40) open N_BOX0P2 as box type 4 (00676290):
    /// the data's OK / Cancel at the foot of the 650x681 box; Yes / No (types 6 and 7) sit mid-box and are not used here.
    /// </summary>
    public static void YesNo(Control parent, string title, string text, Texture2D? face, Action<bool> answer)
    {
        Control? blocker = null;
        var s = Open(parent, Box0p2, b => blocker = b);
        EnableButtons(s, "ButtonOk", "ButtonCancel");
        s.Find("Title")?.SetText($"<h3_header><center>{title}");
        s.Find("MsgText")?.SetValue("text", text);
        SetFace(s, "Face", face);
        bool closed = false;
        void Done(bool yes) { if (closed) return; closed = true; blocker?.QueueFree(); answer(yes); }
        s.Bus.On("confirm", () => Done(true));
        s.Bus.On("cancel", () => Done(false));
        Bind(s, () => Done(true), () => Done(false));
    }

    /// <summary>Pick one of two options (left/right buttons with icon and caption), then OK.</summary>
    public static void Choose2(Control parent, string title, string text, Texture2D? face,
        (Texture2D? Icon, string Text) left, (Texture2D? Icon, string Text) right, Action<int> picked, string? header = null,
        bool choicesEnabled = true, bool okEnabled = false, (string? Title, string? Left, string? Right) tooltips = default)
    {
        Control? blocker = null;
        var s = Open(parent, Box2p, b => blocker = b);
        s.Find("Title")?.SetText($"<h3_header><center>{title}");
        if (header != null) s.Find("HeaderMain")?.SetText($"<center>{header}");
        s.Find("MsgText")?.SetValue("text", text);
        SetFace(s, "Face", face);
        var lb = s.Find("ButtonLeft");
        var rb = s.Find("ButtonRight");
        var ok = s.Find("ButtonOk");
        SetFace(s, "ButtonLeft/Icon", left.Icon);
        SetFace(s, "ButtonRight/Icon", right.Icon);
        s.Find("ButtonLeft/Text")?.SetText($"<body>{left.Text}");
        s.Find("ButtonRight/Text")?.SetText($"<body>{right.Text}");
        // Box2p (base 00676de0): both choices enabled and unpressed, OK disabled until one is picked.
        // Picking (00675f60 left / 00675eb0 right) presses that side, releases the other and enables OK;
        // picking the side already chosen confirms, as do left_force / right_force (double click, 006760c0 / 00676010).
        EnableButtons(s, "ButtonLeft", "ButtonRight");
        // Callers may disable both choices and enable OK up front (CTreeOfKnowledgeBox 006ac530 via 00675760);
        // OK then closes with no choice (-1).
        if (!choicesEnabled) { if (lb != null) lb.Enabled = false; if (rb != null) rb.Enabled = false; }
        if (ok != null) ok.Enabled = okEnabled;
        if (tooltips.Title != null) s.Find("Title")?.SetTooltip(tooltips.Title);
        if (tooltips.Left != null) lb?.SetTooltip(tooltips.Left);
        if (tooltips.Right != null) rb?.SetTooltip(tooltips.Right);
        int choice = -1;
        bool closed = false;
        void Done() { if (closed || (choice < 0 && !okEnabled)) return; closed = true; blocker?.QueueFree(); picked(choice); }
        void Pick(int c)
        {
            if (choice == c) { Done(); return; }
            choice = c;
            lb?.SetState(c == 0 ? 1 : 0);
            rb?.SetState(c == 1 ? 1 : 0);
            if (ok != null) ok.Enabled = true;
        }
        s.Bus.On("left", () => Pick(0));
        s.Bus.On("right", () => Pick(1));
        s.Bus.On("left_force", () => { choice = 0; Done(); });
        s.Bus.On("right_force", () => { choice = 1; Done(); });
        s.Bus.On("confirm", Done);
        Bind(s, Done, Done);
    }

    /// <summary>The level-up box: the gained attribute, up to two skills and two perks to choose from.</summary>
    public static void LevelUp(Control parent, HeroState hero, LevelUpOffer offer, Action<int> picked)
    {
        Control? blocker = null;
        var s = Open(parent, LevelUpBox, b => blocker = b);
        var db = Game.Instance.Db;
        // 006a8f50: Who = the box's related text "levelup" with name, level and profession (class name); SkillBack shows
        // the gained attribute's UI/HeroAttributes entry (DescriptionFileRef as heroattribute, Texture on Face_1).
        if (s.Find("Who") is { } who)
        {
            who.SetText(UiGameRoot.RelatedText("N_LEVEL_UP_WINDOW_ID", "levelup") ?? UiResources.Instance.Text("UI/LevelUPBox/levelup.txt") ?? "");
            who.SetValue("name", hero.Name);
            who.SetValue("level", offer.NewLevel.ToString());
            who.SetValue("profession", Hero.HeroScreen.ClassName(hero.Class));
        }
        SetFace(s, "Face", HeroFace(hero));
        var (attrText, attrIcon) = HeroAttribute(offer.Attribute);
        s.Find("SkillBack/Skill")?.SetValue("heroattribute", attrText);
        SetFace(s, "SkillBack/Face_1", attrIcon);
        // Options are numbered skills first, then perks, as ChooseLevelUpCmd expects.
        var options = new List<(string Name, Texture2D? Icon)>();
        foreach (var sk in offer.Skills)
            options.Add((SkillName(sk.SkillId, (int)sk.Mastery), SkillIcon(sk.SkillId, (int)sk.Mastery)));
        foreach (var p in offer.Perks)
            options.Add((SkillName(p, 1), SkillIcon(p, 1)));
        string[] slots = { "Skill1", "Skill2", "Skill3", "Skill4" };
        // Skills go in the left column, perks in the right one.
        var slotOf = new List<int>();
        for (int i = 0; i < offer.Skills.Count && i < 2; i++) slotOf.Add(i);
        for (int i = 0; i < offer.Perks.Count && i < 2; i++) slotOf.Add(2 + i);
        int choice = 0;
        for (int k = 0; k < 4; k++)
        {
            int opt = slotOf.IndexOf(k);
            var btn = s.Find(slots[k]);
            if (btn == null) continue;
            btn.Visible = opt >= 0;
            if (opt < 0) continue;
            (btn.FindChild("Choose", true, false) as UiNode)?.SetValue("heroskill", options[opt].Name);
            if (btn.FindChild("FaceSkill", true, false) is Control icon && options[opt].Icon != null)
                icon.AddChild(new TextureRect { Texture = options[opt].Icon, AnchorRight = 1, AnchorBottom = 1, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore });
            // 006a8f50: each choice gets a hero skill tooltip (SHeroSkillTooltip) shown while right-clicked.
            var tip = opt < offer.Skills.Count ? (offer.Skills[opt].SkillId, (int)offer.Skills[opt].Mastery) : (offer.Perks[opt - offer.Skills.Count], 1);
            HeroSkillTooltip.Attach(btn, () => (tip.Item1, tip.Item2, hero));
            int captured = opt;
            s.Bus.On($"skill_{k + 1}", () =>
            {
                choice = captured;
                for (int j = 0; j < 4; j++) s.Find(slots[j])?.SetState(slotOf.IndexOf(j) == captured ? 1 : 0);
            });
        }
        for (int j = 0; j < 4; j++) s.Find(slots[j])?.SetState(slotOf.IndexOf(j) == 0 ? 1 : 0);
        bool closed = false;
        void Done() { if (closed) return; closed = true; blocker?.QueueFree(); picked(options.Count == 0 ? 0 : choice); }
        s.Bus.On("MB_Button_Ok", Done);
        Bind(s, Done, Done);
    }

    /// <summary>
    /// The CommonBoxes data ships its OK / Cancel / left / right buttons disabled (`Enabled` false, e.g.
    /// Box0p/ButtonOk); the box code enables the ones it uses (window vfunc +0x3c = 00b95010 → 00a50e90, which
    /// sets the enabled flag; the two-choice box checks it through +0x40 in 00676120 / 006760c0 / 00676010).
    /// </summary>
    static void EnableButtons(UiScreen s, params string[] names)
    {
        foreach (var n in names)
            if (s.Find(n) is { } b) b.Enabled = true;
    }

    /// <summary>Enter confirms, Escape cancels (input_a2.cfg confirm_action / close_window).</summary>
    static void Bind(UiScreen s, Action ok, Action cancel)
    {
        var keys = new KeyCatcher(ok, cancel) { Name = "Keys" };
        s.AddChild(keys);
    }

    sealed partial class KeyCatcher(Action ok, Action cancel) : Node
    {
        public override void _UnhandledInput(InputEvent e)
        {
            // The box actions guard against closing twice themselves.
            if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
            if (k.Keycode is Key.Enter or Key.KpEnter) { GetViewport().SetInputAsHandled(); ok(); }
            else if (k.Keycode == Key.Escape) { GetViewport().SetInputAsHandled(); cancel(); }
        }
    }

    // ---------------------------------------------------------------- text and icons

    /// <summary>UI/HeroAttributes/HeroAttributes.(UIHeroAttributes).xdb entry of a level-up attribute: description text and icon.</summary>
    static (string Text, Texture2D? Icon) HeroAttribute(string attr)
    {
        const string table = "UI/HeroAttributes/HeroAttributes.(UIHeroAttributes).xdb";
        var key = attr switch { "Attack" => "HERO_ATTRIB_OFFENCE", "Defence" => "HERO_ATTRIB_DEFENCE", "SpellPower" => "HERO_ATTRIB_SPELLPOWER", "Knowledge" => "HERO_ATTRIB_KNOWLEDGE", _ => "" };
        var item = Game.Instance.Fs.Exists(table)
            ? Game.Instance.Xdb.Load(table).Root?.Element("attributes")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("Attribute") == key)
            : null;
        if (item == null) return ($"+1 {AttributeName(attr)}", null);
        var text = Game.Instance.Xdb.ReadText((string?)item.Element("DescriptionFileRef")?.Attribute("href"), table) ?? "";
        var tex = (string?)item.Element("Texture")?.Attribute("href");
        return (text.Trim(), string.IsNullOrEmpty(tex) ? null : Game.Instance.LoadTexture(H5.Core.Xdb.XdbRef.Parse(tex, table).Path));
    }

    public static string AttributeName(string a) => a switch
    {
        "STAT_ATTACK" or "ATTACK" => "Attack",
        "STAT_DEFENCE" or "DEFENCE" => "Defense",
        "STAT_SPELL_POWER" or "SPELL_POWER" or "SPELLPOWER" => "Spell Power",
        "STAT_KNOWLEDGE" or "KNOWLEDGE" => "Knowledge",
        _ => a.Replace("STAT_", "").Replace('_', ' ').ToLowerInvariant(),
    };

    const string SkillsTable = "GameMechanics/RefTables/Skills.xdb";

    static System.Xml.Linq.XElement? SkillItem(string id) =>
        Game.Instance.Xdb.Load(SkillsTable).Root?.Element("objects")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == id)?.Element("obj");

    /// <summary>Skill name at a mastery level ("Advanced Logistics"): Skills.xdb NameFileRef[level − 1].</summary>
    public static string SkillName(string id, int level)
    {
        var names = SkillItem(id)?.Element("NameFileRef")?.Elements("Item").ToList();
        if (names is { Count: > 0 })
        {
            var href = (string?)names[Math.Clamp(level - 1, 0, names.Count - 1)].Attribute("href");
            if (UiResources.Instance.Text(href) is { Length: > 0 } t) return UiResources.StripMarkup(t).Trim();
        }
        return Game.Instance.Db.Skills.TryGetValue(id, out var def) && def.Name.Length > 0 ? def.Name : id;
    }

    public static Texture2D? HeroFace(HeroState h)
    {
        if (!Game.Instance.Db.Heroes.TryGetValue(h.DefId, out var def) || !Game.Instance.Fs.Exists(def.SourcePath)) return null;
        var root = Game.Instance.Xdb.Load(def.SourcePath).Root!;
        var href = (string?)root.Element("FaceTexture")?.Attribute("href");
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(XdbRef.Parse(href, def.SourcePath).Path);
    }

    /// <summary>Skill/perk icon from Skills.xdb: Texture items are indexed by mastery (item 0 is empty for skills).</summary>
    public static Texture2D? SkillIcon(string id, int level)
    {
        var textures = SkillItem(id)?.Element("Texture")?.Elements("Item").ToList();
        if (textures is not { Count: > 0 }) return null;
        var href = (string?)textures[Math.Clamp(level, 0, textures.Count - 1)].Attribute("href");
        if (string.IsNullOrEmpty(href)) href = textures.Select(t => (string?)t.Attribute("href")).FirstOrDefault(h => !string.IsNullOrEmpty(h));
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(XdbRef.Parse(href, SkillsTable).Path);
    }
}
