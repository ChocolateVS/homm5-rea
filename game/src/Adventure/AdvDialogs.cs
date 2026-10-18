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
        void Close() { blocker?.QueueFree(); onClose?.Invoke(); }
        s.Bus.On("confirm", Close);
        Bind(s, Close, Close);
    }

    /// <summary>A Yes/No question.</summary>
    public static void YesNo(Control parent, string title, string text, Texture2D? face, Action<bool> answer)
    {
        Control? blocker = null;
        var s = Open(parent, Box0p2, b => blocker = b);
        foreach (var n in new[] { "ButtonOk", "ButtonCancel" }) s.SetVisible(n, false);
        foreach (var n in new[] { "ButtonYes", "ButtonNo" }) s.SetVisible(n, true);
        s.Find("Title")?.SetText($"<h3_header><center>{title}");
        s.Find("MsgText")?.SetValue("text", text);
        SetFace(s, "Face", face);
        void Done(bool yes) { blocker?.QueueFree(); answer(yes); }
        s.Bus.On("confirm", () => Done(true));
        s.Bus.On("cancel", () => Done(false));
        Bind(s, () => Done(true), () => Done(false));
    }

    /// <summary>Pick one of two options (left/right buttons with icon and caption), then OK.</summary>
    public static void Choose2(Control parent, string title, string text, Texture2D? face,
        (Texture2D? Icon, string Text) left, (Texture2D? Icon, string Text) right, Action<int> picked)
    {
        Control? blocker = null;
        var s = Open(parent, Box2p, b => blocker = b);
        s.Find("Title")?.SetText($"<h3_header><center>{title}");
        s.Find("MsgText")?.SetValue("text", text);
        SetFace(s, "Face", face);
        int choice = 0;
        var lb = s.Find("ButtonLeft");
        var rb = s.Find("ButtonRight");
        SetFace(s, "ButtonLeft/Icon", left.Icon);
        SetFace(s, "ButtonRight/Icon", right.Icon);
        s.Find("ButtonLeft/Text")?.SetText($"<body>{left.Text}");
        s.Find("ButtonRight/Text")?.SetText($"<body>{right.Text}");
        void Pick(int c) { choice = c; lb?.SetState(c == 0 ? 1 : 0); rb?.SetState(c == 1 ? 1 : 0); }
        s.Bus.On("left", () => Pick(0));
        s.Bus.On("right", () => Pick(1));
        Pick(0);
        void Done() { blocker?.QueueFree(); picked(choice); }
        s.Bus.On("confirm", Done);
        Bind(s, Done, Done);
    }

    /// <summary>The level-up box: the gained attribute, up to two skills and two perks to choose from.</summary>
    public static void LevelUp(Control parent, HeroState hero, LevelUpOffer offer, Action<int> picked)
    {
        Control? blocker = null;
        var s = Open(parent, LevelUpBox, b => blocker = b);
        var db = Game.Instance.Db;
        s.Find("Who")?.SetText($"<body>{hero.Name} has reached level {offer.NewLevel}.");
        SetFace(s, "Face", HeroFace(hero));
        s.Find("SkillBack/Skill")?.SetValue("heroattribute", $"+1 {AttributeName(offer.Attribute)}");
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
            int captured = opt;
            s.Bus.On($"skill_{k + 1}", () =>
            {
                choice = captured;
                for (int j = 0; j < 4; j++) s.Find(slots[j])?.SetState(slotOf.IndexOf(j) == captured ? 1 : 0);
            });
        }
        for (int j = 0; j < 4; j++) s.Find(slots[j])?.SetState(slotOf.IndexOf(j) == 0 ? 1 : 0);
        void Done() { blocker?.QueueFree(); picked(options.Count == 0 ? 0 : choice); }
        s.Bus.On("MB_Button_Ok", Done);
        Bind(s, Done, Done);
    }

    /// <summary>Enter confirms, Escape cancels (input_a2.cfg confirm_action / close_window).</summary>
    static void Bind(UiScreen s, Action ok, Action cancel)
    {
        var keys = new KeyCatcher(ok, cancel) { Name = "Keys" };
        s.AddChild(keys);
    }

    sealed partial class KeyCatcher(Action ok, Action cancel) : Node
    {
        bool _done;
        public override void _UnhandledInput(InputEvent e)
        {
            if (_done || e is not InputEventKey { Pressed: true, Echo: false } k) return;
            if (k.Keycode is Key.Enter or Key.KpEnter) { _done = true; GetViewport().SetInputAsHandled(); ok(); }
            else if (k.Keycode == Key.Escape) { _done = true; GetViewport().SetInputAsHandled(); cancel(); }
        }
    }

    // ---------------------------------------------------------------- text and icons

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
    static Texture2D? SkillIcon(string id, int level)
    {
        var textures = SkillItem(id)?.Element("Texture")?.Elements("Item").ToList();
        if (textures is not { Count: > 0 }) return null;
        var href = (string?)textures[Math.Clamp(level, 0, textures.Count - 1)].Attribute("href");
        if (string.IsNullOrEmpty(href)) href = textures.Select(t => (string?)t.Attribute("href")).FirstOrDefault(h => !string.IsNullOrEmpty(h));
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(XdbRef.Parse(href, SkillsTable).Path);
    }
}
