using Godot;
using H5.Core.Ui;
using H5Reborn.Ui.Net;

namespace H5Reborn.Ui.Screens;

/// <summary>
/// The MPDMWait presets table: rows of 8 hero icons built from the screen's own
/// 'line_template' window (IconBack frame, 64x64 face, Selection64x64 marker),
/// scrolled by the screen's table_slider bar like the exe (00742910 / 00742230 / 00742350): SetRange(0, 100, 10),
/// special positions = rows − 1 (0 when that is not positive), SetIndex(top row), the bar enabled only when
/// rows − 1 &gt; 1; the bar's index is the top row and the lines show the presets from top × columns. The local player's pick uses the stock (additive)
/// selection frame; the opponent's pick is shown with the same frame tinted red.
/// </summary>
public partial class PresetGridView : Control
{
    const int Columns = 8;
    readonly UiDrawable? _frame, _selection;
    readonly Vector2 _cell, _faceOffset, _faceSize;
    readonly int _visibleRows;
    readonly UiScrollBar? _bar;
    int _scroll;

    public int? Mine { get; set; }
    public int? Theirs { get; set; }
    public event Action<int>? Picked;

    public PresetGridView(UiWindow template, float visibleHeight, UiScrollBar? bar = null)
    {
        _bar = bar;
        var icon0 = template.Find("icon_0")!;
        var icon1 = template.Find("icon_1")!;
        var face = icon0.Find("hero_icon")!;
        _frame = icon0.Background;
        _selection = icon0.Find("hero_selection")?.Background;
        _cell = new Vector2(icon1.Rect.X - icon0.Rect.X, icon0.Rect.H);
        _faceOffset = new Vector2(face.Rect.X - icon0.Rect.X, face.Rect.Y - icon0.Rect.Y);
        _faceSize = new Vector2(face.Rect.W, face.Rect.H);
        _visibleRows = Math.Max(1, (int)(visibleHeight / _cell.Y));
        Size = new Vector2(_cell.X * Columns, _cell.Y * _visibleRows);
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.Linear;
        ClipContents = true;
        // The selection frame texture is additive (CONVERT_TRANSPARENT_ADD).
        _selectionLayer = new AdditiveLayer { DrawCallback = DrawSelection };
        AddChild(_selectionLayer);
        if (_bar != null)
        {
            // 00742230: range 0..100 page 10, rows − 1 special positions, index = top row, enabled when rows − 1 > 1.
            int n = Math.Max(0, Rows - 1);
            _bar.SetRange(0, 100, 10);
            _bar.Model.SetSpecialPositions(n);
            _bar.Model.SetIndex(_scroll);
            _bar.SetValue(_bar.Model.Pos);
            _bar.Enabled = n > 1;
            // 00742350: the listener takes the bar's index as the top row and refills the lines.
            _bar.PositionChanged += i => { _scroll = Math.Max(0, i); QueueRedraw(); };
        }
    }

    readonly AdditiveLayer _selectionLayer;

    void DrawSelection(CanvasItem canvas)
    {
        if (UiResources.Instance.Texture(_selection?.DdsPath) is not { } sel) return;
        foreach (var (index, tint) in new[] { (Theirs, new Color(1, 0.3f, 0.25f)), (Mine, Colors.White) })
        {
            if (index is not { } i) continue;
            int row = i / Columns - _scroll, col = i % Columns;
            if (row < 0 || row >= _visibleRows) continue;
            canvas.DrawTexture(sel, new Vector2(col * _cell.X, row * _cell.Y) + _faceOffset, tint);
        }
    }

    int Rows => (DuelPresetList.All.Count + Columns - 1) / Columns;

    public override void _Draw()
    {
        var res = UiResources.Instance;
        var frame = res.Texture(_frame?.DdsPath);
        var presets = DuelPresetList.All;
        for (int i = _scroll * Columns; i < presets.Count; i++)
        {
            int row = i / Columns - _scroll, col = i % Columns;
            if (row >= _visibleRows) break;
            var origin = new Vector2(col * _cell.X, row * _cell.Y);
            if (frame != null) DrawTexture(frame, origin);
            if (res.Texture(presets[i].RoundedFace ?? presets[i].LeftFace) is { } faceTex)
                DrawTextureRect(faceTex, new Rect2(origin + _faceOffset, _faceSize), false);
        }
        _selectionLayer.QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } mb) return;
        switch (mb.ButtonIndex)
        {
            case MouseButton.WheelDown or MouseButton.WheelUp when _bar != null:
                _bar.Wheel(UiScrollBar.WheelNotches(mb) ?? 0);
                break;
            case MouseButton.WheelDown: _scroll = Math.Min(_scroll + 1, Math.Max(0, Rows - _visibleRows)); break;
            case MouseButton.WheelUp: _scroll = Math.Max(0, _scroll - 1); break;
            case MouseButton.Left:
                int col = (int)(mb.Position.X / _cell.X), row = (int)(mb.Position.Y / _cell.Y) + _scroll;
                int index = row * Columns + col;
                if (col < Columns && index < DuelPresetList.All.Count) Picked?.Invoke(index);
                break;
            default: return;
        }
        QueueRedraw();
        AcceptEvent();
    }

    public void Refresh() => QueueRedraw();

    public override string _GetTooltip(Vector2 atPosition)
    {
        int col = (int)(atPosition.X / _cell.X), row = (int)(atPosition.Y / _cell.Y) + _scroll;
        int index = row * Columns + col;
        if (col >= Columns || index >= DuelPresetList.All.Count) return "";
        var p = DuelPresetList.All[index];
        return $"{p.Name}\n{p.HeroName}, level {p.Level}";
    }
}
