namespace H5.Scripting.Lua;

/// <summary>
/// Faithful port of Lua 4.0's ltable.c. Lua 4.0 tables have no array part: every key lives
/// in a power-of-two node vector with chained scatter (Brent's variation) and a descending
/// "firstfree" pointer. Reproducing the node layout exactly matters because `for k,v in t`
/// and next() walk the node vector in index order — scripts that iterate engine arrays
/// (GetObjectsInRegion, GetPlayerHeroes…) see the original order only with this layout.
/// </summary>
public sealed class LuaTable : LuaObject
{
    const int MinPower2 = 4;

    LuaValue[] _keys = Array.Empty<LuaValue>();
    LuaValue[] _vals = Array.Empty<LuaValue>();
    int[] _next = Array.Empty<int>();
    int _size;
    int _firstFree;

    /// <summary>Creates a table sized like luaH_new(L, sizeHint).</summary>
    public LuaTable(int sizeHint = 0) => SetNodeVector(Power2(sizeHint));

    public int NodeCount => _size;

    /// <summary>luaO_power2: smallest power of two strictly greater than n, at least MINPOWER2.</summary>
    static int Power2(int n)
    {
        int p = MinPower2;
        while (p <= n) p <<= 1;
        return p;
    }

    void SetNodeVector(int size)
    {
        _keys = new LuaValue[size];
        _vals = new LuaValue[size];
        _next = new int[size];
        for (int i = 0; i < size; i++) { _keys[i] = LuaValue.Nil; _vals[i] = LuaValue.Nil; _next[i] = -1; }
        _size = size;
        _firstFree = size - 1;
    }

    /// <summary>C's (unsigned long)(long)d with MSVC's out-of-range result 0x80000000.</summary>
    static uint NumberHash(double d)
    {
        if (d > -2147483649.0 && d < 2147483648.0) return (uint)(int)d;
        return 0x80000000u;
    }

    /// <summary>lstring.c hash_s: seed with the length and fold in (at most ~32) leading bytes.</summary>
    public static uint StringHash(string s)
    {
        uint l = (uint)s.Length;
        uint h = l;
        uint step = (l >> 5) | 1;
        int p = 0;
        for (; l >= step; l -= step)
            h ^= (h << 5) + (h >> 2) + (byte)s[p++];
        return h;
    }

    static uint ObjectHash(LuaObject o) => (uint)o.Id << 3;

    int MainPosition(in LuaValue key)
    {
        uint h = key.Type switch
        {
            LuaType.Number => NumberHash(key.N),
            LuaType.String => StringHash(key.Str),
            LuaType.Table or LuaType.Function or LuaType.Userdata => ObjectHash((LuaObject)key.O!),
            _ => throw new LuaRuntimeException("table index is nil"),
        };
        return (int)(h & (uint)(_size - 1));
    }

    /// <summary>Node index holding the key (even if its value is nil), or -1.</summary>
    int Find(in LuaValue key)
    {
        if (key.IsNil) return -1;
        int n = MainPosition(key);
        do
        {
            if (_keys[n].Equals(key)) return n;
            n = _next[n];
        } while (n >= 0);
        return -1;
    }

    public LuaValue Get(in LuaValue key)
    {
        if (key.IsNil) throw new LuaRuntimeException("table index is nil");
        int n = Find(key);
        return n < 0 ? LuaValue.Nil : _vals[n];
    }

    public LuaValue Get(string key) => Get(LuaValue.String(key));
    public LuaValue Get(double key) => Get(LuaValue.Number(key));

    /// <summary>
    /// luaH_set followed by the store, as luaV_settable does. Note Lua 4.0 inserts the key even
    /// when the value is nil; such dead nodes keep their slot until the next rehash.
    /// </summary>
    public void Set(in LuaValue key, in LuaValue value)
    {
        int slot = SetSlot(key);
        _vals[slot] = value;
    }

    public void Set(string key, in LuaValue value) => Set(LuaValue.String(key), value);
    public void Set(double key, in LuaValue value) => Set(LuaValue.Number(key), value);

    int SetSlot(in LuaValue key)
    {
        int mp = MainPosition(key);
        int n = mp;
        do
        {
            if (_keys[n].Equals(key)) return n;
            n = _next[n];
        } while (n >= 0);

        if (!_keys[mp].IsNil)
        {
            n = _firstFree;
            int othern;
            if (mp > n && (othern = MainPosition(_keys[mp])) != mp)
            {
                // Colliding node is out of its main position: move it to the free slot.
                while (_next[othern] != mp) othern = _next[othern];
                _next[othern] = n;
                _keys[n] = _keys[mp];
                _vals[n] = _vals[mp];
                _next[n] = _next[mp];
                _next[mp] = -1;
                _vals[mp] = LuaValue.Nil;
            }
            else
            {
                // Colliding node is in its main position: new key goes to the free slot.
                _next[n] = _next[mp];
                _next[mp] = n;
                mp = n;
            }
        }
        _keys[mp] = key;
        for (;;)
        {
            if (_keys[_firstFree].IsNil) return mp;
            if (_firstFree == 0) break;
            _firstFree--;
        }
        Rehash();
        return SetSlot(key);
    }

    int NumUse()
    {
        int used = 0;
        for (int i = 0; i < _size; i++) if (!_vals[i].IsNil) used++;
        return used;
    }

    void Rehash()
    {
        int oldSize = _size;
        var oldKeys = _keys;
        var oldVals = _vals;
        int nelems = NumUse();
        if (nelems >= oldSize - oldSize / 4) SetNodeVector(oldSize * 2);
        else if (nelems <= oldSize / 4 && oldSize > MinPower2) SetNodeVector(oldSize / 2);
        else SetNodeVector(oldSize);
        for (int i = 0; i < oldSize; i++)
            if (!oldVals[i].IsNil)
                _vals[SetSlot(oldKeys[i])] = oldVals[i];
    }

    /// <summary>
    /// luaH_next: the node after <paramref name="key"/> (nil = first) that has a non-nil value.
    /// Returns false at the end. Throws like the original for keys not in the table.
    /// </summary>
    public bool Next(in LuaValue key, out LuaValue nextKey, out LuaValue nextVal)
    {
        int i;
        if (key.IsNil) i = 0;
        else
        {
            int n = Find(key);
            if (n < 0) throw new LuaRuntimeException("invalid key for `next'");
            i = n + 1;
        }
        for (; i < _size; i++)
        {
            if (!_vals[i].IsNil)
            {
                nextKey = _keys[i];
                nextVal = _vals[i];
                return true;
            }
        }
        nextKey = LuaValue.Nil;
        nextVal = LuaValue.Nil;
        return false;
    }

    /// <summary>Live entries in traversal (node) order.</summary>
    public IEnumerable<KeyValuePair<LuaValue, LuaValue>> Pairs()
    {
        for (int i = 0; i < _size; i++)
            if (!_vals[i].IsNil) yield return new(_keys[i], _vals[i]);
    }

    /// <summary>Count of non-nil values (Lua 4.0 getn semantics are not used by the game).</summary>
    public int Count => NumUse();

    // ---- raw node access for serialisation (layout must survive save/load exactly) ----

    internal (LuaValue[] Keys, LuaValue[] Vals, int[] Next, int FirstFree) RawNodes => (_keys, _vals, _next, _firstFree);

    internal void RestoreNodes(LuaValue[] keys, LuaValue[] vals, int[] next, int firstFree)
    {
        _keys = keys;
        _vals = vals;
        _next = next;
        _size = keys.Length;
        _firstFree = firstFree;
    }
}

public class LuaRuntimeException : Exception
{
    public LuaRuntimeException(string message) : base(message) { }
}
