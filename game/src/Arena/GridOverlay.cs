using Godot;

namespace H5Reborn.Arena;

/// <summary>
/// Cell states of the combat grid, numbered as H5_Game.exe's grid kinds (00f32ae4 user 0055c480):
/// 1 grid (combat_show_grid), 2 target (destination under the cursor), 3 select (active / carried stack),
/// 4 affected, 5 aimable, 6 passable (movement area), 7 passable target, 8 intersection (6 over 7 or 7 over 6).
/// </summary>
public enum CellMark { None = 0, Grid = 1, Target = 2, Select = 3, Affected = 4, Aimable = 5, Passable = 6, PassableTarget = 7, Intersection = 8 }

/// <summary>
/// Per-cell overlay quads draped on the terrain, textured with the original grid materials of
/// UI/CombatArena-Heavy/SelectionDesc (CombatSelectionDesc, reader 0051adc0) and faded per kind like the exe:
/// material by kind 0055b990, fade (alpha multiplier) by kind 0055c230, marking rules 0055bf10 / 0055ba00.
/// A stack of size ≥ 2 is drawn as one "Double" quad over its footprint (anchor = the exe's high corner);
/// the other cells of the footprint are hidden unless they carry a target / select / affected / aimable mark.
/// </summary>
public partial class GridOverlay : Node3D
{
    const string SelectionDescPath = "UI/CombatArena-Heavy/SelectionDesc.(CombatSelectionDesc).xdb";
    const int Subdiv = 4;
    const float Lift = 0.03f;
    // Quad half extents in cells: single ±1.09 units, double −3.09..+1.09 units around the anchor cell centre
    // (0055bb60; 2 world units per cell).
    const float SingleHalf = 1.09f / ArenaGrid.TileSize, DoubleLow = 3.09f / ArenaGrid.TileSize, DoubleHigh = 1.09f / ArenaGrid.TileSize;

    struct CellState { public CellMark Kind; public bool Double, Hidden; }

    readonly ArenaGrid _grid;
    readonly MeshInstance3D[,] _cells = new MeshInstance3D[ArenaGrid.Width, ArenaGrid.Height];
    readonly ArrayMesh?[,] _doubleMeshes = new ArrayMesh?[ArenaGrid.Width, ArenaGrid.Height];
    readonly ArrayMesh[,] _singleMeshes = new ArrayMesh[ArenaGrid.Width, ArenaGrid.Height];
    readonly CellState[,] _state = new CellState[ArenaGrid.Width, ArenaGrid.Height];
    readonly Dictionary<(string Field, float Fade), Material?> _materials = new();
    System.Xml.Linq.XElement? _desc;
    bool _showGrid;

    public GridOverlay(ArenaGrid grid)
    {
        _grid = grid;
        for (int x = 0; x < ArenaGrid.Width; x++)
        for (int y = 0; y < ArenaGrid.Height; y++)
        {
            _singleMeshes[x, y] = BuildMesh(x + 0.5f - SingleHalf, y + 0.5f - SingleHalf, x + 0.5f + SingleHalf, y + 0.5f + SingleHalf);
            var mi = new MeshInstance3D
            {
                Name = $"Cell_{x}_{y}",
                Mesh = _singleMeshes[x, y],
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            _cells[x, y] = mi;
            AddChild(mi);
        }
    }

    /// <summary>combat_show_grid: the base layer marks every cell with the grid kind (0055b870).</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; ClearMarks(); }
    }

    /// <summary>Base layer (0055b870): every cell back to the grid kind when the grid is shown, else empty.</summary>
    public void ClearMarks()
    {
        for (int x = 0; x < ArenaGrid.Width; x++)
        for (int y = 0; y < ArenaGrid.Height; y++)
        { _state[x, y] = new CellState { Kind = _showGrid ? CellMark.Grid : CellMark.None }; Refresh(x, y); }
    }

    /// <summary>One cell (0055bf10 with size 1): ignored while hidden under a double quad; passable / passable-target
    /// over the other of the two (or an intersection) becomes an intersection.</summary>
    public void SetMark(Vector2I cell, CellMark kind) => Mark(cell, 1, kind);

    /// <summary>
    /// 0055bf10: a footprint of <paramref name="size"/> cells with its lowest cell at <paramref name="low"/>. Size ≥ 2 sets
    /// the double flag on the anchor (the footprint's high corner) and hides the footprint's other cells whose kind is
    /// none / grid / passable / passable target / intersection.
    /// </summary>
    public void Mark(Vector2I low, int size, CellMark kind)
    {
        if (size < 2)
        {
            if (!ArenaGrid.InBounds(low)) return;
            ref var c = ref _state[low.X, low.Y];
            if (c.Hidden || c.Kind == kind) return;
            c.Kind = !c.Double && kind is CellMark.Passable or CellMark.PassableTarget
                     && c.Kind is CellMark.Intersection or CellMark.Passable or CellMark.PassableTarget ? CellMark.Intersection : kind;
            Refresh(low.X, low.Y);
            return;
        }
        var anchor = new Vector2I(low.X + size - 1, low.Y + size - 1);
        if (!ArenaGrid.InBounds(anchor)) return;
        ref var a = ref _state[anchor.X, anchor.Y];
        a.Hidden = false;
        a.Double = true;
        a.Kind = kind;
        for (int dx = 0; dx < size; dx++)
        for (int dy = 0; dy < size; dy++)
        {
            if (dx == 0 && dy == 0) continue;
            int x = anchor.X - dx, y = anchor.Y - dy;
            if (!ArenaGrid.InBounds(new Vector2I(x, y))) continue;
            ref var o = ref _state[x, y];
            o.Hidden = o.Kind is CellMark.None or CellMark.Grid or CellMark.Passable or CellMark.PassableTarget or CellMark.Intersection;
            if (o.Hidden) o.Kind = CellMark.None;
            Refresh(x, y);
        }
        Refresh(anchor.X, anchor.Y);
    }

    /// <summary>0055ba00 (movement areas): the cell takes the kind and is shown again; its double flag stays, and
    /// passable / passable target over the other of the two (or an intersection) on a single cell gives an intersection.</summary>
    public void MarkArea(Vector2I cell, CellMark kind)
    {
        if (!ArenaGrid.InBounds(cell)) return;
        ref var c = ref _state[cell.X, cell.Y];
        c.Hidden = false;
        if (c.Kind != kind)
            c.Kind = !c.Double && kind is CellMark.Passable or CellMark.PassableTarget
                     && c.Kind is CellMark.Intersection or CellMark.Passable or CellMark.PassableTarget ? CellMark.Intersection : kind;
        Refresh(cell.X, cell.Y);
    }

    void Refresh(int x, int y)
    {
        var st = _state[x, y];
        var mi = _cells[x, y];
        var mat = st.Hidden || st.Kind == CellMark.None ? null : MaterialFor(st.Kind, st.Double);
        mi.Visible = mat != null;
        if (mat == null) return;
        mi.MaterialOverride = mat;
        // 0055c7e0: the double quad (0055bb60(1)) only where the double flag is set.
        mi.Mesh = st.Double ? _doubleMeshes[x, y] ??= BuildMesh(x + 0.5f - DoubleLow, y + 0.5f - DoubleLow, x + 0.5f + DoubleHigh, y + 0.5f + DoubleHigh)
                            : _singleMeshes[x, y];
    }

    /// <summary>0055b990 (material by kind; Double variants for target / select / aimable on a double cell) and
    /// 0055c230 (fade by kind; 6 and 8 share PassableFadeValue).</summary>
    Material? MaterialFor(CellMark kind, bool dbl)
    {
        var (field, fadeField) = kind switch
        {
            CellMark.Grid => ("InactiveMaterial", "GridFadeValue"),
            CellMark.Target => (dbl ? "SelectionMaterialDouble" : "SelectionMaterial", "TargetFadeValue"),
            CellMark.Select => (dbl ? "SelectionMaterialDouble" : "SelectionMaterial", "SelectFadeValue"),
            CellMark.Affected => ("ActiveMaterial", "AffectedFadeValue"),
            CellMark.Aimable => (dbl ? "AimableMaterialDouble" : "AimableMaterial", "AimableFadeValue"),
            CellMark.Passable => ("ActiveMaterial", "PassableFadeValue"),
            CellMark.PassableTarget => ("ActiveMaterial", "PassableTargetFadeValue"),
            CellMark.Intersection => ("IntersectionMaterial", "PassableFadeValue"),
            _ => (null, null),
        };
        if (field == null) return null;
        _desc ??= Game.Instance.Fs.Exists(SelectionDescPath) ? Game.Instance.Xdb.Load(SelectionDescPath).Root : null;
        float fade = Math.Clamp(float.TryParse((string?)_desc?.Element(fadeField!), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 1f, 0f, 1f);
        if (_materials.TryGetValue((field, fade), out var m)) return m;
        Material? result = null;
        var href = (string?)_desc?.Element(field)?.Attribute("href");
        if (href != null)
        {
            var path = H5.Core.Xdb.XdbRef.Parse(href, SelectionDescPath).Path;
            if (Game.Instance.Fs.Exists(path))
            {
                var texRef = (string?)Game.Instance.Xdb.Load(path).Root!.Element("Texture")?.Attribute("href");
                if (texRef != null && Game.Instance.LoadTexture(H5.Core.Xdb.XdbRef.Parse(texRef, path).Path) is { } tex)
                {
                    var sm = Render.OverlayMaterial.Create(tex);
                    sm.SetShaderParameter("fade", fade);
                    result = sm;
                }
            }
        }
        _materials[(field, fade)] = result;
        return result;
    }

    /// <summary>A quad over grid coordinates [x0, x1] × [y0, y1] (cell units), draped on the terrain.</summary>
    ArrayMesh BuildMesh(float x0, float y0, float x1, float y1)
    {
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var idx = new List<int>();
        int n = Subdiv + 1;
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            float u = i / (float)Subdiv, v = j / (float)Subdiv;
            var p = _grid.CellCenter(x0 + (x1 - x0) * u, y0 + (y1 - y0) * v);
            verts.Add(p + new Vector3(0, Lift, 0));
            uvs.Add(new Vector2(u, v));
        }
        for (int j = 0; j < Subdiv; j++)
        for (int i = 0; i < Subdiv; i++)
        {
            int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
            idx.AddRange(new[] { a, c, b, b, c, d });
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
