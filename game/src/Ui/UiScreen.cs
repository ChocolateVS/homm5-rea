using Godot;
using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>
/// Hosts one original UI screen (a WindowScreen xdb) as a Godot Control tree.
/// Layout is computed in real screen pixels with the original anchoring rules
/// (screens authored for 1024x768; ALIGN_SCREEN roots fill the viewport and
/// children re-anchor by their alignments), and redone on resize. Children are
/// added in the original paint order, so Godot's draw order and reverse-order
/// mouse picking match the stock engine.
/// </summary>
public partial class UiScreen : Control
{
    public UiWindow Root { get; }
    public UiMessageBus Bus { get; }
    readonly Dictionary<UiWindow, UiNode> _nodes = new();

    public UiScreen(string screenXdb, UiMessageBus? parentBus = null)
    {
        StockTooltip.EnsureInit();
        Root = UiResources.Instance.LoadWindow(screenXdb);
        Bus = new UiMessageBus(parentBus ?? UiMessageBus.Global);
        Name = System.IO.Path.GetFileNameWithoutExtension(screenXdb);
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
        // Design-size layout up front so screen code can read window rects while building.
        UiLayout.Apply(Root);
        AddChild(Build(Root));
        // Events rather than _EnterTree/_ExitTree overrides, which subclasses replace.
        TreeEntered += () => Settings.VideoSettings.Changed += QueueRelayout;
        TreeExiting += () => Settings.VideoSettings.Changed -= QueueRelayout;
    }

    UiNode Build(UiWindow w)
    {
        UiNode node = w.Type switch
        {
            "WindowEditLine" => new UiEditLine(w),
            "WindowComboBox" => new UiComboBox(w),
            "WindowScrollBar" => new UiScrollBar(w),
            "WindowScrollableContainer" => new UiScrollableContainer(w),
            _ => new UiNode(w),
        };
        node.Screen = this;
        _nodes[w] = node;
        foreach (var child in UiLayout.PaintOrder(w))
            node.AddChild(Build(child));
        node.OnBuilt();
        return node;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) Relayout();
    }

    public override void _Ready() => Relayout();

    public void Relayout()
    {
        _relayoutQueued = false;
        var size = Size;
        if (size.X <= 0 || size.Y <= 0) size = GetViewportRect().Size;
        // UI scale (user-requested extension): lay out at the virtual size with the original
        // anchoring rules, then scale the whole tree uniformly; input follows the transform.
        float scale = UiScale;
        int w = (int)Math.Round(size.X / scale), h = (int)Math.Round(size.Y / scale);
        UiLayout.Apply(Root, w, h);
        if (ApplyTextSizes()) UiLayout.Apply(Root, w, h);
        Place(Root, new UiRect(0, 0, 0, 0));
        var rootNode = _nodes[Root];
        rootNode.Scale = new Vector2(scale, scale);
        // Bitmap fonts stay pixel-exact at integer scales; filter otherwise.
        rootNode.TextureFilter = Math.Abs(scale - Math.Round(scale)) < 0.001 ? TextureFilterEnum.Nearest : TextureFilterEnum.Linear;
    }

    /// <summary>Uniform scale applied to this screen's UI (Settings.VideoSettings.UiScale unless overridden).</summary>
    public float UiScale => ScaleOverride ?? Settings.VideoSettings.UiScale;
    public float? ScaleOverride { get; set; }


    /// <summary>Converts a point in this screen's (unscaled) coordinates to design/virtual UI coordinates.</summary>
    public Vector2 ToUi(Vector2 local) => local / UiScale;

    /// <summary>
    /// WindowTextView ResizeOnTextSet: the window's height becomes its text block's height
    /// (width too with ResizeWidthOnTextSet) and it is then re-placed by its own alignment,
    /// so e.g. a zero-height, centre-aligned header ends up vertically centred on its slot.
    /// </summary>
    bool ApplyTextSizes()
    {
        bool changed = false;
        foreach (var (w, node) in _nodes)
        {
            if (!node.ResizesOnText) continue;
            _authoredSize.TryAdd(w, w.Placement.Size);
            var authored = _authoredSize[w];
            var text = node.MeasureText(node.ResizesWidthOnText ? 0 : w.Rect.W);
            if (text == Vector2.Zero) continue;
            var resized = new Int2(node.ResizesWidthOnText ? (int)Math.Ceiling(text.X) : authored.Value.X, (int)Math.Ceiling(text.Y));
            w.Placement = w.Placement.MergedWith(new UiPlacement { Size = (resized, true) });
            changed = true;
        }
        return changed;
    }

    readonly Dictionary<UiWindow, (Int2 Value, bool Set)> _authoredSize = new();
    bool _relayoutQueued;

    /// <summary>Relayout at the end of the frame (text changes on ResizeOnTextSet windows).</summary>
    public void QueueRelayout()
    {
        if (_relayoutQueued || !IsInsideTree()) return;
        _relayoutQueued = true;
        CallDeferred(MethodName.Relayout);
    }

    void Place(UiWindow w, UiRect parent)
    {
        var node = _nodes[w];
        node.Position = new Vector2(w.Rect.X - parent.X, w.Rect.Y - parent.Y);
        node.Size = new Vector2(Math.Max(0, w.Rect.W), Math.Max(0, w.Rect.H));
        foreach (var c in w.Children) Place(c, w.Rect);
        node.OnPlaced();
    }

    public UiNode Node(UiWindow w) => _nodes[w];

    /// <summary>
    /// Instantiates a copy of a template window (the original screens keep hidden
    /// "*Template" windows that code clones, e.g. GameOptions' CheckBoxPanelTemplate)
    /// under <paramref name="parent"/>, offset by <paramref name="position"/> in the
    /// parent's coordinates, and lays the screen out again.
    /// </summary>
    public UiNode Instantiate(UiWindow template, UiWindow parent, Int2 position, string? name = null)
    {
        var copy = UiWindowCopy.Deep(template);
        copy.Visible = true;
        if (name != null) copy.Name = name;
        // A template stretched by EPA_MARGIN keeps the size it was laid out with (its margins refer to the
        // template's own parent); re-anchoring it at the low end with its authored size 0 made it zero wide, so
        // e.g. the game options' check box labels wrapped after every word.
        var ps = copy.Placement.Size.Value;
        var size = new Int2(copy.Placement.HorAlign.Value == UiAlign.Margin ? template.Rect.W : ps.X,
            copy.Placement.VerAlign.Value == UiAlign.Margin ? template.Rect.H : ps.Y);
        copy.Placement = copy.Placement.MergedWith(new UiPlacement
        {
            Size = (size, true),
            Position = (position, true),
            HorAlign = (UiAlign.LowEnd, true),
            VerAlign = (UiAlign.LowEnd, true),
        });
        parent.Children.Add(copy);
        var node = Build(copy);
        _nodes[parent].AddChild(node);
        QueueRelayout();
        return node;
    }

    /// <summary>Removes a window made by <see cref="Instantiate"/> (and its node) from the screen.</summary>
    public void RemoveInstance(UiNode node)
    {
        foreach (var w in node.Window.Descendants().Prepend(node.Window)) _nodes.Remove(w);
        foreach (var parent in _nodes.Keys.ToList()) parent.Children.Remove(node.Window);
        node.QueueFree();
        QueueRelayout();
    }

    /// <summary>Finds a node by window name, optionally by a '/'-separated path of names (e.g. "MultiPlayer/MenuPanel/BackBtn").</summary>
    public UiNode? Find(string path)
    {
        UiWindow? cur = Root;
        foreach (var part in path.Split('/'))
        {
            cur = cur?.Children.FirstOrDefault(c => Eq(c.Name, part)) ?? cur?.Find(part);
            if (cur == null) return null;
        }
        return cur == null ? null : _nodes[cur];
    }

    /// <summary>The node built for a window of this screen (null if it isn't part of it).</summary>
    public UiNode? NodeOf(UiWindow w) => _nodes.GetValueOrDefault(w);

    public IEnumerable<UiNode> FindAll(string name) =>
        Root.Descendants().Where(w => Eq(w.Name, name)).Select(w => _nodes[w]);

    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Shows one of a set of alternative top-level panels (screens keep them all Visible=true in data and toggle from code).</summary>
    public void ShowOnly(string name, params string[] panels)
    {
        foreach (var p in panels)
            if (Find(p) is { } n) n.Visible = Eq(p, name);
    }

    public void SetVisible(string path, bool visible)
    {
        if (Find(path) is { } n) n.Visible = visible;
    }
}
