using Godot;
using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>
/// Godot Control for one original UI window (WindowSimple, WindowMSButton,
/// WindowTextView, ...). Draws the Background/Foreground drawables with the stock
/// semantics (unscaled clipped texture, stretched region, 9-slice tiling, frame
/// sequence), its markup text with bitmap fonts, and implements WindowMSButton
/// states: Normal / MouseOver / Pushed / Disabled visuals, multi-state toggles,
/// radio groups (ButtonGroupID) and command lists dispatched through the screen's bus.
/// </summary>
public partial class UiNode : Control
{
    public UiWindow Window { get; }
    public UiScreen? Screen { get; set; }

    /// <summary>Index into <see cref="UiWindow.ButtonStates"/> (logical state).</summary>
    public int State { get; private set; }

    bool _hover, _pressed;
    string? _textOverride;
    readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    BitmapTextLayout? _textLayout;
    string? _textLayoutKey;
    double _time;

    public bool IsButton => Window.Type == "WindowMSButton";

    public UiNode(UiWindow window)
    {
        Window = window;
        Name = string.IsNullOrEmpty(window.Name) ? window.Type : window.Name;
        Visible = window.Visible;
        TextureFilter = TextureFilterEnum.ParentNode;
        MouseFilter = IsButton ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        if (window.ButtonStates.Count > 0)
            State = Math.Clamp((int?)window.RawInstance?.Element("State") ?? 0, 0, window.ButtonStates.Count - 1);
        if (window.TooltipPath != null && UiResources.Instance.Text(window.TooltipPath) is { Length: > 0 } tip)
        {
            TooltipText = UiResources.StripMarkup(tip);
            _tooltipMarkup = tip.Trim();
            if (MouseFilter == MouseFilterEnum.Ignore) MouseFilter = MouseFilterEnum.Pass;
        }
        SetProcess(HasFrameSequence());
        if (UsesAdditive())
        {
            _additiveLayer = new AdditiveLayer { Name = "_Additive", DrawCallback = DrawAdditive };
            AddChild(_additiveLayer);
        }
    }

    bool HasFrameSequence() =>
        Window.Background?.Kind == UiDrawableKind.FrameSequence || Window.Foreground?.Kind == UiDrawableKind.FrameSequence;

    public bool Enabled
    {
        get => Window.Enabled;
        set { Window.Enabled = value; QueueRedraw(); }
    }

    /// <summary>Replaces the window's text markup (format prefix from the visual state is still applied).</summary>
    string? _tooltipMarkup;

    /// <summary>Tooltips use the stock TextTooltip window and delay (StockTooltip), not Godot's plain panel.</summary>
    public override GodotObject? _MakeCustomTooltip(string forText)
    {
        StockTooltip.EnsureInit();
        return StockTooltip.Build(_tooltipMarkup ?? forText, Screen?.UiScale ?? 1f);
    }

    public void SetText(string? markup) { _textOverride = markup; InvalidateText(); if (ResizesOnText) Screen?.QueueRelayout(); }

    /// <summary>Replaces the tooltip markup (code-chosen tooltips such as the retreat button's refusal reasons).</summary>
    public void SetTooltip(string? markup)
    {
        _tooltipMarkup = markup?.Trim();
        TooltipText = markup == null ? "" : UiResources.StripMarkup(markup);
        if (markup != null && MouseFilter == MouseFilterEnum.Ignore) MouseFilter = MouseFilterEnum.Pass;
    }

    /// <summary>Sets a &lt;value=name&gt; placeholder used by this window's text.</summary>
    public void SetValue(string name, string value) { _values[name] = value; InvalidateText(); if (ResizesOnText) Screen?.QueueRelayout(); }

    public void SetState(int state, bool fireCommands = false)
    {
        if (Window.ButtonStates.Count == 0) return;
        State = Math.Clamp(state, 0, Window.ButtonStates.Count - 1);
        InvalidateText();
        Fire(Window.ButtonStates[State].OnVisualEnter);
        if (fireCommands) Fire(Window.ButtonStates[State].OnEnter);
    }

    void InvalidateText() { _textLayout = null; QueueRedraw(); }

    string SubState =>
        !Window.Enabled ? "Disabled" : _pressed && _hover ? "Pushed" : _hover ? "MouseOver" : "Normal";

    UiVisualState? CurrentVisual()
    {
        if (Window.ButtonStates.Count == 0) return null;
        var visuals = Window.ButtonStates[Math.Min(State, Window.ButtonStates.Count - 1)].Visuals;
        visuals.TryGetValue("Normal", out var normal);
        if (!visuals.TryGetValue(SubState, out var v) && SubState == "Pushed") visuals.TryGetValue("MouseOver", out v);
        if (v == null || v == normal) return normal;
        // Sub-states leave out whatever they don't change; inherit it from the state's Normal visual.
        return new UiVisualState
        {
            Background = v.Background ?? normal?.Background,
            Foreground = v.Foreground ?? normal?.Foreground,
            TextFormatPath = v.TextFormatPath ?? normal?.TextFormatPath,
        };
    }

    public override void _Process(double delta)
    {
        _time += delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var visual = CurrentVisual();
        var rect = new Rect2(Vector2.Zero, Size);
        foreach (var d in new[] { visual?.Background ?? Window.Background, visual?.Foreground ?? Window.Foreground })
            if (d != null && (_additiveLayer == null || !UiResources.Instance.IsAdditive(d.TexturePath)))
                DrawDrawable(this, d, rect);
        DrawText(visual);
        _additiveLayer?.QueueRedraw();
    }

    // Additive drawables are drawn by a first child with an additive blend material.
    readonly Control? _additiveLayer;

    bool UsesAdditive()
    {
        var all = new List<UiDrawable?> { Window.Background, Window.Foreground };
        foreach (var bs in Window.ButtonStates)
            foreach (var v in bs.Visuals.Values) { all.Add(v.Background); all.Add(v.Foreground); }
        return all.Any(d => d != null && UiResources.Instance.IsAdditive(d.TexturePath));
    }

    internal void DrawAdditive(CanvasItem layer)
    {
        var visual = CurrentVisual();
        var rect = new Rect2(Vector2.Zero, Size);
        foreach (var d in new[] { visual?.Background ?? Window.Background, visual?.Foreground ?? Window.Foreground })
            if (d != null && UiResources.Instance.IsAdditive(d.TexturePath)) DrawDrawable(layer, d, rect);
    }

    void DrawDrawable(CanvasItem canvas, UiDrawable? d, Rect2 r)
    {
        if (d == null) return;
        var tex = UiResources.Instance.Texture(d.DdsPath);
        if (tex == null) return;
        var color = UiResources.ToColor(d.Color);
        int tw = d.TextureSize.X > 0 ? d.TextureSize.X : tex.GetWidth();
        int th = d.TextureSize.Y > 0 ? d.TextureSize.Y : tex.GetHeight();
        switch (d.Kind)
        {
            case UiDrawableKind.SimpleTexture:
            {
                // Unscaled, aligned inside the window and clipped to it.
                float x = d.TextureAlignX switch { UiAlign.HighEnd => r.End.X - tw, UiAlign.Center => (r.Size.X - tw) / 2, _ => 0 };
                float y = d.TextureAlignY switch { UiAlign.HighEnd => r.End.Y - th, UiAlign.Center => (r.Size.Y - th) / 2, _ => 0 };
                float cw = Math.Min(tw, r.End.X - x), ch = Math.Min(th, r.End.Y - y);
                if (cw > 0 && ch > 0)
                    canvas.DrawTextureRectRegion(tex, new Rect2(x, y, cw, ch), new Rect2(0, 0, cw, ch), color);
                break;
            }
            case UiDrawableKind.ScalingTexture:
            {
                int sw = d.ScaleSourceSize.X > 0 ? d.ScaleSourceSize.X : tw, sh = d.ScaleSourceSize.Y > 0 ? d.ScaleSourceSize.Y : th;
                canvas.DrawTextureRectRegion(tex, r, new Rect2(0, 0, sw, sh), color);
                break;
            }
            case UiDrawableKind.TiledTexture:
                DrawNineSlice(canvas, tex, d, r, color);
                break;
            case UiDrawableKind.FrameSequence:
            {
                int fw = d.FrameSize.X > 0 ? d.FrameSize.X : tw, fh = d.FrameSize.Y > 0 ? d.FrameSize.Y : th;
                int count = Math.Max(1, d.FrameCount > 0 ? d.FrameCount : d.FrameCountX * Math.Max(1, d.FrameCountY));
                int frame = d.FrameTimeMs > 0 ? (int)(_time * 1000 / d.FrameTimeMs) % count : 0;
                int cols = Math.Max(1, d.FrameCountX);
                var src = new Rect2(frame % cols * fw, frame / cols * fh, fw, fh);
                canvas.DrawTextureRectRegion(tex, r.Size.X > 0 ? r : new Rect2(0, 0, fw, fh), src, color);
                break;
            }
        }
    }

    static void DrawNineSlice(CanvasItem canvas, Texture2D tex, UiDrawable d, Rect2 r, Color color)
    {
        int Wd(string k) => d.Tiles.TryGetValue(k, out var t) ? t.Size.X : 0;
        int Ht(string k) => d.Tiles.TryGetValue(k, out var t) ? t.Size.Y : 0;
        float left = Math.Max(Wd("LT"), Wd("L")), right = Math.Max(Wd("RT"), Wd("R"));
        float top = Math.Max(Ht("LT"), Ht("T")), bottom = Math.Max(Ht("LB"), Ht("B"));
        float w = r.Size.X, h = r.Size.Y;
        var cells = new (string, Rect2)[]
        {
            ("F", new(left, top, w - left - right, h - top - bottom)),
            ("T", new(left, 0, w - left - right, top)),
            ("B", new(left, h - bottom, w - left - right, bottom)),
            ("L", new(0, top, left, h - top - bottom)),
            ("R", new(w - right, top, right, h - top - bottom)),
            ("LT", new(0, 0, left, top)),
            ("RT", new(w - right, 0, right, top)),
            ("LB", new(0, h - bottom, left, bottom)),
            ("RB", new(w - right, h - bottom, right, bottom)),
        };
        foreach (var (k, cell) in cells)
        {
            if (!d.Tiles.TryGetValue(k, out var part) || cell.Size.X <= 0 || cell.Size.Y <= 0) continue;
            var s = part.Source;
            if (s.W <= 0 || s.H <= 0) continue;
            // Edges and fill repeat the source region; corners are drawn once.
            for (float y = cell.Position.Y; y < cell.End.Y; y += s.H)
            for (float x = cell.Position.X; x < cell.End.X; x += s.W)
            {
                float cw = Math.Min(s.W, cell.End.X - x), ch = Math.Min(s.H, cell.End.Y - y);
                canvas.DrawTextureRectRegion(tex, new Rect2(x, y, cw, ch), new Rect2(s.X, s.Y, cw, ch), color);
            }
        }
    }

    string CurrentMarkup(UiVisualState? visual)
    {
        var t = Window.Text;
        if (t == null && _textOverride == null) return "";
        string body = _textOverride ?? t?.Raw ?? "";
        string format = t?.Format ?? UiResources.Instance.Text(visual?.TextFormatPath) ?? "";
        return format + body;
    }

    /// <summary>WindowTextView ResizeOnTextSet: the window takes its text's height (and width if ResizeWidthOnTextSet).</summary>
    public bool ResizesOnText => (bool?)Window.RawInstance?.Element("ResizeOnTextSet") == true;
    public bool ResizesWidthOnText => (bool?)Window.RawInstance?.Element("ResizeWidthOnTextSet") == true;

    /// <summary>Size of the laid-out text when wrapped to <paramref name="width"/> (0 = no wrapping).</summary>
    public Vector2 MeasureText(float width)
    {
        var markup = CurrentMarkup(CurrentVisual());
        if (markup.Length == 0) return Vector2.Zero;
        if (width > 0) return BitmapTextLayout.Block(markup, _values, width, scale: Screen?.UiScale).Size;
        var line = BitmapTextLayout.Block(markup, _values, 100000, wrap: false, scale: Screen?.UiScale);
        return new Vector2(line.MaxLineWidth, line.Size.Y);
    }

    void DrawText(UiVisualState? visual)
    {
        var t = Window.Text;
        string markup = CurrentMarkup(visual);
        if (markup.Length == 0) return;
        var key = markup + "|" + Size + "|" + string.Join(";", _values.Select(kv => kv.Key + "=" + kv.Value));
        if (_textLayout == null || _textLayoutKey != key)
        {
            // The exe wraps the text at the full window width and places the block by the text placement's
            // set alignments only (UiLayout.PlaceText, H5_Game.exe 00a4d7b0), in window-local space.
            _textLayout = BitmapTextLayout.Block(markup, _values, Size.X, scale: Screen?.UiScale);
            var (tx, ty) = UiLayout.PlaceText(t?.Placement, new UiRect(0, 0, (int)Size.X, (int)Size.Y),
                (int)_textLayout.Size.X, (int)_textLayout.Size.Y);
            _textLayout.At(new Vector2(tx, ty));
            _textLayoutKey = key;
        }
        _textLayout.Draw(this);
    }

    // ---- Button behaviour ----

    public override void _Notification(int what)
    {
        switch ((long)what)
        {
            case NotificationMouseEnter when IsButton:
                _hover = true; QueueRedraw();
                if (Window.Enabled) UiAudio.Hover?.Invoke(this);
                break;
            case NotificationMouseExit when IsButton:
                _hover = false; QueueRedraw();
                LeavePushed();
                break;
            case NotificationResized:
                InvalidateText();
                break;
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (!IsButton || !Window.Enabled || e is not InputEventMouseButton mb) return;
        if (mb.ButtonIndex == MouseButton.Left)
        {
            bool onPush = Window.TriggerMode == "BCST_ON_PUSH";
            if (mb.DoubleClick && Window.ButtonStates.Count > 0) { Fire(Window.ButtonStates[State].OnDoubleClick); AcceptEvent(); return; }
            if (mb.Pressed)
            {
                _pressed = true; QueueRedraw();
                EnterPushed();
                if (onPush) Activate();
            }
            else if (_pressed)
            {
                _pressed = false; QueueRedraw();
                LeavePushed();
                if (!onPush && _hover) Activate();
            }
            AcceptEvent();
        }
        else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed && Window.ButtonStates.Count > 0)
        {
            Fire(Window.ButtonStates[State].OnRightClick);
            AcceptEvent();
        }
    }

    // Visual sub-state commands (OnEnterSubState of "Pushed", e.g. the ATB scroll buttons' UISRunReaction):
    // entering the pushed look runs them, leaving it (release or mouse exit) runs the reversable back reactions.
    bool _inPushed;

    void EnterPushed()
    {
        if (_inPushed || State >= Window.ButtonStates.Count || !Window.ButtonStates[State].Visuals.TryGetValue("Pushed", out var v)) return;
        _inPushed = true;
        Fire(v.OnEnter);
    }

    void LeavePushed()
    {
        if (!_inPushed) return;
        _inPushed = false;
        if (State < Window.ButtonStates.Count && Window.ButtonStates[State].Visuals.TryGetValue("Pushed", out var v)) Fire(v.OnLeave);
    }

    /// <summary>Simulates a click: advances multi-state buttons and runs the entered state's commands.</summary>
    public void Activate()
    {
        UiAudio.Click?.Invoke(this);
        if (Window.ButtonStates.Count > 1)
        {
            int next = (State + 1) % Window.ButtonStates.Count;
            if (Window.ButtonGroupId != 0)
            {
                // Radio group: the clicked button becomes active, siblings reset silently.
                if (State != 0) return;
                foreach (var sib in GetParent().GetChildren().OfType<UiNode>())
                    if (sib != this && sib.Window.ButtonGroupId == Window.ButtonGroupId) sib.SetState(0);
                next = 1;
            }
            SetState(next, fireCommands: true);
        }
        else if (Window.ButtonStates.Count == 1)
        {
            Fire(Window.ButtonStates[0].OnEnter);
        }
        Clicked?.Invoke(this);
    }

    /// <summary>Raised after a click was processed (for code-driven buttons without commands).</summary>
    public event Action<UiNode>? Clicked;

    void Fire(List<UiCommand> commands)
    {
        foreach (var c in commands)
            (Screen?.Bus ?? UiMessageBus.Global).Dispatch(c, this);
    }
}

/// <summary>
/// Full-rect child drawn with additive blending, for textures exported as
/// CONVERT_TRANSPARENT_ADD (alpha 0, colour added to what is behind).
/// </summary>
public partial class AdditiveLayer : Control
{
    public Action<CanvasItem>? DrawCallback;

    public AdditiveLayer()
    {
        Material = UiResources.AdditiveMaterial;
        MouseFilter = MouseFilterEnum.Ignore;
        AnchorRight = 1;
        AnchorBottom = 1;
        TextureFilter = TextureFilterEnum.ParentNode;
    }

    public override void _Draw() => DrawCallback?.Invoke(this);
}
