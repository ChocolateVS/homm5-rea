namespace H5.Rmg;

/// <summary>
/// Emulates the iteration order of the Nival hash_map the generator keeps zones and
/// neighbour counts in (insert 0x009106f0, rehash 0x00b62030, bucket sizes from the prime
/// table at 0x00e0c020). Keys are hashed as unsigned ints modulo the bucket count; new
/// nodes go to the head of their bucket chain; when the element count exceeds the bucket
/// count the table grows to the next prime and every chain is re-linked head-first, which
/// reverses relative order within a bucket. Iteration walks buckets in ascending order.
/// Results that depend on "first maximum" or "first zone that fits" rely on this order.
/// </summary>
public sealed class NivalHashMap<TValue>
{
    static readonly uint[] Primes =
    {
        13, 29, 53, 97, 193, 389, 769, 1543, 3079, 6151, 12289, 24593, 49157, 98317, 196613,
        393241, 786433, 1572869, 3145739, 6291469, 12582917, 25165843, 50331653, 100663319,
        201326611, 402653189, 805306457, 1610612741, 3221225473u, 4294967291u,
    };

    sealed class Node(uint key, TValue value)
    {
        public readonly uint Key = key;
        public TValue Value = value;
        public Node? Next;
    }

    Node?[] _buckets;
    public int Count { get; private set; }

    /// <summary>The original constructs these with a size hint (10 → 13 buckets in the generator).</summary>
    public NivalHashMap(int sizeHint = 10) => _buckets = new Node?[LowerBoundPrime(sizeHint)];

    static uint LowerBoundPrime(int n)
    {
        foreach (var p in Primes) if (p >= (uint)n) return p;
        return Primes[^1];
    }

    public bool TryGetValue(int key, out TValue value)
    {
        for (var n = _buckets[(uint)key % (uint)_buckets.Length]; n != null; n = n.Next)
            if (n.Key == (uint)key) { value = n.Value; return true; }
        value = default!;
        return false;
    }

    public bool ContainsKey(int key) => TryGetValue(key, out _);

    /// <summary>Inserts if absent (returns false when the key already existed, leaving its value).</summary>
    public bool Add(int key, TValue value)
    {
        if (ContainsKey(key)) return false;
        Reserve(Count + 1);
        uint b = (uint)key % (uint)_buckets.Length;
        _buckets[b] = new Node((uint)key, value) { Next = _buckets[b] };
        Count++;
        return true;
    }

    public TValue this[int key]
    {
        get => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException(key.ToString());
        set
        {
            for (var n = _buckets[(uint)key % (uint)_buckets.Length]; n != null; n = n.Next)
                if (n.Key == (uint)key) { n.Value = value; return; }
            Add(key, value);
        }
    }

    /// <summary>exe 0x00b62030: grow to the next prime ≥ n when n exceeds the bucket count.</summary>
    void Reserve(int n)
    {
        int old = _buckets.Length;
        if (n <= old) return;
        uint size = LowerBoundPrime(n);
        if (size <= (uint)old) return;
        var nb = new Node?[size];
        for (int i = 0; i < old; i++)
        {
            while (_buckets[i] is { } node)
            {
                _buckets[i] = node.Next;
                uint b = node.Key % size;
                node.Next = nb[b];
                nb[b] = node;
            }
        }
        _buckets = nb;
    }

    /// <summary>Iteration order of the original (bucket index ascending, chain order within a bucket).</summary>
    public IEnumerable<(int Key, TValue Value)> Items()
    {
        foreach (var head in _buckets)
            for (var n = head; n != null; n = n.Next)
                yield return ((int)n.Key, n.Value);
    }

    public IEnumerable<TValue> Values => Items().Select(i => i.Value);
}
