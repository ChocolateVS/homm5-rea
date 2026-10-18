using Godot;

namespace H5Reborn.Arena;

/// <summary>Cell decoration states, each backed by an original UI/CombatArena/Grid material.</summary>
public enum CellMark { None, Grid, Select, Affected, Aimable, Select2, Affected2, Aimable2 }

/// <summary>
/// Per-cell overlay quads draped on the terrain, textured with the original grid
/// materials (AM_OVERLAY: alpha-blended over the ground, unaffected by shadows).
/// </summary>
public partial class GridOverlay : Node3D
{
    static readonly Dictionary<CellMark, string> MaterialPaths = new()
    {
        [CellMark.Grid] = "UI/CombatArena/Grid/Inactive.xdb",
        [CellMark.Select] = "UI/CombatArena/Grid/Select.xdb",
        [CellMark.Affected] = "UI/CombatArena/Grid/affected.xdb",
        [CellMark.Aimable] = "UI/CombatArena/Grid/aimable.xdb",
        [CellMark.Select2] = "UI/CombatArena/Grid/select_2.xdb",
        [CellMark.Affected2] = "UI/CombatArena/Grid/affected_2.xdb",
        [CellMark.Aimable2] = "UI/CombatArena/Grid/aimable_2.xdb",
    };

    static Shader? _shader;
    const int Subdiv = 4;
    const float Lift = 0.03f;

    readonly ArenaGrid _grid;
    readonly MeshInstance3D[,] _cells = new MeshInstance3D[ArenaGrid.Width, ArenaGrid.Height];
    readonly CellMark[,] _marks = new CellMark[ArenaGrid.Width, ArenaGrid.Height];
    readonly Dictionary<CellMark, Material?> _materials = new();
    bool _showGrid;

    public GridOverlay(ArenaGrid grid)
    {
        _grid = grid;
        for (int x = 0; x < ArenaGrid.Width; x++)
        for (int y = 0; y < ArenaGrid.Height; y++)
        {
            var mi = new MeshInstance3D
            {
                Name = $"Cell_{x}_{y}",
                Mesh = BuildCellMesh(x, y),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            _cells[x, y] = mi;
            AddChild(mi);
        }
    }

    /// <summary>The faint cell outline the original draws when "show grid" is on.</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; for (int x = 0; x < ArenaGrid.Width; x++) for (int y = 0; y < ArenaGrid.Height; y++) Refresh(x, y); }
    }

    public void SetMark(Vector2I cell, CellMark mark)
    {
        if (!ArenaGrid.InBounds(cell)) return;
        _marks[cell.X, cell.Y] = mark;
        Refresh(cell.X, cell.Y);
    }

    public void ClearMarks()
    {
        for (int x = 0; x < ArenaGrid.Width; x++)
        for (int y = 0; y < ArenaGrid.Height; y++)
        { _marks[x, y] = CellMark.None; Refresh(x, y); }
    }

    void Refresh(int x, int y)
    {
        var mark = _marks[x, y];
        if (mark == CellMark.None && _showGrid) mark = CellMark.Grid;
        var mi = _cells[x, y];
        mi.Visible = mark != CellMark.None && MaterialFor(mark) != null;
        if (mi.Visible) mi.MaterialOverride = MaterialFor(mark);
    }

    Material? MaterialFor(CellMark mark)
    {
        if (_materials.TryGetValue(mark, out var m)) return m;
        Material? result = null;
        if (MaterialPaths.TryGetValue(mark, out var path) && Game.Instance.Fs.Exists(path))
        {
            var texRef = (string?)Game.Instance.Xdb.Load(path).Root!.Element("Texture")?.Attribute("href");
            if (texRef != null && Game.Instance.LoadTexture(H5.Core.Xdb.XdbRef.Parse(texRef, path).Path) is { } tex)
            {
                _shader ??= GD.Load<Shader>("res://src/Arena/GridOverlay.gdshader");
                var sm = new ShaderMaterial { Shader = _shader };
                sm.SetShaderParameter("tex", tex);
                result = sm;
            }
        }
        _materials[mark] = result;
        return result;
    }

    ArrayMesh BuildCellMesh(int cx, int cy)
    {
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var idx = new List<int>();
        int n = Subdiv + 1;
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            float u = i / (float)Subdiv, v = j / (float)Subdiv;
            var p = _grid.CellCenter(cx + u, cy + v);
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
