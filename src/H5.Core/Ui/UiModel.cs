using System.Xml.Linq;

namespace H5.Core.Ui;

public readonly record struct Int2(int X, int Y)
{
    public static readonly Int2 Zero = new(0, 0);
    public override string ToString() => $"{X},{Y}";
}

public readonly record struct UiRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public override string ToString() => $"[{X},{Y} {W}x{H}]";
}

/// <summary>
/// Axis anchoring from the xdb <c>HorAllign</c>/<c>VerAllign</c> fields
/// (EPA_* = "position alignment", ERA_CENTER is the same as EPA_CENTER).
/// </summary>
public enum UiAlign
{
    /// <summary>EPA_LOW_END: offset from the parent's left/top edge.</summary>
    LowEnd,
    /// <summary>EPA_HIGH_END: the window's right/bottom edge sits at parent's right/bottom + Position.</summary>
    HighEnd,
    /// <summary>EPA_CENTER / ERA_CENTER: centred in the parent, then offset by Position.</summary>
    Center,
    /// <summary>EPA_MARGIN: stretched to the parent minus LowerMargin (left/top) and UpperMargin (right/bottom).</summary>
    Margin,
}

/// <summary>
/// Placement block shared by every window type. Each field is an xdb
/// <c>&lt;First&gt;value&lt;/First&gt;&lt;Second&gt;bool&lt;/Second&gt;</c> pair where Second means "this value
/// is set here"; an instance's set fields override its Shared definition's.
/// </summary>
public sealed class UiPlacement
{
    public (Int2 Value, bool Set) Position, Size, LowerMargin, UpperMargin;
    public (UiAlign Value, bool Set) HorAlign, VerAlign;

    public static UiPlacement Parse(XElement? e)
    {
        var p = new UiPlacement();
        if (e == null) return p;
        p.Position = Vec(e.Element("Position"));
        p.Size = Vec(e.Element("Size"));
        p.LowerMargin = Vec(e.Element("LowerMargin"));
        p.UpperMargin = Vec(e.Element("UpperMargin"));
        p.HorAlign = Align(e.Element("HorAllign"));
        p.VerAlign = Align(e.Element("VerAllign"));
        return p;
    }

    /// <summary>Overlay <paramref name="over"/> (instance) on top of this (shared) placement.</summary>
    public UiPlacement MergedWith(UiPlacement? over)
    {
        if (over == null) return this;
        return new UiPlacement
        {
            Position = over.Position.Set ? over.Position : Position,
            Size = over.Size.Set ? over.Size : Size,
            LowerMargin = over.LowerMargin.Set ? over.LowerMargin : LowerMargin,
            UpperMargin = over.UpperMargin.Set ? over.UpperMargin : UpperMargin,
            HorAlign = over.HorAlign.Set ? over.HorAlign : HorAlign,
            VerAlign = over.VerAlign.Set ? over.VerAlign : VerAlign,
        };
    }

    static (Int2, bool) Vec(XElement? e)
    {
        if (e == null) return (Int2.Zero, false);
        var f = e.Element("First");
        return (new Int2((int?)f?.Element("x") ?? 0, (int?)f?.Element("y") ?? 0), H5.Core.Data.XmlHelpers.BoolValue(e.Element("Second")));
    }

    static (UiAlign, bool) Align(XElement? e)
    {
        if (e == null) return (UiAlign.LowEnd, false);
        var v = (string?)e.Element("First") switch
        {
            "EPA_HIGH_END" or "ERA_HIGH_END" => UiAlign.HighEnd,
            "EPA_CENTER" or "ERA_CENTER" => UiAlign.Center,
            "EPA_MARGIN" => UiAlign.Margin,
            _ => UiAlign.LowEnd,
        };
        return (v, H5.Core.Data.XmlHelpers.BoolValue(e.Element("Second")));
    }

    public override string ToString() =>
        $"pos={Position.Value} size={Size.Value} h={HorAlign.Value} v={VerAlign.Value} margins={LowerMargin.Value}/{UpperMargin.Value}";
}

public enum UiDrawableKind { SimpleTexture, ScalingTexture, TiledTexture, FrameSequence, Unknown }

/// <summary>One of the nine regions of a <c>BackgroundTiledTexture</c> (texture pixel rect in <c>Maps</c>).</summary>
public readonly record struct UiTilePart(Int2 Size, UiRect Source, int Rotate);

/// <summary>A window Background/Foreground image (the Background* xdb types).</summary>
public sealed class UiDrawable
{
    public UiDrawableKind Kind;
    public string SourceType = "";
    /// <summary>Pak path of the (Texture) xdb.</summary>
    public string? TexturePath;
    /// <summary>Pak path of the .dds resolved from the Texture's DestName.</summary>
    public string? DdsPath;
    /// <summary>Logical texture size from the Texture xdb (Width/Height).</summary>
    public Int2 TextureSize;
    /// <summary>ARGB tint; -1 (0xFFFFFFFF) is untinted.</summary>
    public uint Color = 0xFFFFFFFF;
    /// <summary>SimpleTexture: alignment of the unscaled texture inside the window.</summary>
    public UiAlign TextureAlignX, TextureAlignY;
    /// <summary>ScalingTexture: texture region (from 0,0) stretched over the window.</summary>
    public Int2 ScaleSourceSize;
    /// <summary>TiledTexture: corners LT,RT,LB,RB, edges T,L,R,B and fill F.</summary>
    public Dictionary<string, UiTilePart> Tiles = new();
    /// <summary>FrameSequence parameters.</summary>
    public Int2 FrameSize;
    public int FrameCountX, FrameCountY, FrameCount, FrameTimeMs, FadeInMs, FadeOutMs;
}

/// <summary>Visual for one button sub-state (Normal/MouseOver/Pushed/Disabled/RightButtonDown).</summary>
public sealed class UiVisualState
{
    public UiDrawable? Background, Foreground;
    public UiText? Text;
    public string? TextFormatPath;
    /// <summary>
    /// The sub-state has a TextFormat (00b91fb0): its placement replaces the text string's for the caption, and its
    /// format string replaces the text string's format when it is not empty.
    /// </summary>
    public bool HasTextFormat;
    public UiPlacement? TextPlacement;
    /// <summary>OnEnterSubState commands, run when the button enters this visual sub-state.</summary>
    public List<UiCommand> OnEnter = new();
    /// <summary>Reverse commands (Reversable sub-states, e.g. UISRunReaction szReactionBack), run on leaving it.</summary>
    public List<UiCommand> OnLeave = new();
}

/// <summary>A logical button state (WindowMSButton ButtonStates entry) and its UI commands.</summary>
public sealed class UiButtonState
{
    public string Name = "";
    public List<UiCommand> OnEnter = new(), OnRightClick = new(), OnDoubleClick = new();
    /// <summary>VisualOnEnterState of the shared visual state: run whenever the button shows this state.</summary>
    public List<UiCommand> OnVisualEnter = new();
    public Dictionary<string, UiVisualState> Visuals = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A UI reaction. Chains such as UISDirectRunReaction → ARSendGameMessage are flattened:
/// <see cref="Kind"/> is the leaf type and <see cref="Argument"/> its payload
/// (game message EventName, UI script name, sound path...).
/// </summary>
public sealed record UiCommand(string Kind, string Argument, int IntParam = 0, string? Source = null);

/// <summary>Text attached to a window: a markup string file plus an optional format/style file.</summary>
public sealed class UiText
{
    /// <summary>Pak path of the UTF-16 text file.</summary>
    public string? TextPath;
    /// <summary>Pak path of the format string (markup prefix such as "&lt;menu&gt;").</summary>
    public string? FormatPath;
    public UiPlacement? Placement;
    /// <summary>Resolved raw text (markup), if loaded.</summary>
    public string? Raw;
    public string? Format;
}

/// <summary>A node of a UI window tree with its Shared definition already merged in.</summary>
public sealed class UiWindow
{
    /// <summary>xdb element name, e.g. WindowSimple, WindowMSButton, WindowTextView, WindowScreen.</summary>
    public string Type = "";
    public string Name = "";
    public string SourcePath = "";
    public string? SharedPath;
    public UiPlacement Placement = new();
    public bool Visible = true, Enabled = true, Transparent;
    public int Priority;
    public string? TooltipPath;
    public UiDrawable? Background, Foreground;
    public UiText? Text;
    public List<UiButtonState> ButtonStates = new();
    public int ButtonGroupId;
    /// <summary>
    /// WindowMSButton <c>AutoChangeState</c> (instance field +0xfc, reader 00be4a60): a click first advances the
    /// button to its next state (00b92e00), then runs the commands of the state it is in.
    /// </summary>
    public bool AutoChangeState;
    public string? TriggerMode;
    public Dictionary<string, string> RelatedTexts = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> RelatedTextures = new(StringComparer.OrdinalIgnoreCase);
    public List<UiWindow> Children = new();
    /// <summary>
    /// Set on sub-windows a control builds from its Shared references (UiLoader.LoadParts): "ButtonLower",
    /// "ButtonGreater", "Slider", "Lever", "Border", "ScrollBar"; null for ordinary children.
    /// </summary>
    public string? Part;
    /// <summary>Instance and Shared elements for type-specific fields not modelled here.</summary>
    public XElement? RawInstance, RawShared;

    /// <summary>Computed by <see cref="UiLayout"/>.</summary>
    public UiRect Rect;

    public IEnumerable<UiWindow> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    /// <summary>First descendant with this name; control parts (<see cref="Part"/>) only when no ordinary window matches.</summary>
    public UiWindow? Find(string name) =>
        Descendants().Where(w => !w.IsPartOrInside).FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Descendants().FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Scrollable-container item list (008888e0): when set, the windows of <see cref="StackItems"/> (the
    /// container's item vector +0x160, filled only by its add-item slot) are stacked along the axis in list order,
    /// each at its own height, with this gap between them; <see cref="StackedSize"/> is the total. Other children
    /// of the window (hidden templates, markers) are not items and keep their own placement; the exe does not
    /// test an item's visibility.
    /// </summary>
    public int? StackInterval;
    public readonly List<UiWindow> StackItems = new();
    public bool StackHorizontal;
    public int StackedSize;

    /// <summary>Set by the loader on parts and everything under them.</summary>
    public bool IsPartOrInside;

    public override string ToString() => $"{Type} '{Name}' {Rect}";
}
