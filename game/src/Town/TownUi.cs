using Godot;
using H5.Adventure.State;
using H5Reborn.Combat;
using H5Reborn.Ui;

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
    /// <summary>The window that receives the creature picture (the slot's 'Pic' child or the button itself).</summary>
    public Control PicHost => Node.FindChild("Pic", true, false) as Control ?? Node;
}

/// <summary>
/// Drag-and-drop between army slots, like the stock screens: drag a stack onto another slot to move,
/// swap or merge it (SwapStacksCmd); hold Shift while dropping to split (original SplitStack window);
/// click a stack, then another slot, does the same as dragging.
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
            var army = v.Hero >= 0 ? s.Hero(v.Hero)?.Army : v.Town >= 0 ? s.Town(v.Town)?.Garrison : null;
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
                H5Reborn.Interaction.CreatureInfoWindow.Show(_overlay, st.Creature, st.Count, v.Hero >= 0 ? _infoState.Hero(v.Hero) : null);
                return;
            }
            if (mb.Pressed) { _dragFrom = v; _pressPos = mb.GlobalPosition; }
            else if (_dragFrom != null)
            {
                var from = _dragFrom;
                _dragFrom = null;
                _dragIcon?.QueueFree();
                _dragIcon = null;
                var target = SlotAt(mb.GlobalPosition);
                bool dragged = (mb.GlobalPosition - _pressPos).Length() > 6;
                if (dragged && target != null && target != from) Drop(from, target, Input.IsKeyPressed(Key.Shift));
                else if (!dragged) ClickSlot(v);
            }
        }
        else if (e is InputEventMouseMotion mm && _dragFrom != null && _dragIcon == null && (mm.GlobalPosition - _pressPos).Length() > 6)
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

    H5.Adventure.State.Stack? StackOf(ArmySlotView v)
    {
        var army = v.Hero >= 0 ? _infoState?.Hero(v.Hero)?.Army : v.Town >= 0 ? _infoState?.Town(v.Town)?.Garrison : null;
        return army != null && v.Slot < army.Length ? army[v.Slot] : null;
    }

    ArmySlotView? SlotAt(Vector2 global) =>
        _slots.LastOrDefault(s => s.Node.IsVisibleInTree() && s.Node.GetGlobalRect().HasPoint(global));

    void ClickSlot(ArmySlotView v)
    {
        if (_selected == null) { _selected = v; Changed?.Invoke(); return; }
        var from = _selected;
        _selected = null;
        if (from != v) Drop(from, v, Input.IsKeyPressed(Key.Shift));
        else Changed?.Invoke();
    }

    void Drop(ArmySlotView from, ArmySlotView to, bool split)
    {
        var sink = _sink();
        if (split)
        {
            SplitDialog.Open(_overlay, from, to, count =>
            {
                sink(new H5.Adventure.Engine.TransferStackCmd(from.Hero, from.Town, from.Slot, to.Hero, to.Town, to.Slot, count));
                Changed?.Invoke();
            });
            return;
        }
        sink(new H5.Adventure.Engine.SwapStacksCmd(from.Hero, from.Town, from.Slot, to.Hero, to.Town, to.Slot));
        Changed?.Invoke();
    }
}

/// <summary>The stock "Split Army" window (UI/SplitStack/SplitStack): type the number to move, then Split.</summary>
public static class SplitDialog
{
    public static void Open(Control parent, ArmySlotView from, ArmySlotView to, Action<int> split)
    {
        var s = (TownScreen.Current?.Session.S);
        var army = from.Hero >= 0 ? s?.Hero(from.Hero)?.Army : s?.Town(from.Town)?.Garrison;
        var stack = army?[from.Slot];
        if (stack is not { Count: > 1 }) return;
        var screen = new UiScreen("UI/SplitStack/SplitStack.(WindowSimple).xdb");
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.35f), AnchorRight = 1, AnchorBottom = 1 };
        parent.AddChild(dim);
        parent.AddChild(screen);
        // Centre the box (authored at 0,0).
        var box = screen.Find("SplitStack");
        if (box != null) box.Position = (parent.Size - box.Size) / 2;
        TownUi.SetPicture(screen.Find("Icon1"), TownUi.CreatureIcon(stack.Creature));
        TownUi.SetPicture(screen.Find("Icon2"), TownUi.CreatureIcon(stack.Creature));
        int half = stack.Count / 2;
        screen.Find("Cell")?.SetText($"<center>{stack.Count - half}");
        if (screen.Find("Edit") is UiEditLine edit)
        {
            edit.Value = half.ToString();
            edit.Changed += v => screen.Find("Cell")?.SetText($"<center>{stack.Count - Math.Clamp(Parse(v), 0, stack.Count)}");
        }
        void Close() { screen.QueueFree(); dim.QueueFree(); }
        screen.Bus.On("split", () =>
        {
            int n = Math.Clamp(Parse((screen.Find("Edit") as UiEditLine)?.Value ?? "0"), 0, stack.Count);
            Close();
            if (n > 0) split(n);
        });
        screen.Bus.On("split_close", Close);
    }

    static int Parse(string s) => int.TryParse(s.Trim(), out var v) ? v : 0;
}
