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
        Root = UiResources.Instance.LoadWindow(screenXdb);
        Bus = new UiMessageBus(parentBus ?? UiMessageBus.Global);
        Name = System.IO.Path.GetFileNameWithoutExtension(screenXdb);
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
        // Design-size layout up front so screen code can read window rects while building.
        UiLayout.Apply(Root);
        AddChild(Build(Root));
    }

    UiNode Build(UiWindow w)
    {
        UiNode node = w.Type == "WindowEditLine" ? new UiEditLine(w) : new UiNode(w);
        node.Screen = this;
        _nodes[w] = node;
        foreach (var child in UiLayout.PaintOrder(w))
            node.AddChild(Build(child));
        return node;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) Relayout();
    }

    public override void _Ready() => Relayout();

    public void Relayout()
    {
        var size = Size;
        if (size.X <= 0 || size.Y <= 0) size = GetViewportRect().Size;
        UiLayout.Apply(Root, (int)size.X, (int)size.Y);
        Place(Root, new UiRect(0, 0, 0, 0));
    }

    void Place(UiWindow w, UiRect parent)
    {
        var node = _nodes[w];
        node.Position = new Vector2(w.Rect.X - parent.X, w.Rect.Y - parent.Y);
        node.Size = new Vector2(Math.Max(0, w.Rect.W), Math.Max(0, w.Rect.H));
        foreach (var c in w.Children) Place(c, w.Rect);
    }

    public UiNode Node(UiWindow w) => _nodes[w];

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
