using Godot;
using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>
/// The original text tooltip: UI/Tooltips/Main.(TooltipContext).xdb names the window
/// (TextTooltip: tiled CommonAdvObjTooltip background, text.txt markup
/// "&lt;tooltip_default&gt;&lt;center&gt;&lt;value=tooltiptext&gt;"), the appear delay (300 ms)
/// and the single-line width (223 px) the text wraps at.
/// </summary>
public static class StockTooltip
{
    const string Context = "UI/Tooltips/Main.(TooltipContext).xdb";
    const string Window = "UI/Tooltips/TextTooltip/TextTooltip.(WindowTooltip).xdb";
    const string Shared = "UI/Tooltips/TextTooltip/TextTooltip.(WindowTooltipShared).xdb";
    static float _width = 223;
    static bool _init;
    // SWindowTooltipShared LowerBorder (+0xc8) / HigherBorder (+0xd0): TextTooltip 13,13 / 13,13.
    static Vector2 _lower = new(13, 13), _higher = new(13, 13);

    /// <summary>Applies the stock appear delay to Godot's tooltip timer (once).</summary>
    public static void EnsureInit()
    {
        if (_init) return;
        _init = true;
        try
        {
            var root = Game.Instance.Xdb.Load(Context).Root;
            if (int.TryParse((string?)root?.Element("AppearDelay"), out var ms))
                ProjectSettings.SetSetting("gui/timers/tooltip_delay_sec", ms / 1000.0);
            if (int.TryParse((string?)root?.Element("SingleLineWidth"), out var w) && w > 0) _width = w;
            var shared = Game.Instance.Xdb.Load(Shared).Root;
            static Vector2 V(System.Xml.Linq.XElement? e, Vector2 d) =>
                e != null && float.TryParse((string?)e.Element("x"), out var x) && float.TryParse((string?)e.Element("y"), out var y) ? new Vector2(x, y) : d;
            _lower = V(shared?.Element("LowerBorder"), _lower);
            _higher = V(shared?.Element("HigherBorder"), _higher);
        }
        catch (Exception e) { GD.PushWarning($"[H5] tooltip context: {e.Message}"); }
    }

    public static Control Build(string tooltipMarkup, float uiScale)
    {
        string template = (UiResources.Instance.Text("UI/Tooltips/TextTooltip/text.txt") ?? "<value=tooltiptext>").Trim();
        string markup = template.Replace("<value=tooltiptext>", tooltipMarkup);
        // Text up to SingleLineWidth stays on one line and the tooltip shrinks to it; longer text wraps at that width.
        var single = BitmapTextLayout.Build(markup, null, new Rect2(0, 0, 4000, 2000), UiAlign.LowEnd, wrap: false);
        bool wraps = single.MaxLineWidth > _width;
        // Single-line text is drawn unwrapped: wrapping at exactly the measured width could push
        // the last glyph (e.g. the closing quote of a <hotkey>) onto a clipped second line.
        float w = Math.Min(_width, Math.Max(24, MathF.Ceiling(single.MaxLineWidth) + 1));
        var layout = !wraps ? single : BitmapTextLayout.Build(markup, null, new Rect2(0, 0, _width, 2000), UiAlign.LowEnd, wrap: true);
        // exe 00b96c90: the text view sits at LowerBorder and the window is the text size + LowerBorder + HigherBorder
        // (both truncated, vfunc 0x58 flags 0xc).
        float h = MathF.Ceiling(layout.Size.Y);
        var size = new Vector2((int)(w + _lower.X + _higher.X), (int)(h + _lower.Y + _higher.Y));

        var screen = new UiScreen(Window) { AnchorRight = 0, AnchorBottom = 0, MouseFilter = Control.MouseFilterEnum.Ignore };
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = size * uiScale };
        holder.AddChild(screen);
        screen.Ready += () =>
        {
            if (screen.NodeOf(screen.Root) is { } rootNode)
            {
                rootNode.Size = size;
                rootNode.Scale = new Vector2(uiScale, uiScale);
                rootNode.Position = Vector2.Zero;
                foreach (var c in rootNode.GetChildren()) if (c is Control cc) cc.Visible = false;
                rootNode.AddChild(new BitmapText { Markup = markup, Wrap = wraps, Position = _lower, Size = new Vector2(w, h) });
            }
        };
        return holder;
    }
}
