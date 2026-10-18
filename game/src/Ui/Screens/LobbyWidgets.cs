using Godot;
using H5.Core.Ui;

namespace H5Reborn.Ui.Screens;

/// <summary>Chat/status log drawn in an original MPCommonChatLog area, newest line at the bottom.</summary>
public partial class ChatLogView : Control
{
    readonly List<string> _lines = new();
    readonly BitmapText _text = new() { VerticalAlign = UiAlign.HighEnd, AnchorRight = 1, AnchorBottom = 1 };

    public ChatLogView()
    {
        AnchorRight = 1;
        AnchorBottom = 1;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_text);
    }

    public void Add(string markupLine)
    {
        _lines.Add(markupLine);
        if (_lines.Count > 200) _lines.RemoveAt(0);
        _text.Markup = string.Join("<br>", _lines);
    }

    public void AddChat(string from, string text) => Add($"<body_bright>{Escape(from)}: <body>{Escape(text)}");
    public void AddSystem(string text) => Add($"<body><color=FFC8A050>{Escape(text)}");

    public static string Escape(string s) => s.Replace('<', '‹').Replace('>', '›');
}

/// <summary>
/// Rows of a WindowListCtrl drawn with the original fonts and text formats. Column
/// geometry is given in coordinates local to the host window; rows are clickable
/// (double click activates).
/// </summary>
public partial class ListRowsView : Control
{
    public readonly record struct Column(float X, float Width, string HeaderMarkup);

    readonly Column[] _columns;
    readonly float _headerY, _headerH, _rowsY, _rowH;
    List<string[]> _rows = new();
    int _selected = -1;

    public event Action<int>? RowSelected;
    public event Action<int>? RowActivated;
    public int Selected => _selected;

    public ListRowsView(Column[] columns, float headerY, float headerH, float rowsY, float rowH)
    {
        _columns = columns;
        _headerY = headerY; _headerH = headerH; _rowsY = rowsY; _rowH = rowH;
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    public void SetRows(List<string[]> rows)
    {
        _rows = rows;
        if (_selected >= rows.Count) _selected = -1;
        QueueRedraw();
    }

    public override void _Draw()
    {
        foreach (var c in _columns)
            BitmapTextLayout.Build(c.HeaderMarkup, null, new Rect2(c.X, _headerY, c.Width, _headerH), UiAlign.Center, wrap: false).Draw(this);
        for (int r = 0; r < _rows.Count; r++)
        {
            float y = _rowsY + r * _rowH;
            if (y + _rowH > Size.Y) break;
            if (r == _selected)
                DrawRect(new Rect2(_columns[0].X, y, _columns[^1].X + _columns[^1].Width - _columns[0].X, _rowH), new Color(1, 0.85f, 0.5f, 0.18f));
            for (int c = 0; c < _columns.Length && c < _rows[r].Length; c++)
                BitmapTextLayout.Build(_rows[r][c], null, new Rect2(_columns[c].X + 4, y, _columns[c].Width - 8, _rowH), UiAlign.Center, wrap: false).Draw(this);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb) return;
        int r = (int)((mb.Position.Y - _rowsY) / _rowH);
        if (mb.Position.Y < _rowsY || r < 0 || r >= _rows.Count) return;
        _selected = r;
        QueueRedraw();
        if (mb.DoubleClick) RowActivated?.Invoke(r);
        else RowSelected?.Invoke(r);
        AcceptEvent();
    }
}
