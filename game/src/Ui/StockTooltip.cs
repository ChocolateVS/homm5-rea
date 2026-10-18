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
    static float _width = 223;
    static bool _init;

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
        }
        catch (Exception e) { GD.PushWarning($"[H5] tooltip context: {e.Message}"); }
    }

    public static Control Build(string tooltipMarkup, float uiScale)
    {
        string template = (UiResources.Instance.Text("UI/Tooltips/TextTooltip/text.txt") ?? "<value=tooltiptext>").Trim();
        string markup = template.Replace("<value=tooltiptext>", tooltipMarkup);
        // Text up to SingleLineWidth stays on one line and the tooltip shrinks to it; longer text wraps at that width.
        var single = BitmapTextLayout.Build(markup, null, new Rect2(0, 0, 4000, 2000), UiAlign.LowEnd, wrap: false);
        float w = Math.Min(_width, Math.Max(24, single.Size.X));
        var layout = single.Size.X <= _width ? single : BitmapTextLayout.Build(markup, null, new Rect2(0, 0, _width, 2000), UiAlign.LowEnd, wrap: true);
        float h = Math.Max(16, layout.Size.Y) + 8;

        var screen = new UiScreen(Window) { AnchorRight = 0, AnchorBottom = 0, MouseFilter = Control.MouseFilterEnum.Ignore };
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(w + 8, h) * uiScale };
        holder.AddChild(screen);
        screen.Ready += () =>
        {
            if (screen.NodeOf(screen.Root) is { } rootNode)
            {
                rootNode.Size = new Vector2(w + 8, h);
                rootNode.Scale = new Vector2(uiScale, uiScale);
                rootNode.Position = Vector2.Zero;
                foreach (var c in rootNode.GetChildren()) if (c is Control cc) cc.Visible = false;
                rootNode.AddChild(new BitmapText { Markup = markup, Wrap = true, Position = new Vector2(4, 4), Size = new Vector2(w, h - 8) });
            }
        };
        return holder;
    }
}
