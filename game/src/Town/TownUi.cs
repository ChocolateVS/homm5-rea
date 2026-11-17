using Godot;
using H5.Adventure.State;
using H5Reborn.Combat;
using H5Reborn.Ui;
using Side = H5.Adventure.Engine.ArmySplit.Side;

namespace H5Reborn.Town;

/// <summary>Small helpers shared by the town and hero screens.</summary>
public static class TownUi
{
    public static readonly string[] ResourceNames = { "Wood", "Ore", "Mercury", "Crystal", "Sulfur", "Gem", "Gold" };
    /// <summary>Lower-case keys used by the CreatureCountHolder / cost windows (same order as <see cref="ResourceNames"/>).</summary>
    public static readonly string[] CostKeys = { "wood", "ore", "mercury", "crystal", "sulfur", "gem", "gold" };

    public static void SetResourceBar(UiScreen screen, PlayerState p)
    {
        for (int i = 0; i < ResourceNames.Length; i++)
            screen.Find($"resource-bar/{ResourceNames[i]}")?.SetText($"<center>{p.Purse[i]}");
    }

    /// <summary>
    /// Fills a CreatureCountHolder strip (one window per resource with 'normal' and 'red' texts):
    /// shows only the resources a price uses, red when the player cannot afford that part.
    /// </summary>
    public static void SetCostStrip(UiNode? holder, int[]? cost, PlayerState p)
    {
        if (holder == null) return;
        for (int i = 0; i < CostKeys.Length; i++)
        {
            if (holder.FindChild(CostKeys[i], false, false) is not UiNode item) continue;
            int need = cost?[i] ?? 0;
            item.Visible = need > 0;
            bool afford = p.Purse[i] >= need;
            if (item.FindChild("normal", false, false) is UiNode n) { n.Visible = afford; n.SetValue("param", need.ToString()); }
            if (item.FindChild("red", false, false) is UiNode r) { r.Visible = !afford; r.SetValue("param", need.ToString()); }
        }
    }

    /// <summary>Puts a picture inside a window (behind its text and frames), replacing a previous one.</summary>
    public static void SetPicture(Control? host, Texture2D? tex)
    {
        if (host == null) return;
        var rect = host.GetNodeOrNull<TextureRect>("_pic");
        if (rect == null)
        {
            rect = new TextureRect
            {
                Name = "_pic", AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                ShowBehindParent = false,
            };
            host.AddChild(rect);
            host.MoveChild(rect, 0);
        }
        rect.Texture = tex;
        rect.Visible = tex != null;
    }

    public static Texture2D? CreatureIcon(string creature, bool large = false) =>
        CombatIcons.Creature(creature, large) is { } p ? Game.Instance.LoadTexture(p) : null;

    public static Texture2D? HeroFace(HeroState h, bool large = true) =>
        CombatIcons.Hero(h.DefId, large) is { } p ? Game.Instance.LoadTexture(p) : null;

    /// <summary>Stock text file contents without markup (UI/…/*.txt), or the fallback.</summary>
    public static string Text(string path, string fallback = "") =>
        UiResources.Instance.Text(path) is { Length: > 0 } t ? t.Trim() : fallback;
}

/// <summary>One army slot on screen: a button window bound to (hero or town, slot index).</summary>
public sealed class ArmySlotView
{
    public required UiNode Node;
    public int Hero = -1, Town = -1, Slot;
    /// <summary>A hero shown without being in the game state (a tavern offer): its army is read from here.</summary>
    public H5.Adventure.State.HeroState? Snapshot;
    /// <summary>The window that receives the creature picture (the slot's 'Pic' child or the button itself).</summary>
    public Control PicHost => Node.FindChild("Pic", true, false) as Control ?? Node;
}

/// <summary>
/// Drag-and-drop between army slots, like the stock screens (town 00615670, hero screen 006c5e60, garrison 00652b70):
/// with Shift held (the screens' "shift" key flag, 00492a40) the stock SplitStack window opens (<see cref="ArmyMove.OpenSplit"/>);
/// otherwise a stack dropped on a stack of the same creature merges into it — inside one army too — and anything else
/// moves or exchanges (<see cref="ArmyMove.Drop"/>). Click a stack, then another slot, does the same as dragging.
/// </summary>
public sealed class ArmySlots
{
    readonly List<ArmySlotView> _slots = new();
    readonly Func<AdvCommandSink> _sink;
    readonly Control _overlay;
    ArmySlotView? _dragFrom, _selected;
    TextureRect? _dragIcon;
    Vector2 _pressPos;

    public delegate bool AdvCommandSink(H5.Adventure.Engine.AdvCommand cmd);

    public event Action? Changed;

    /// <summary>No moves or splits (a hero shown as a snapshot); a double click still opens the creature info.</summary>
    public bool ReadOnly;

    public ArmySlots(Control overlay, Func<AdvCommandSink> sink)
    {
        _overlay = overlay;
        _sink = sink;
    }

    public void Add(ArmySlotView v)
    {
        _slots.Add(v);
        v.Node.MouseFilter = Control.MouseFilterEnum.Stop;
        v.Node.GuiInput += e => OnInput(v, e);
    }

    public void Clear() => _slots.Clear();

    public IReadOnlyList<ArmySlotView> Views => _slots;

    public void Refresh(H5.Adventure.State.AdvState s)
    {
        _infoState = s; // for CreatureInfoWindow on double click
        foreach (var v in _slots)
        {
            var army = ArmyOf(v);
            var stack = army != null && v.Slot < army.Length ? army[v.Slot] : null;
            // The 'Pic' window is hidden in the data and shown by code when the slot holds a stack.
            v.PicHost.Visible = stack is { Count: > 0 };
            TownUi.SetPicture(v.PicHost, stack is { Count: > 0 } ? TownUi.CreatureIcon(stack.Creature) : null);
            if (v.Node.FindChild("Text", true, false) is UiNode t) { t.Visible = stack is { Count: > 0 }; t.SetValue("NumberOfCreatures", stack?.Count.ToString() ?? ""); }
            if (v.Node.FindChild("highlight", true, false) is Control hl) hl.Visible = v == _selected;
            v.Node.Visible = army != null || v.Node.Visible;
        }
    }

    void OnInput(ArmySlotView v, InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            // Double click on a stack opens the stock creature info screen (manual p.10) — Input.CreatureInfoWindow.
            if (mb.Pressed && mb.DoubleClick && _infoState != null && StackOf(v) is { Count: > 0 } st)
            {
                _dragFrom = null;
                _selected = null;
                Changed?.Invoke();
                H5Reborn.Interaction.CreatureInfoWindow.Show(_overlay, st.Creature, st.Count, v.Snapshot ?? (v.Hero >= 0 ? _infoState.Hero(v.Hero) : null));
                return;
            }
            if (ReadOnly) return;
            if (mb.Pressed) { _dragFrom = v; _pressPos = mb.GlobalPosition; }
            else if (_dragFrom != null)
            {
                var from = _dragFrom;
                _dragFrom = null;
                _dragIcon?.QueueFree();
                _dragIcon = null;
                var target = SlotAt(mb.GlobalPosition);
                bool dragged = (mb.GlobalPosition - _pressPos).Length() > 6;
                if (dragged && target != null && target != from) Drop(from, target, ArmyMove.ShiftHeld);
                else if (!dragged) ClickSlot(v);
            }
        }
        else if (!ReadOnly && e is InputEventMouseMotion mm && _dragFrom != null && _dragIcon == null && (mm.GlobalPosition - _pressPos).Length() > 6)
        {
            var tex = (_dragFrom.PicHost.GetNodeOrNull<TextureRect>("_pic"))?.Texture;
            if (tex == null) { _dragFrom = null; return; }
            _dragIcon = new TextureRect { Texture = tex, Size = new Vector2(55, 55), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0.85f) };
            _overlay.AddChild(_dragIcon);
        }
        if (e is InputEventMouseMotion && _dragIcon != null)
            _dragIcon.GlobalPosition = _overlay.GetGlobalMousePosition() - new Vector2(27, 27);
    }

    H5.Adventure.State.AdvState? _infoState;

    H5.Adventure.State.Stack?[]? ArmyOf(ArmySlotView v) =>
        v.Snapshot?.Army ?? (v.Hero >= 0 ? _infoState?.Hero(v.Hero)?.Army : v.Town >= 0 ? _infoState?.Town(v.Town)?.Garrison : null);

    H5.Adventure.State.Stack? StackOf(ArmySlotView v) => ArmyOf(v) is { } army && v.Slot < army.Length ? army[v.Slot] : null;

    ArmySlotView? SlotAt(Vector2 global) =>
        _slots.LastOrDefault(s => s.Node.IsVisibleInTree() && s.Node.GetGlobalRect().HasPoint(global));

    void ClickSlot(ArmySlotView v)
    {
        if (_selected == null) { _selected = v; Changed?.Invoke(); return; }
        var from = _selected;
        _selected = null;
        if (from != v) Drop(from, v, ArmyMove.ShiftHeld);
        else Changed?.Invoke();
    }

    Side SideOf(ArmySlotView v) => new(v.Hero, v.Town, -1, v.Slot, ArmyOf(v));

    void Drop(ArmySlotView from, ArmySlotView to, bool split)
    {
        var sink = _sink();
        var a = SideOf(from);
        var b = SideOf(to);
        if (split)
        {
            ArmyMove.OpenSplit(_overlay, a, b, cmd => sink(cmd), () => Changed?.Invoke());
            return;
        }
        if (ArmyMove.Drop(a, b) is { } cmd) sink(cmd);
        Changed?.Invoke();
    }
}

/// <summary>
/// The army-slot rules shared by the town rows, the hero screen (one or two heroes), the garrison and caravan screens;
/// the pure parts live in the engine (<see cref="H5.Adventure.Engine.ArmySplit"/>, tested there).
/// </summary>
public static class ArmyMove
{
    /// <summary>The "shift" key flag the army screens register (bind +shift 'SHIFT'; read by 00492a40).</summary>
    public static bool ShiftHeld => Input.IsKeyPressed(Key.Shift);

    /// <summary>A plain drop (see <see cref="H5.Adventure.Engine.ArmySplit.Drop"/>).</summary>
    public static H5.Adventure.Engine.AdvCommand? Drop(Side a, Side b) => H5.Adventure.Engine.ArmySplit.Drop(a, b);

    /// <summary>
    /// The stock "Split Army" window (UI/SplitStack, CSplitStackWindow 005872b0 / 00587800): Edit holds the target's new size,
    /// Cell the dragged side's rest (total − value, 00587230), the Scroll slider runs over [min, max] with page 1 (005872b0);
    /// typed text is clamped to the range (00587060); Split applies (00587140), split_close closes. Nothing opens when
    /// the controller refuses (00587cf0 returns 0: <see cref="H5.Adventure.Engine.ArmySplit.CanSplit"/>).
    /// </summary>
    public static void OpenSplit(Control parent, Side from, Side to, Func<H5.Adventure.Engine.AdvCommand, bool> sink, Action? done)
    {
        var a = from;
        var b = to;
        if (!H5.Adventure.Engine.ArmySplit.CanSplit(a, b)) { done?.Invoke(); return; }
        var src = a.Stack!;
        int total = src.Count + (b.Stack?.Count ?? 0);
        int value = H5.Adventure.Engine.ArmySplit.StartValue(a, b, out int lo, out int hi);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0), AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Stop };
        var ui = new UiScreen("UI/SplitStack/SplitStack.(WindowSimple).xdb");
        var size = new Vector2(ui.Root.Rect.W, ui.Root.Rect.H);
        ui.AnchorRight = 0;
        ui.AnchorBottom = 0;
        ui.Size = size;
        dim.AddChild(ui);
        parent.AddChild(dim);
        void Center() => ui.Position = ((dim.Size - size) * 0.5f).Floor();
        dim.Resized += Center;
        Callable.From(Center).CallDeferred();
        // 005872b0: both icons show the dragged side's creature.
        TownUi.SetPicture(ui.Find("Icon1"), TownUi.CreatureIcon(src.Creature));
        TownUi.SetPicture(ui.Find("Icon2"), TownUi.CreatureIcon(src.Creature));
        var edit = ui.Find("Edit") as UiEditLine;
        var scroll = ui.Find("Scroll") as UiScrollBar;
        bool syncing = false;
        void Show(bool setEdit)
        {
            syncing = true;
            if (setEdit && edit != null) edit.Value = value.ToString();
            ui.Find("Cell")?.SetText($"<center>{total - value}");
            scroll?.SetValue(value);
            syncing = false;
        }
        if (scroll != null)
        {
            scroll.SetRange(lo, hi + 1, 1);
            scroll.ValueChanged += v => { if (syncing) return; value = Math.Clamp((int)MathF.Round(v), lo, hi); Show(true); };
        }
        // 00587060: a value in range is taken, anything else is clamped to [lo, hi] (text that is not a number counts as 0).
        if (edit != null)
            edit.Changed += t => { if (syncing) return; value = Math.Clamp(int.TryParse(t.Trim(), out var n) ? n : 0, lo, hi); Show(false); };
        Show(true);
        bool closed = false;
        void Close() { if (closed) return; closed = true; dim.QueueFree(); done?.Invoke(); }
        ui.Bus.On("split", () =>
        {
            var cmds = H5.Adventure.Engine.ArmySplit.SplitCommands(a, b, value);
            foreach (var c in cmds) sink(c);
            Close();
        });
        ui.Bus.On("split_close", Close);
    }
}
