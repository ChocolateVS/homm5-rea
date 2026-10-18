using Godot;
using H5.Core.Ui;
using H5Reborn.Ui.Net;

namespace H5Reborn.Ui.Screens;

/// <summary>
/// The MPDMWait presets table: rows of 8 hero icons built from the screen's own
/// 'line_template' window (IconBack frame, 64x64 face, Selection64x64 marker),
/// scrolled by the mouse wheel. The local player's pick uses the stock (additive)
/// selection frame; the opponent's pick is shown with the same frame tinted red.
/// </summary>
public partial class PresetGridView : Control
{
    const int Columns = 8;
    readonly UiDrawable? _frame, _selection;
    readonly Vector2 _cell, _faceOffset, _faceSize;
    readonly int _visibleRows;
    int _scroll;

    public int? Mine { get; set; }
    public int? Theirs { get; set; }
    public event Action<int>? Picked;

    public PresetGridView(UiWindow template, float visibleHeight)
    {
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
