using H5.Core.Data;

namespace H5.Combat;

/// <summary>
/// Battle-start / tactics auto placement ported from H5_Game.exe (FUN_00578510 → FUN_0085a190), see
/// docs/rules/audit/combat_abilities_d.md. Grid: W deploy columns (col 0 = own map edge, W−1 = front) × H = field
/// height rows; the rewrite cell of grid (col, row) is x = col (left army) or Width − 1 − col (right army), y = row.
/// A stack stores the exe "max corner": a 2×2 placed at (col, row) covers cols col..col+1 and rows row−1..row.
/// </summary>
public static class ExeAutoPlacement
{
    /// <summary>Console variable combat_new_autoplacing (DAT_00f43ea4, default 1, registered by FUN_0085aee0).</summary>
    public static bool CombatNewAutoplacing = true;

    sealed class Rec
    {
        public int Index;
        public CreatureDef Def = null!;
        public int Count, Size, V;
        public bool Placed, Removed;
        public int Col, Row;     // grid cell of the max corner
        public int ExeX, ExeY;   // the exe battlefield cell stored with the record (+0x14 / +0x18)
        public bool Shooter => Def.Shots > 0;
    }

    sealed class Placer
    {
        public int W, H, Width, FieldW;
        public bool Left, NoHero, Kind4;
        public bool[,] Grid = null!;   // [row, col] true = blocked / occupied
        public int[] Run1 = null!, Run2 = null!;
        public List<Rec> Recs = null!;
        public int K;
        public GameDatabase Db = null!;
        public HashSet<Cell> Static = null!;

        public bool InGrid(int col, int row) => col >= 0 && col < W && row >= 0 && row < H;

        /// <summary>FUN_00856fa0: all cells col..col+s−1 × row−s+1..row inside the grid and free.</summary>
        public bool Fit(int row, int col, int s)
        {
            if (W < s) return false;
            for (int c = col; c < col + s; c++)
                for (int r = row; r > row - s; r--)
                    if (!InGrid(c, r) || Grid[r, c]) return false;
            return true;
        }

        public void Place(Rec rec, int col, int row)
        {
            int s = rec.Size;
            for (int c = col; c < col + s; c++)
                for (int r = row; r > row - s; r--) Grid[r, c] = true;
            rec.Placed = true; rec.Col = col; rec.Row = row;
            rec.ExeX = Left ? (s == 1 ? col + 2 : col + 3) : FieldW - col - 3;
            rec.ExeY = row + 1;
        }

        /// <summary>FUN_00857160.</summary>
        public bool TryAt(Rec rec, int col, int row)
        {
            if (rec.Size == 1)
            {
                if (!Fit(row, col, 1)) return false;
                Place(rec, col, row);
                return true;
            }
            foreach (int c in new[] { col, col - 1 })
                foreach (int r in new[] { row, row + 1 })
                    if (Fit(r, c, 2)) { Place(rec, c, r); return true; }
            return false;
        }

        /// <summary>FUN_00857880.</summary>
        public void PlaceList(List<Rec> list, List<(int Col, int Row)> cells)
        {
            foreach (var rec in list)
            {
                if (rec.Placed) continue;
                foreach (var (c, r) in cells)
                    if (TryAt(rec, c, r)) break;
            }
        }

        public void ColPlace(List<Rec> list, List<int> rows, int col) => PlaceList(list, rows.Select(r => (col, r)).ToList());

        /// <summary>FUN_00857640: 2×2 stacks down one column, the row cursor shared by all records.</summary>
        public void Fill2(int col, bool shootersOnly)
        {
            int r = 1;
            foreach (var rec in Recs)
            {
                if (rec.Placed || rec.Size != 2 || shootersOnly && !rec.Shooter) continue;
                while (!Fit(r, col, 2)) { r++; if (r >= H) return; }
                Place(rec, col, r);
            }
        }

        /// <summary>FUN_00857780: 1×1 stacks down one column.</summary>
        public void Fill1(int col, bool shootersOnly)
        {
            int r = 0;
            foreach (var rec in Recs)
            {
                if (rec.Placed || rec.Size != 1 || shootersOnly && !rec.Shooter) continue;
                if (col < 0 || col >= W) return;
                while (Grid[r, col]) { r++; if (r >= H) return; }
                Place(rec, col, r);
            }
        }

        // ---- categories (FUN_008583c0) and lists (FUN_00858d40)
        static bool HasAb(CreatureDef d, string a) => d.Abilities.Contains(a);
        static string Family(CreatureDef d) => d.BaseCreature is { Length: > 0 } b && b != "CREATURE_UNKNOWN" ? b : d.Id;

        public bool Cat(int cat, Rec r)
        {
            var d = r.Def;
            switch (cat)
            {
                case 0: return true;
                case 1: return !r.Shooter;
                case 2: return !r.Shooter && r.Size > 1 && !HasAb(d, "ABILITY_AURA_OF_MAGIC_RESISTANCE");
                case 3: return !r.Shooter && r.Size == 1;
                case 4:
                    if (r.Shooter) return true;
                    return HasAb(d, Ab.GoblinThrower) && Recs.Any(o => Family(o.Def) == "CREATURE_GOBLIN");
                case 5: return HasAb(d, "ABILITY_AURA_OF_MAGIC_RESISTANCE") || HasAb(d, "ABILITY_SHIELD_OTHER");
                case 6: return r.Size == 1;
                case 7: return r.Size > 1;
                case 8: return HasAb(d, "ABILITY_SACRIFICE_GOBLIN") || HasAb(d, "ABILITY_SWALLOW_GOBLIN") || HasAb(d, Ab.GoblinThrower);
                case 9: return Family(d) == "CREATURE_GOBLIN";
                default: return false;
            }
        }

        public List<Rec> CatList(int cat, bool alt)
        {
            var list = Recs.Where(r => !r.Removed && Cat(cat, r)).ToList();
            int thr = FieldW - W - 6;
            return alt ? MergeSort(list, (a, b) => AltBefore(a, b, thr)) : MergeSort(list, (a, b) => a.V > b.V);
        }

        /// <summary>FUN_0085aba0: walkers before flyers, then the faster when either is fast enough, then by value.</summary>
        static bool AltBefore(Rec a, Rec b, int thr)
        {
            bool fa = a.Def.Flying, fb = b.Def.Flying;
            if (!fa && fb) return true;
            if (fa && !fb) return false;
            int sa = a.Def.Speed, sb = b.Def.Speed;
            if ((sa >= thr || sb >= thr) && sa != sb) return sa > sb;
            return a.V > b.V;
        }

        /// <summary>FUN_008585c0: row indices by value, descending (exe merge sort).</summary>
        public List<int> RowOrder(int[] values) => MergeSort(Enumerable.Range(0, H).ToList(), (a, b) => values[a] > values[b]);

        /// <summary>FUN_00857e60: the K consecutive rows (cyclic) with the smallest free-run sum.</summary>
        public int[] ShooterMask(bool large)
        {
            var rl = large ? Run2 : Run1;
            int n = H, k = K;
            var mask = new int[n];
            if (k <= 0) return mask;
            int S = 0;
            for (int i = 0; i < k; i++) S += rl[i % n];
            int best = S, start = 0;
            for (int i = 0; i < n; i++)
            {
                S += rl[(k + i) % n] - rl[i];
                if (S < best) { best = S; start = i + 1; }
            }
            for (int j = 0; j < k; j++) mask[(start + j) % n] = 1;
            return mask;
        }

        /// <summary>FUN_00858fb0: goblins around the goblin users.</summary>
        public void Goblins(List<Rec> g9, List<Rec> g8)
        {
            var lists = new List<List<(int Col, int Row)>>();
            foreach (var u in g8)
            {
                int x = u.ExeX, y = u.ExeY, s = u.Size;
                var ring = new List<(int, int)>();
                for (int j = 1 - s; j <= 0; j++) ring.Add((x + 1, y + j));
                for (int i = 1; i >= -s; i--) ring.Add((x + i, y + 1));
                for (int j = 0; j >= 1 - s; j--) ring.Add((x - s, y + j));
                for (int i = -s; i <= 1; i++) ring.Add((x + i, y - s));
                var L = new List<(int, int)>();
                foreach (var (ex, ey) in ring)
                {
                    int col = Left ? ex - 2 : FieldW - ex - 3, row = ey - 1;
                    if (InGrid(col, row) && !Grid[row, col]) L.Add((col, row));
                }
                lists.Add(L);
            }
            int g = 0;
            while (lists.Count > 0)
            {
                while (g < g9.Count && g9[g].Placed) g++;
                if (g >= g9.Count) return;
                if (lists[0].Count == 0) { lists.RemoveAt(0); continue; }
                (int, int) bestCell = lists[0][0]; int bestCnt = -1;
                foreach (var c in lists[0])
                {
                    int cnt = 1 + lists.Skip(1).Count(l => l.Contains(c));
                    if (cnt > bestCnt) { bestCnt = cnt; bestCell = c; }
                }
                if (TryAt(g9[g], bestCell.Item1, bestCell.Item2)) g++;
                lists.RemoveAll(l => l.Contains(bestCell));
            }
        }

        static readonly (int Dx, int Dy)[] N8 = { (0, -1), (0, 1), (-1, 0), (1, 0), (1, -1), (1, 1), (-1, -1), (-1, 1) };

        /// <summary>FUN_008580e0 (exe quirk kept: the shooters' exe x,y are used as grid col,row).</summary>
        public void NearShooters(List<Rec> list, List<Rec> shooters)
        {
            var points = new List<(int, int)>();
            foreach (var s in shooters)
            {
                points.Add((s.ExeX, s.ExeY));
                if (s.Size != 1) { points.Add((s.ExeX, s.ExeY + 1)); points.Add((s.ExeX - 1, s.ExeY)); points.Add((s.ExeX - 1, s.ExeY + 1)); }
            }
            var cells = new List<(int, int)>();
            foreach (var (px, py) in points)
                foreach (var (dx, dy) in N8)
                {
                    var c = (px + dx, py + dy);
                    if (InGrid(c.Item1, c.Item2) && !Grid[c.Item2, c.Item1] && !cells.Contains(c)) cells.Add(c);
                }
            PlaceList(list, cells);
        }

        public void StandardPass()
        {
            var r = RowOrder(Run2);
            ColPlace(CatList(2, alt: true), r, W - 1);
            var mask = ShooterMask(false);
            r = RowOrder(mask);
            var S = CatList(4, alt: false);
            ColPlace(S, r, 0);
            r = RowOrder(Run1);
            var g9 = CatList(9, alt: true); var g8 = CatList(8, alt: true);
            if (g9.Count > 0 && g8.Count > 0) { ColPlace(g8, r, W - 1); Goblins(g9, g8); }
            NearShooters(CatList(5, alt: true), S);
            ColPlace(CatList(3, alt: true), r, W - 1);
        }
    }

    /// <summary>The exe's merge sort (FUN_0085a700 / FUN_0085b0e0 family): strided bottom-up merge, ties take the right side.</summary>
    static List<T> MergeSort<T>(List<T> a, Func<T, T, bool> before)
    {
        int n = a.Count;
        if (n < 2) return a.ToList();
        var cur = a.ToArray();
        int w = 1;
        while (w * 2 < n) w *= 2;
        for (; w > 0; w >>= 1)
        {
            var d = new T[n];
            if (2 * w > n) for (int i = n - w; i < n; i++) d[i] = cur[i];
            for (int i = 0; i < Math.Min(w, n - w); i++)
            {
                int L = i, R = i + w, o = i;
                while (true)
                {
                    if (before(cur[L], cur[R]))
                    {
                        d[o] = cur[L]; o += w; L += 2 * w;
                        if (L >= n) { for (; R < n; R += 2 * w, o += w) d[o] = cur[R]; break; }
                    }
                    else
                    {
                        d[o] = cur[R]; o += w; R += 2 * w;
                        if (R >= n) { for (; L < n; L += 2 * w, o += w) d[o] = cur[L]; break; }
                    }
                }
            }
            cur = d;
        }
        return cur.ToList();
    }

    /// <summary>FUN_00858a80 → FUN_00bf9b80: the stack's own AI power against one Peasant, no hero.</summary>
    static int StackValue(GameDatabase db, CreatureDef d, int n)
    {
        var peasant = db.Creatures.TryGetValue("CREATURE_PEASANT", out var p) ? p : d;
        int dmg = (int)((d.MinDamage + d.MaxDamage) * (long)n / 2.0);
        var own = new AI.SideValue { Health = (double)d.Health * n, Damage = dmg, AttackLevel = dmg > 0 ? d.Attack : 0, Defence = d.Defence, Bonus = 1 };
        var other = new AI.SideValue { AttackLevel = peasant.Attack, TroopsDefence = 0 };
        return own.Power(other);
    }

    /// <summary>
    /// Cells for the side's stacks in slot order (null: the stack was removed from the arena / not placed).
    /// <paramref name="blocked"/> are extra occupied cells (war machines and other objects of the army).
    /// </summary>
    public static List<Cell?> Place(GameDatabase db, BattleState s, int side, List<ArmySlot> slots, IEnumerable<Cell>? blocked = null)
    {
        var recs = slots.Select((a, i) => new Rec { Index = i, Def = db.Creature(a.Creature), Count = a.Count }).ToList();
        foreach (var r in recs) { r.Size = Math.Max(1, r.Def.CombatSize); r.V = StackValue(db, r.Def, r.Count); }
        var P = new Placer
        {
            W = side == 0 ? s.DeployColumns0 : s.DeployColumns1, H = s.Height, Width = s.Width, FieldW = s.Width + 4,
            Left = side == 0, NoHero = !s.Heroes[side].Present, Recs = recs, Db = db, Static = new HashSet<Cell>(s.Obstacles),
        };
        var extra = new HashSet<Cell>(blocked ?? Enumerable.Empty<Cell>());
        int ToX(int col) => P.Left ? col : P.Width - 1 - col;

        void Setup()
        {
            P.Grid = new bool[P.H, Math.Max(1, P.W)];
            for (int r = 0; r < P.H; r++)
                for (int c = 0; c < P.W; c++)
                {
                    var cell = new Cell(ToX(c), r);
                    if (!s.InBounds(cell) || P.Static.Contains(cell) || extra.Contains(cell)) P.Grid[r, c] = true;
                }
            P.K = recs.Count(r => P.Cat(4, r));
            P.Run1 = Runs(1); P.Run2 = Runs(2);
        }

        int[] Runs(int sz)
        {
            var o = new int[P.H];
            for (int y = 0; y < P.H; y++)
            {
                int x = P.Left ? 2 : P.Width - 3, n = 0;
                while (x >= 0 && x < P.Width && !P.Static.Contains(new Cell(x, y))) { n++; x += P.Left ? 1 : -1; }
                o[y] = n;
            }
            if (sz > 1) for (int i = P.H - 1; i >= 1; i--) o[i] = Math.Min(o[i - 1], o[i]);
            return o;
        }

        bool simple = recs.Count(r => r.Count > 0) < 2;
        if (!CombatNewAutoplacing || simple)
        {
            Setup();
            foreach (var r in recs) r.Placed = false;
            if (!Even(P)) Fallback(P);
        }
        else
        {
            foreach (var r in recs) r.Removed = false;
            while (true)
            {
                Setup();
                foreach (var r in recs) r.Placed = false;
                // FUN_008598e0 (the defensive pass FUN_008593c0 needs the AI strategy flag, which stays 0 here)
                P.StandardPass();
                for (int col = 0; col < P.W; col++) { P.Fill2(col, false); P.Fill1(col, false); }
                if (recs.All(r => r.Placed || r.Removed)) break;
                // FUN_008588b0's retry repeats the same placement and FUN_008599d0 never joins: remove the weakest stack.
                var list = P.CatList(0, alt: false);
                if (list.Count == 0) break;
                list[^1].Removed = true;
            }
        }
        return recs.Select(r =>
        {
            if (!r.Placed || r.Removed && !r.Placed) return (Cell?)null;
            int sz = r.Size;
            int x = P.Left ? r.Col : P.Width - sz - r.Col;
            return new Cell(x, r.Row - sz + 1);
        }).ToList();
    }

    /// <summary>FUN_00857380: even spread over the rows by cumulative size.</summary>
    static bool Even(Placer P)
    {
        if (P.Recs.Any(r => r.Placed)) return false;
        int T = P.Recs.Sum(r => r.Size) + (P.Kind4 ? 2 : 0);
        if (P.H < T) return false;
        int row = 0, cum = 0;
        foreach (var rec in P.Recs)
        {
            int s = rec.Size;
            long a = (long)(2 * row - s + 3) * T, b = (long)(s + 2 * cum) * P.H;
            if (a <= b)
                while (row < P.H) { a += 2 * T; row++; if (a > b) break; }
            while (true)
            {
                bool any = false;
                if (s <= P.W) for (int c = 0; c <= P.W - s; c++) if (P.Fit(row, c, s)) { any = true; break; }
                if (any) break;
                if (row >= P.H) return false;
                row++;
            }
            if (row >= P.H) return false;
            int col = 0;
            if (!rec.Shooter && !P.NoHero) { for (int c = P.W - s; c >= 0; c--) if (P.Fit(row, c, s)) { col = c; break; } }
            else { for (int c = 0; c <= P.W - s; c++) if (P.Fit(row, c, s)) { col = c; break; } }
            P.Place(rec, col, row);
            cum += s;
        }
        return true;
    }

    /// <summary>FUN_00859930.</summary>
    static void Fallback(Placer P)
    {
        P.Fill2(0, true); P.Fill1(0, true);
        if (P.W > 2) { P.Fill2(1, false); P.Fill1(2, false); }
        P.Fill2(0, false); P.Fill1(1, false); P.Fill1(0, false);
    }
}
