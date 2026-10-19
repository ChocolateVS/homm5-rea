namespace H5.Scripting.Lua;

/// <summary>
/// The engine's Lua table. H5_Game.exe does NOT use Lua 4.0's ltable.c: its tables are an
/// STL-style chained hash map (bucket vector + singly linked nodes, SGI hash_table layout):
/// <list type="bullet">
/// <item>find FUN_004c0900, luaH_set FUN_004c0b10 (find, else insert a (key, nil) node), insert
/// FUN_004c0ab0 = resize(count + 1) FUN_004c09c0, then push the node at the HEAD of its bucket.</item>
/// <item>bucket counts from the prime list at 0x00e0c020 (lower_bound, FUN_00cef2a0); a new table
/// is created with hint 10 (FUN_00c67690 → FUN_00c67710) = 13 buckets, whatever the constructor size.</item>
/// <item>rehash moves each old bucket's nodes, head first, to the head of their new bucket.</item>
/// <item>hash FUN_00463f90: number = low 32 bits of the FISTP (round-half-even) int64; string =
/// h·5 + (signed char) over the bytes up to NUL (FUN_0040c470); table/function/userdata = pointer
/// &gt;&gt; 3 (heap addresses — reproduced with the object id, see GAP in docs/mmh55/SCRIPTS.md); nil = 0.</item>
/// <item>nil is an ordinary key (no "table index is nil" error exists in the exe).</item>
/// <item>storing nil keeps the node: traversal (`for k,v in t`, FUN_004c0c60 / FUN_00c44170 /
/// FUN_00464cc0) yields nil-valued entries until the collector's mark pass (FUN_00464640) erases
/// every nil-valued node with a non-nil key (FUN_00464c40). See <see cref="LuaState.GcCheck"/>.</item>
/// </list>
/// </summary>
public sealed class LuaTable : LuaObject
{
    /// <summary>Bucket counts (0x00e0c020, 30 entries).</summary>
    static readonly uint[] Primes =
    {
        13, 29, 53, 97, 193, 389, 769, 1543, 3079, 6151, 12289, 24593, 49157, 98317, 196613, 393241,
        786433, 1572869, 3145739, 6291469, 12582917, 25165843, 50331653, 100663319, 201326611,
        402653189, 805306457, 1610612741, 3221225473, 4294967291,
    };

    int[] _buckets;
    LuaValue[] _keys = new LuaValue[8];
    LuaValue[] _vals = new LuaValue[8];
    int[] _next = new int[8];
    int _nodes;      // allocated node slots (some may be on the free list)
    int _free = -1;  // free node list (erased nodes)
    int _count;

    /// <summary>Set by the owning state: told when a nil value is stored (a collection would erase that node).</summary>
    internal LuaState? Owner;

    /// <summary>Creates an empty table; the size hint is ignored like the exe's CREATETABLE.</summary>
    public LuaTable(int sizeHint = 0)
    {
        _buckets = NewBuckets(NextPrime(10));
    }

    static int[] NewBuckets(uint n)
    {
        var b = new int[n];
        Array.Fill(b, -1);
        return b;
    }

    static uint NextPrime(int n)
    {
        foreach (var p in Primes) if (p >= (uint)n) return p;
        return Primes[^1];
    }

    public int BucketCount => _buckets.Length;

    /// <summary>FUN_00463f90.</summary>
    public static uint Hash(in LuaValue key)
    {
        switch (key.Type)
        {
            case LuaType.Number:
                {
                    double d = Math.Round(key.N, MidpointRounding.ToEven);
                    // FISTP m64: out of range / NaN store the integer indefinite 0x8000000000000000.
                    if (double.IsNaN(d) || d >= 9223372036854775808.0 || d < -9223372036854775808.0) return 0;
                    return (uint)(ulong)(long)d;
                }
            case LuaType.String: return StringHash(key.Str);
            case LuaType.Table or LuaType.Function or LuaType.Userdata:
                return (uint)((LuaObject)key.O!).Id;
            default: return 0;
        }
    }

    /// <summary>FUN_0040c470: h = h*5 + (signed char)c up to the first NUL.</summary>
    public static uint StringHash(string s)
    {
        int h = 0;
        foreach (char ch in s)
        {
            if (ch == '\0') break;
            h = unchecked(h * 5 + (sbyte)(byte)ch);
        }
        return (uint)h;
    }

    int Bucket(in LuaValue key) => (int)(Hash(key) % (uint)_buckets.Length);

    int Find(in LuaValue key)
    {
        for (int n = _buckets[Bucket(key)]; n >= 0; n = _next[n])
            if (_keys[n].Equals(key)) return n;
        return -1;
    }

    public LuaValue Get(in LuaValue key)
    {
        int n = Find(key);
        return n < 0 ? LuaValue.Nil : _vals[n];
    }

    public LuaValue Get(string key) => Get(LuaValue.String(key));
    public LuaValue Get(double key) => Get(LuaValue.Number(key));

    /// <summary>luaH_set + store (luaV_settable).</summary>
    public void Set(in LuaValue key, in LuaValue value)
    {
        int n = Find(key);
        if (n < 0) n = Insert(key);
        _vals[n] = value;
        if (value.IsNil && !key.IsNil) Owner?.NoteNilValue(this);
    }

    public void Set(string key, in LuaValue value) => Set(LuaValue.String(key), value);
    public void Set(double key, in LuaValue value) => Set(LuaValue.Number(key), value);

    int Insert(in LuaValue key)
    {
        Resize(_count + 1);
        int b = Bucket(key);
        int n = AllocNode();
        _keys[n] = key;
        _vals[n] = LuaValue.Nil;
        _next[n] = _buckets[b];
        _buckets[b] = n;
        _count++;
        return n;
    }

    int AllocNode()
    {
        if (_free >= 0)
        {
            int f = _free;
            _free = _next[f];
            return f;
        }
        if (_nodes == _keys.Length)
        {
            int cap = _keys.Length * 2;
            Array.Resize(ref _keys, cap);
            Array.Resize(ref _vals, cap);
            Array.Resize(ref _next, cap);
        }
        return _nodes++;
    }

    /// <summary>FUN_004c09c0 (hash_table::resize).</summary>
    void Resize(int hint)
    {
        int old = _buckets.Length;
        if (hint <= old) return;
        uint n = NextPrime(hint);
        if (n <= (uint)old) return;
        var nb = NewBuckets(n);
        for (int i = 0; i < old; i++)
        {
            int node = _buckets[i];
            while (node >= 0)
            {
                int after = _next[node];
                int b = (int)(Hash(_keys[node]) % n);
                _next[node] = nb[b];
                nb[b] = node;
                node = after;
            }
        }
        _buckets = nb;
    }

    void Erase(int node)
    {
        int b = Bucket(_keys[node]);
        if (_buckets[b] == node) _buckets[b] = _next[node];
        else
        {
            int p = _buckets[b];
            while (p >= 0 && _next[p] != node) p = _next[p];
            if (p < 0) return;
            _next[p] = _next[node];
        }
        _keys[node] = LuaValue.Nil;
        _vals[node] = LuaValue.Nil;
        _next[node] = _free;
        _free = node;
        _count--;
    }

    /// <summary>The collector's pass over a reachable table: erase nil-valued nodes whose key is not nil.</summary>
    internal void CollectNilValues()
    {
        for (int b = 0; b < _buckets.Length; b++)
        {
            int n = _buckets[b];
            while (n >= 0)
            {
                int after = _next[n];
                if (_vals[n].IsNil && !_keys[n].IsNil) Erase(n);
                n = after;
            }
        }
    }

    int FirstNode()
    {
        foreach (var h in _buckets) if (h >= 0) return h;
        return -1;
    }

    /// <summary>
    /// The VM's traversal step (FUN_004c0c60): nil = first node; otherwise the node after the
    /// key's node in its chain, else the head of the next non-empty bucket. A key that is no longer
    /// in the table ends the traversal. Nil-valued entries are returned.
    /// </summary>
    public bool Next(in LuaValue key, out LuaValue nextKey, out LuaValue nextVal)
    {
        int n;
        if (key.IsNil) n = FirstNode();
        else
        {
            n = Find(key);
            if (n >= 0)
            {
                if (_next[n] >= 0) n = _next[n];
                else
                {
                    n = -1;
                    for (int b = Bucket(key) + 1; b < _buckets.Length; b++)
                        if (_buckets[b] >= 0) { n = _buckets[b]; break; }
                }
            }
        }
        if (n < 0)
        {
            nextKey = LuaValue.Nil;
            nextVal = LuaValue.Nil;
            return false;
        }
        nextKey = _keys[n];
        nextVal = _vals[n];
        return true;
    }

    /// <summary>Every node in traversal order (including nil-valued ones).</summary>
    public IEnumerable<KeyValuePair<LuaValue, LuaValue>> Nodes()
    {
        for (int b = 0; b < _buckets.Length; b++)
            for (int n = _buckets[b]; n >= 0; n = _next[n])
                yield return new(_keys[n], _vals[n]);
    }

    /// <summary>Live (non-nil) entries in traversal order, for host code.</summary>
    public IEnumerable<KeyValuePair<LuaValue, LuaValue>> Pairs() => Nodes().Where(kv => !kv.Value.IsNil);

    /// <summary>Count of non-nil values.</summary>
    public int Count => Pairs().Count();

    /// <summary>Nodes held (including nil-valued ones not yet collected).</summary>
    public int NodeCount => _count;

    // ---- serialisation: buckets and chain order must survive save/load exactly ----

    /// <summary>Nodes in traversal order with their bucket index, plus the bucket count.</summary>
    internal (LuaValue[] Keys, LuaValue[] Vals, int[] Buckets, int BucketCount) RawNodes
    {
        get
        {
            var keys = new List<LuaValue>(_count);
            var vals = new List<LuaValue>(_count);
            var bks = new List<int>(_count);
            for (int b = 0; b < _buckets.Length; b++)
                for (int n = _buckets[b]; n >= 0; n = _next[n]) { keys.Add(_keys[n]); vals.Add(_vals[n]); bks.Add(b); }
            return (keys.ToArray(), vals.ToArray(), bks.ToArray(), _buckets.Length);
        }
    }

    internal void RestoreNodes(LuaValue[] keys, LuaValue[] vals, int[] buckets, int bucketCount)
    {
        _buckets = NewBuckets((uint)Math.Max(1, bucketCount));
        int cap = Math.Max(8, keys.Length);
        _keys = new LuaValue[cap];
        _vals = new LuaValue[cap];
        _next = new int[cap];
        _nodes = 0;
        _free = -1;
        _count = 0;
        var tails = new int[_buckets.Length];
        Array.Fill(tails, -1);
        for (int i = 0; i < keys.Length; i++)
        {
            int n = _nodes++;
            _keys[n] = keys[i];
            _vals[n] = vals[i];
            _next[n] = -1;
            int b = buckets[i];
            if (tails[b] < 0) _buckets[b] = n; else _next[tails[b]] = n;
            tails[b] = n;
            _count++;
            if (vals[i].IsNil && !keys[i].IsNil) Owner?.NoteNilValue(this);
        }
    }
}

public class LuaRuntimeException : Exception
{
    public LuaRuntimeException(string message) : base(message) { }
}
