using System.Xml.Linq;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Core.Ui;

/// <summary>
/// Loads UI window trees from UI/**.xdb. Every window is an instance xdb
/// (WindowSimple, WindowMSButton, ...) pointing at a "*Shared" xdb holding its
/// children, background and default placement; the instance overrides placement
/// fields whose Second flag is true. Hrefs are relative to the referencing file.
/// </summary>
public sealed class UiLoader
{
    readonly XdbLoader _xdb;
    readonly PakFileSystem _fs;

    public UiLoader(XdbLoader xdb) { _xdb = xdb; _fs = xdb.FileSystem; }

    public bool LoadTexts { get; set; } = true;

    public UiWindow Load(string path) => LoadWindow(PakFileSystem.Normalize(path), _xdb.Load(path).Root!, 0);

    UiWindow LoadWindow(string file, XElement inst, int depth)
    {
        if (depth > 64) throw new InvalidDataException($"UI nesting too deep at {file}");
        var w = new UiWindow
        {
            Type = inst.Name.LocalName,
            Name = (string?)inst.Element("Name") ?? "",
            SourcePath = file,
            RawInstance = inst,
            Visible = (bool?)inst.Element("Visible") ?? true,
            Enabled = (bool?)inst.Element("Enabled") ?? true,
            Priority = (int?)inst.Element("Priority") ?? 0,
            TooltipPath = Path(inst.Element("TooltipFileRef"), file),
            ButtonGroupId = (int?)inst.Element("ButtonGroupID") ?? 0,
        };

        var sharedRef = inst.Element("Shared");
        XElement? shared = null;
        string sharedFile = file;
        if (sharedRef != null)
        {
            var r = XdbRef.Parse((string?)sharedRef.Attribute("href"), file);
            if (!r.IsEmpty && _fs.Exists(r.Path)) { shared = _xdb.Load(r.Path).Root; sharedFile = r.Path; w.SharedPath = r.Path; }
            else if (sharedRef.HasElements) shared = sharedRef.Elements().First();
        }
        w.RawShared = shared;

        var sharedPlacement = UiPlacement.Parse(shared?.Element("Placement"));
        w.Placement = sharedPlacement.MergedWith(UiPlacement.Parse(inst.Element("Placement")));
        if (shared != null)
        {
            w.Transparent = (bool?)shared.Element("Flags")?.Element("Transparent") ?? false;
            w.Background = LoadDrawable(shared.Element("Background"), sharedFile);
            w.Foreground = LoadDrawable(shared.Element("Foreground"), sharedFile);
            w.TriggerMode = (string?)shared.Element("TriggerMode");
            ParseVisualStates(w, shared, sharedFile);
        }

        w.Text = LoadText(inst, file);
        ParseButtonStates(w, inst, file);
        ReadRelated(w, inst.Element("relatedTexts"), file, "texts", "TextName", "text", w.RelatedTexts);
        ReadRelated(w, inst.Element("relatedTextures"), file, "textures", "TextureName", "Texture", w.RelatedTextures);

        foreach (var item in shared?.Element("Children")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var child = ResolveChild(item, sharedFile, out var childFile);
            if (child != null) w.Children.Add(LoadWindow(childFile, child, depth + 1));
        }
        return w;
    }

    XElement? ResolveChild(XElement item, string currentFile, out string childFile)
    {
        childFile = currentFile;
        var href = (string?)item.Attribute("href");
        if (string.IsNullOrEmpty(href)) return item.Elements().FirstOrDefault();
        if (href.StartsWith("#n:inline")) return item.Elements().FirstOrDefault();
        var r = XdbRef.Parse(href, currentFile);
        if (r.IsEmpty || !_fs.Exists(r.Path)) return null;
        childFile = r.Path;
        return _xdb.Load(r.Path).Root;
    }

    string? Path(XElement? refEl, string file)
    {
        var href = (string?)refEl?.Attribute("href");
        if (string.IsNullOrEmpty(href)) return null;
        var r = XdbRef.Parse(href, file);
        return r.IsEmpty ? null : r.Path;
    }

    (XElement? el, string file) Deref(XElement? refEl, string file)
    {
        if (refEl == null) return (null, file);
        var href = (string?)refEl.Attribute("href");
        if (string.IsNullOrEmpty(href) || href.StartsWith("#n:inline"))
            return (refEl.Elements().FirstOrDefault(), file);
        var r = XdbRef.Parse(href, file);
        return !r.IsEmpty && _fs.Exists(r.Path) ? (_xdb.Load(r.Path).Root, r.Path) : (null, file);
    }

    public UiDrawable? LoadDrawable(XElement? refEl, string file)
    {
        var (e, f) = Deref(refEl, file);
        if (e == null) return null;
        var d = new UiDrawable
        {
            SourceType = e.Name.LocalName,
            Kind = e.Name.LocalName switch
            {
                "BackgroundSimpleTexture" => UiDrawableKind.SimpleTexture,
                "BackgroundSimpleScallingTexture" => UiDrawableKind.ScalingTexture,
                "BackgroundTiledTexture" => UiDrawableKind.TiledTexture,
                "BackgroundFrameSequence" => UiDrawableKind.FrameSequence,
                _ => UiDrawableKind.Unknown,
            },
            Color = unchecked((uint)((int?)e.Element("Color") ?? -1)),
        };
        d.TexturePath = Path(e.Element("Texture"), f);
        if (d.TexturePath != null && _fs.Exists(d.TexturePath))
        {
            var tex = _xdb.Load(d.TexturePath).Root!;
            d.TextureSize = new Int2((int?)tex.Element("Width") ?? 0, (int?)tex.Element("Height") ?? 0);
            d.DdsPath = Path(tex.Element("DestName"), d.TexturePath);
        }
        d.TextureAlignX = AlignOf((string?)e.Element("TextureX"));
        d.TextureAlignY = AlignOf((string?)e.Element("TextureY"));
        if (e.Element("Size") is { } sz) d.ScaleSourceSize = new Int2((int?)sz.Element("x") ?? 0, (int?)sz.Element("y") ?? 0);
        foreach (var part in new[] { "LT", "RT", "LB", "RB", "T", "L", "R", "B", "F" })
        {
            var pe = e.Element("r" + part);
            if (pe == null) continue;
            var size = new Int2((int?)pe.Element("Size")?.Element("x") ?? 0, (int?)pe.Element("Size")?.Element("y") ?? 0);
            var m = pe.Element("Maps");
            int x1 = (int?)m?.Element("x1") ?? 0, y1 = (int?)m?.Element("y1") ?? 0, x2 = (int?)m?.Element("x2") ?? 0, y2 = (int?)m?.Element("y2") ?? 0;
            if (size.X == 0 && size.Y == 0 && x2 == x1 && y2 == y1) continue;
            d.Tiles[part] = new UiTilePart(size, new UiRect(x1, y1, x2 - x1, y2 - y1), (int?)pe.Element("Rotate") ?? 0);
        }
        if (d.Kind == UiDrawableKind.FrameSequence)
        {
            d.FrameSize = new Int2((int?)e.Element("FrameSize")?.Element("x") ?? 0, (int?)e.Element("FrameSize")?.Element("y") ?? 0);
            d.FrameCountX = (int?)e.Element("FrameCountX") ?? 1;
            d.FrameCountY = (int?)e.Element("FrameCountY") ?? 1;
            d.FrameCount = (int?)e.Element("FrameCount") ?? 1;
            d.FrameTimeMs = (int?)e.Element("Time") ?? 0;
            d.FadeInMs = (int?)e.Element("TimeFadeIn") ?? 0;
            d.FadeOutMs = (int?)e.Element("TimeFadeOut") ?? 0;
        }
        return d;
    }

    static UiAlign AlignOf(string? v) => v switch
    {
        "EPA_HIGH_END" => UiAlign.HighEnd,
        "EPA_CENTER" or "ERA_CENTER" => UiAlign.Center,
        "EPA_MARGIN" => UiAlign.Margin,
        _ => UiAlign.LowEnd,
    };

    UiText? LoadText(XElement inst, string file)
    {
        string? textPath = Path(inst.Element("TextFileRef"), file);
        string? formatPath = null;
        UiPlacement? placement = null;

        // TextString → ForegroundTextString (instance: TextStringFileRef; shared: FormatStringFileRef + Position)
        var (fts, ftsFile) = Deref(inst.Element("TextString"), file);
        if (fts != null)
        {
            textPath ??= Path(fts.Element("TextStringFileRef"), ftsFile);
            var (sh, shFile) = Deref(fts.Element("Shared"), ftsFile);
            if (sh != null)
            {
                textPath ??= Path(sh.Element("TextStringFileRef"), shFile);
                formatPath = Path(sh.Element("FormatStringFileRef"), shFile);
                placement = UiPlacement.Parse(sh.Element("Position"));
            }
        }
        var (tf, tfFile) = Deref(inst.Element("TextFormat"), file);
        if (tf != null)
        {
            formatPath = Path(tf.Element("FormatStringFileRef"), tfFile) ?? formatPath;
            placement = UiPlacement.Parse(tf.Element("Placement"));
        }
        if (textPath == null && formatPath == null) return null;
        var t = new UiText { TextPath = textPath, FormatPath = formatPath, Placement = placement };
        if (LoadTexts)
        {
            t.Raw = textPath != null ? _xdb.ReadText("/" + textPath) : null;
            t.Format = formatPath != null ? _xdb.ReadText("/" + formatPath) : null;
        }
        return t;
    }

    void ParseButtonStates(UiWindow w, XElement inst, string file)
    {
        var states = inst.Element("ButtonStates")?.Elements("Item").ToList();
        if (states == null) return;
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            var bs = i < w.ButtonStates.Count ? w.ButtonStates[i] : AddState(w);
            bs.Name = (string?)s.Element("Name") ?? bs.Name;
            bs.OnEnter.AddRange(Commands(s.Element("CommandsOnEnterState"), file));
            bs.OnRightClick.AddRange(Commands(s.Element("CommandsOnRightClick"), file));
            bs.OnDoubleClick.AddRange(Commands(s.Element("commandsOnLDblKlick"), file));
        }
    }

    static UiButtonState AddState(UiWindow w) { var s = new UiButtonState(); w.ButtonStates.Add(s); return s; }

    void ParseVisualStates(UiWindow w, XElement shared, string file)
    {
        var vs = shared.Element("VisualStates")?.Elements("Item").ToList();
        if (vs == null) return;
        for (int i = 0; i < vs.Count; i++)
        {
            var bs = i < w.ButtonStates.Count ? w.ButtonStates[i] : AddState(w);
            foreach (var sub in vs[i].Elements())
            {
                if (sub.Element("Background") == null && sub.Element("TextFormat") == null) continue;
                var v = new UiVisualState
                {
                    Background = LoadDrawable(sub.Element("Background"), file),
                    Foreground = LoadDrawable(sub.Element("Foreground"), file),
                };
                var (tf, tfFile) = Deref(sub.Element("TextFormat"), file);
                if (tf != null) v.TextFormatPath = Path(tf.Element("FormatStringFileRef"), tfFile);
                var enterBlock = sub.Element("OnEnterSubState");
                v.OnEnter.AddRange(Commands(enterBlock, file));
                if ((string?)enterBlock?.Element("Reversable") == "true")
                    foreach (var item in enterBlock!.Element("Commands")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                        v.OnLeave.AddRange(ReverseReaction(item, file));
                bs.Visuals[sub.Name.LocalName] = v;
            }
        }
    }

    /// <summary>Flattens a &lt;Commands&gt; block (and nested reaction chains) to leaf commands.</summary>
    IEnumerable<UiCommand> Commands(XElement? block, string file)
    {
        foreach (var item in block?.Element("Commands")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            foreach (var c in Reaction(item, file, 0)) yield return c;
    }

    IEnumerable<UiCommand> Reaction(XElement refEl, string file, int depth)
    {
        var (e, f) = Deref(refEl, file);
        if (e == null || depth > 16) yield break;
        switch (e.Name.LocalName)
        {
            case "UISDirectRunReaction":
                foreach (var c in Reaction(e.Element("ReactionForward")!, f, depth + 1)) yield return c;
                break;
            case "ARSendGameMessage":
                yield return new UiCommand("GameMessage", (string?)e.Element("EventName") ?? "", (int?)e.Element("IntParam") ?? 0, f);
                break;
            case "UISRunReaction":
                yield return new UiCommand("RunReaction", (string?)e.Element("szReactionForward")?.Element("First") ?? "", 0, f);
                break;
            case "UISSendGameMessage":
            case "UISSendUIMessage":
                yield return new UiCommand(e.Name.LocalName, string.Join(" ", e.Elements().Skip(1).Select(x => x.Value)), 0, f);
                break;
            case "UISPlaySound":
                yield return new UiCommand("PlaySound", Path(e.Element("Sound"), f) ?? "", 0, f);
                break;
            default:
                yield return new UiCommand(e.Name.LocalName, "", 0, f);
                break;
        }
    }

    /// <summary>The reverse of a reversable reaction: UISRunReaction's szReactionBack.</summary>
    IEnumerable<UiCommand> ReverseReaction(XElement refEl, string file)
    {
        var (e, f) = Deref(refEl, file);
        if (e?.Name.LocalName == "UISRunReaction" && (string?)e.Element("szReactionBack")?.Element("First") is { Length: > 0 } back)
            yield return new UiCommand("RunReaction", back, 0, f);
    }

    void ReadRelated(UiWindow w, XElement? refEl, string file, string listName, string keyName, string valueName, Dictionary<string, string> into)
    {
        var (e, f) = Deref(refEl, file);
        foreach (var it in e?.Element(listName)?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var key = (string?)it.Element(keyName);
            var val = Path(it.Element(valueName), f);
            if (key != null && val != null) into[key] = val;
        }
    }
}
