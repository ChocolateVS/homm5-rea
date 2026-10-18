using Godot;
using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>
/// WindowComboBox: shows the selected item with the original fonts over the stock
/// combo texture; a click opens a drop-down list below it. The drop-down's own art
/// (a list control referenced from the combo's shared data) is not decoded yet, so
/// the list is drawn as a dark panel with the stock body font.
/// </summary>
public partial class UiComboBox : UiNode
{
    public List<string> Items { get; } = new();
    public int Selected { get; private set; } = -1;
    public event Action<int>? SelectionChanged;

    const float ItemHeight = 24, TextInset = 10;
    DropDown? _open;

    public UiComboBox(UiWindow w) : base(w)
    {
        MouseFilter = MouseFilterEnum.Stop;
    }

    public void SetItems(IEnumerable<string> items, int selected)
    {
        Items.Clear();
        Items.AddRange(items);
        Selected = Math.Clamp(selected, -1, Items.Count - 1);
        QueueRedraw();
    }

    public void Select(int index, bool notify = true)
    {
        if (index < 0 || index >= Items.Count) return;
        Selected = index;
        QueueRedraw();
        if (notify) SelectionChanged?.Invoke(index);
    }

    public override void _Draw()
    {
        base._Draw();
        if (Selected < 0 || Selected >= Items.Count) return;
        var markup = (Enabled ? "<body_bright>" : "<body>") + Escape(Items[Selected]);
        BitmapTextLayout.Build(markup, null, new Rect2(TextInset, 0, Size.X - TextInset * 2 - 24, Size.Y), UiAlign.Center, wrap: false).Draw(this);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } || !Enabled) return;
        AcceptEvent();
        if (_open != null) { Close(); return; }
        // The list belongs to the screen's root node so it shares the UI scale and draws on top.
        var host = Screen != null ? Screen.Node(Screen.Root) : (Control)GetParent();
        _open = new DropDown(this);
        host.AddChild(_open);
        _open.Position = host.GetGlobalTransform().AffineInverse() * GetGlobalPosition() + new Vector2(0, Size.Y);
        _open.Size = new Vector2(Size.X, ItemHeight * Items.Count + 6);
    }

    void Close()
    {
        _open?.QueueFree();
        _open = null;
    }

    static string Escape(string s) => s.Replace('<', '‹').Replace('>', '›');

    sealed partial class DropDown : Control
    {
        readonly UiComboBox _owner;
        int _hover = -1;

        public DropDown(UiComboBox owner)
        {
            _owner = owner;
            MouseFilter = MouseFilterEnum.Stop;
            TextureFilter = TextureFilterEnum.ParentNode;
            ZIndex = 100;
        }

        public override void _Draw()
        {
            var r = new Rect2(Vector2.Zero, Size);
            DrawRect(r, new Color(0.12f, 0.09f, 0.06f, 0.96f));
            DrawRect(r, new Color(0.62f, 0.45f, 0.24f), filled: false, width: 1);
            for (int i = 0; i < _owner.Items.Count; i++)
            {
                var row = new Rect2(3, 3 + i * ItemHeight, Size.X - 6, ItemHeight);
                if (i == _hover) DrawRect(row, new Color(1, 0.85f, 0.5f, 0.18f));
                var style = i == _owner.Selected ? "<body_bright><color=FFFFE6A0>" : "<body_bright>";
                BitmapTextLayout.Build(style + Escape(_owner.Items[i]), null, new Rect2(row.Position.X + TextInset - 3, row.Position.Y, row.Size.X, row.Size.Y), UiAlign.Center, wrap: false).Draw(this);
            }
        }

        int RowAt(Vector2 p) => p.X < 0 || p.X > Size.X ? -1 : (int)((p.Y - 3) / ItemHeight) is var i && i >= 0 && i < _owner.Items.Count ? i : -1;

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseMotion mm) { var h = RowAt(mm.Position); if (h != _hover) { _hover = h; QueueRedraw(); } }
            else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
            {
                var i = RowAt(mb.Position);
                _owner.Close();
                if (i >= 0) _owner.Select(i);
                AcceptEvent();
            }
        }

        public override void _Notification(int what)
        {
            if (what == NotificationMouseExit && _hover != -1) { _hover = -1; QueueRedraw(); }
        }

        public override void _UnhandledInput(InputEvent e)
        {
            // A click anywhere else closes the list, like the stock combo.
            if (e is InputEventMouseButton { Pressed: true }) _owner.Close();
        }
    }
}
