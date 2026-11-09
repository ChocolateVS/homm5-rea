using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.AdvNet;

/// <summary>
/// Incremental view sync (view format 2). A player's view is the exact JSON text
/// <see cref="AdvState"/> serialises to, cut into keyed segments whose concatenation is that text:
/// small values whole, large containers into their members (array runs of primitives in blocks).
/// Updates carry only the segments whose bytes changed; the state hash is FNV-1a over every
/// segment's key and FNV-1a, so a replica re-hashes only what changed and still detects any byte
/// that differs from the host's view. Segment keys: top-level fields by name; below,
/// <c>parent␟.member</c>, <c>parent␟index</c>, <c>parent␟#block</c>, <c>parent␟(</c> / <c>parent␟)</c>
/// for a split container's opening / closing part (␟ = U+001F); a key's text before the first ␟ is
/// its top-level field ("" for the document's own braces).
///
/// Patch: {"vf":2,"reset":[[key,text]…]} replaces everything; otherwise {"vf":2,"del":[key…],
/// "ins":[[key,afterKey|null,text]…],"set":[[key,text]…]}, applied in that order.
/// </summary>
public static class ViewSegments
{
    public const int Format = 2;
    internal const char Sep = '\u001f';
    /// <summary>Containers whose text (with its leading separator and member name) exceeds this are split.</summary>
    internal const int SplitBytes = 1024;
    /// <summary>Consecutive primitive array elements per segment.</summary>
    internal const int Block = 128;

    internal static ulong Fnv(ReadOnlySpan<byte> b)
    {
        ulong h = 14695981039346656037UL;
        foreach (var x in b) { h ^= x; h *= 1099511628211UL; }
        return h;
    }

    internal static string Combine(IEnumerable<(string Key, ulong Hash)> segs)
    {
        ulong h = 14695981039346656037UL;
        void Byte(byte b) { h ^= b; h *= 1099511628211UL; }
        foreach (var (k, sh) in segs)
        {
            foreach (var c in k) { Byte((byte)c); Byte((byte)(c >> 8)); }
            Byte(0);
            for (int i = 0; i < 64; i += 8) Byte((byte)(sh >> i));
        }
        return h.ToString("x16");
    }

    internal static string FieldOf(string key)
    {
        int i = key.IndexOf(Sep);
        return i < 0 ? key : key[..i];
    }

    internal readonly record struct Child(string? Name, int RangeStart, int ValueStart, int ValueEnd, bool Container);

    /// <summary>The members / elements of the container at [valueStart, valueEnd); a child's range starts after the previous child (its separator and name included).</summary>
    internal static List<Child> Children(byte[] doc, int valueStart, int valueEnd)
    {
        var r = new Utf8JsonReader(doc.AsSpan(valueStart, valueEnd - valueStart));
        r.Read();
        var list = new List<Child>();
        int prevEnd = 1;
        while (r.Read())
        {
            if (r.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) break;
            string? name = null;
            if (r.TokenType == JsonTokenType.PropertyName) { name = r.GetString(); r.Read(); }
            int vs = (int)r.TokenStartIndex;
            bool container = r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray;
            if (container) r.Skip();
            int ve = (int)r.BytesConsumed;
            list.Add(new Child(name, valueStart + prevEnd, valueStart + vs, valueStart + ve, container));
            prevEnd = ve;
        }
        return list;
    }

    /// <summary>Cuts the value at [valueStart, valueEnd) (text from <paramref name="rangeStart"/>) into segments.</summary>
    internal static void Split(byte[] doc, int rangeStart, int valueStart, int valueEnd, string key, List<ViewSegment> output)
    {
        if (valueEnd - rangeStart <= SplitBytes || doc[valueStart] is not ((byte)'{' or (byte)'['))
        {
            output.Add(new ViewSegment(key, doc.AsMemory(rangeStart, valueEnd - rangeStart)));
            return;
        }
        var kids = Children(doc, valueStart, valueEnd);
        output.Add(new ViewSegment(key + Sep + "(", doc.AsMemory(rangeStart, valueStart + 1 - rangeStart)));
        int end = valueStart + 1;
        for (int i = 0; i < kids.Count;)
        {
            var k = kids[i];
            if (k.Name == null && !k.Container)
            {
                int block = i / Block, j = i;
                while (j < kids.Count && kids[j].Name == null && !kids[j].Container && j / Block == block) j++;
                output.Add(new ViewSegment(key + Sep + "#" + block, doc.AsMemory(k.RangeStart, kids[j - 1].ValueEnd - k.RangeStart)));
                end = kids[j - 1].ValueEnd;
                i = j;
                continue;
            }
            Split(doc, k.RangeStart, k.ValueStart, k.ValueEnd, key + Sep + (k.Name != null ? "." + k.Name : i.ToString()), output);
            end = k.ValueEnd;
            i++;
        }
        output.Add(new ViewSegment(key + Sep + ")", doc.AsMemory(end, valueEnd - end)));
    }
}

public readonly record struct ViewSegment(string Key, ReadOnlyMemory<byte> Bytes);

/// <summary>
/// Host side, one per player: builds that player's view as segments without cloning the whole
/// game state. Everything except the map objects goes through <see cref="ViewFilter.Redact"/> on a
/// copy that leaves out the objects and the explored maps (other players' are hidden anyway; the
/// player's own is shared read-only); each map object is redacted on its own by
/// <see cref="ViewFilter.RedactObject"/>, and an explored object whose full state did not change
/// reuses its previous redacted text. The text equals <see cref="ViewFilter.ViewFor"/>'s.
/// </summary>
public sealed class ViewDocBuilder
{
    static readonly Func<object, object> Shallow = (Func<object, object>)Delegate.CreateDelegate(typeof(Func<object, object>),
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!);

    readonly Dictionary<int, (byte[] Full, byte[] Member)> _seen = new();
    readonly Dictionary<int, byte[]> _pristine = new();
    string _teams = "";

    /// <summary>The full state of each map object as serialised this update (shared by every player's builder).</summary>
    public sealed class ObjectTexts(AdvState full)
    {
        readonly Dictionary<int, byte[]> _bytes = new();
        public byte[] Of(int key) => _bytes.TryGetValue(key, out var b) ? b : _bytes[key] = JsonSerializer.SerializeToUtf8Bytes(full.Objects[key], AdvState.Json);
    }

    public List<ViewSegment> Build(AdvState full, AdvMap map, int me, int? current, ObjectTexts objects)
    {
        ViewFilter.BindMap(map);
        var teams = string.Join(",", full.Players.Select(p => $"{p.Index}:{p.Team}"));
        if (teams != _teams) { _seen.Clear(); _teams = teams; }
        var fullMine = full.Players.Find(p => p.Index == me) ?? throw new ArgumentException($"no player {me}");

        var sh = (AdvState)Shallow(full);
        sh.Objects = new();
        sh.Rng = new H5.Combat.Rng();
        sh.HeroPool = new();
        sh.Players = full.Players.Select(p =>
        {
            var c = (PlayerState)Shallow(p);
            c.Explored = p.Index == me ? new() : p.Explored.Select(_ => Array.Empty<bool>()).ToList();
            return c;
        }).ToList();
        var v = JsonSerializer.Deserialize<AdvState>(JsonSerializer.SerializeToUtf8Bytes(sh, AdvState.Json), AdvState.Json)!;
        v.Players.Find(p => p.Index == me)!.Explored = fullMine.Explored;
        ViewFilter.Redact(v, full, map, me, current, objects: false);
        var doc = JsonSerializer.SerializeToUtf8Bytes(v, AdvState.Json);

        var output = new List<ViewSegment>();
        var top = ViewSegments.Children(doc, 0, doc.Length);
        output.Add(new ViewSegment(ViewSegments.Sep + "(", doc.AsMemory(0, 1)));
        int end = 1;
        var ally = ViewFilter.AllyOf(full, me);
        foreach (var k in top)
        {
            if (k.Name == "Objects") EmitObjects(doc, k, full, map, me, fullMine, ally, objects, output);
            else ViewSegments.Split(doc, k.RangeStart, k.ValueStart, k.ValueEnd, k.Name!, output);
            end = k.ValueEnd;
        }
        output.Add(new ViewSegment(ViewSegments.Sep + ")", doc.AsMemory(end, doc.Length - end)));
        return output;
    }

    void EmitObjects(byte[] doc, ViewSegments.Child k, AdvState full, AdvMap map, int me, PlayerState mine, Func<int, bool> ally,
        ObjectTexts objects, List<ViewSegment> output)
    {
        const string key = "Objects";
        output.Add(new ViewSegment(key + ViewSegments.Sep + "(", doc.AsMemory(k.RangeStart, k.ValueStart + 1 - k.RangeStart)));
        bool first = true;
        foreach (var id in full.Objects.Keys)
        {
            byte[] member;
            if (!ViewFilter.ObjectSeen(map, mine, id))
            {
                if (!_pristine.TryGetValue(id, out member!))
                    _pristine[id] = member = Member(id, JsonSerializer.SerializeToUtf8Bytes(new ObjState { Object = id }, AdvState.Json));
            }
            else
            {
                var fb = objects.Of(id);
                if (_seen.TryGetValue(id, out var c) && c.Full.AsSpan().SequenceEqual(fb)) member = c.Member;
                else
                {
                    var o = JsonSerializer.Deserialize<ObjState>(fb, AdvState.Json)!;
                    ViewFilter.RedactObject(o, me, ally);
                    member = Member(id, JsonSerializer.SerializeToUtf8Bytes(o, AdvState.Json));
                    _seen[id] = (fb, member);
                }
            }
            output.Add(new ViewSegment(key + ViewSegments.Sep + "." + id, first ? member.AsMemory(1) : member));
            first = false;
        }
        output.Add(new ViewSegment(key + ViewSegments.Sep + ")", doc.AsMemory(k.ValueEnd - 1, 1)));
    }

    /// <summary>",\"id\":" + value: a dictionary member with its leading separator (dropped for the first member).</summary>
    static byte[] Member(int id, byte[] value)
    {
        var head = Encoding.UTF8.GetBytes($",\"{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}\":");
        var b = new byte[head.Length + value.Length];
        head.CopyTo(b, 0);
        value.CopyTo(b, head.Length);
        return b;
    }
}

/// <summary>Host side, one per player: the segments that player's replica holds, and the patch to the next view.</summary>
public sealed class ViewSender
{
    List<string> _order = new();
    Dictionary<string, (ReadOnlyMemory<byte> Bytes, ulong Hash)> _segs = new();

    public string Hash { get; private set; } = "";

    /// <summary>Makes <paramref name="next"/> the current view; returns the patch from the previous one.</summary>
    public JsonObject Update(List<ViewSegment> next)
    {
        var segs = new Dictionary<string, (ReadOnlyMemory<byte> Bytes, ulong Hash)>(next.Count);
        var order = new List<string>(next.Count);
        var changed = new List<int>();
        long changedBytes = 0, totalBytes = 0;
        foreach (var s in next)
        {
            var key = s.Key;
            for (int dup = 1; segs.ContainsKey(key); dup++) key = s.Key + ViewSegments.Sep + "!" + dup;
            ulong h;
            if (_segs.TryGetValue(key, out var old) && old.Bytes.Span.SequenceEqual(s.Bytes.Span)) h = old.Hash;
            else { h = ViewSegments.Fnv(s.Bytes.Span); changed.Add(order.Count); changedBytes += s.Bytes.Length; }
            segs[key] = (s.Bytes, h);
            order.Add(key);
            totalBytes += s.Bytes.Length;
        }

        // Surviving segments must keep their relative order for an incremental patch.
        bool ordered = true;
        if (_order.Count > 0)
        {
            var oldIndex = new Dictionary<string, int>(_order.Count);
            for (int i = 0; i < _order.Count; i++) oldIndex[_order[i]] = i;
            int last = -1;
            foreach (var k in order)
                if (oldIndex.TryGetValue(k, out var i)) { if (i < last) { ordered = false; break; } last = i; }
        }

        var patch = new JsonObject { ["vf"] = ViewSegments.Format };
        if (_order.Count == 0 || !ordered || changedBytes * 2 > totalBytes)
            patch["reset"] = Pairs(order, segs);
        else
        {
            var del = new JsonArray();
            foreach (var k in _order) if (!segs.ContainsKey(k)) del.Add(k);
            var ins = new JsonArray();
            var set = new JsonArray();
            foreach (var i in changed)
            {
                var k = order[i];
                var text = Encoding.UTF8.GetString(segs[k].Bytes.Span);
                if (_segs.ContainsKey(k)) set.Add(new JsonArray(k, text));
                else ins.Add(new JsonArray(k, i > 0 ? order[i - 1] : null, text));
            }
            if (del.Count > 0) patch["del"] = del;
            if (ins.Count > 0) patch["ins"] = ins;
            if (set.Count > 0) patch["set"] = set;
        }
        _order = order;
        _segs = segs;
        Hash = ViewSegments.Combine(order.Select(k => (k, segs[k].Hash)));
        return patch;
    }

    /// <summary>The whole current view as a reset patch (game setup).</summary>
    public JsonObject Snapshot() => new() { ["vf"] = ViewSegments.Format, ["reset"] = Pairs(_order, _segs) };

    static JsonArray Pairs(List<string> order, Dictionary<string, (ReadOnlyMemory<byte> Bytes, ulong Hash)> segs)
    {
        var a = new JsonArray();
        foreach (var k in order) a.Add(new JsonArray(k, Encoding.UTF8.GetString(segs[k].Bytes.Span)));
        return a;
    }
}

/// <summary>Replica side: the segments of this player's view, patched by each update.</summary>
public sealed class ViewReceiver
{
    readonly List<string> _order = new();
    readonly Dictionary<string, (byte[] Bytes, ulong Hash)> _segs = new();

    /// <summary>Applies a patch; returns the top-level fields it touched (null: all of them).</summary>
    public HashSet<string>? Apply(JsonObject patch)
    {
        if ((int?)patch["vf"] != ViewSegments.Format) throw new InvalidDataException("unsupported view format");
        if (patch["reset"] is JsonArray reset)
        {
            _order.Clear();
            _segs.Clear();
            foreach (var n in reset)
            {
                var k = (string)n![0]!;
                if (_segs.ContainsKey(k)) throw new InvalidDataException("duplicate view segment");
                _order.Add(k);
                Put(k, (string)n[1]!);
            }
            return null;
        }
        var fields = new HashSet<string>();
        if (patch["del"] is JsonArray del)
        {
            var gone = new HashSet<string>();
            foreach (var n in del)
            {
                var k = (string)n!;
                if (!_segs.Remove(k)) throw new InvalidDataException("view segment missing");
                gone.Add(k);
                fields.Add(ViewSegments.FieldOf(k));
            }
            _order.RemoveAll(gone.Contains);
        }
        if (patch["ins"] is JsonArray ins)
            foreach (var n in ins)
            {
                var k = (string)n![0]!;
                var after = (string?)n[1];
                if (_segs.ContainsKey(k)) throw new InvalidDataException("duplicate view segment");
                int at = after == null ? 0 : _order.IndexOf(after) + 1;
                if (at == 0 && after != null) throw new InvalidDataException("view segment missing");
                _order.Insert(at, k);
                Put(k, (string)n[2]!);
                fields.Add(ViewSegments.FieldOf(k));
            }
        if (patch["set"] is JsonArray set)
            foreach (var n in set)
            {
                var k = (string)n![0]!;
                if (!_segs.ContainsKey(k)) throw new InvalidDataException("view segment missing");
                Put(k, (string)n[1]!);
                fields.Add(ViewSegments.FieldOf(k));
            }
        return fields;
    }

    void Put(string key, string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        _segs[key] = (b, ViewSegments.Fnv(b));
    }

    public string Hash => ViewSegments.Combine(_order.Select(k => (k, _segs[k].Hash)));

    /// <summary>The whole view text.</summary>
    public byte[] Document() => Concat(_ => true);

    /// <summary>A JSON object holding only the top-level <paramref name="fields"/>, as the view has them.</summary>
    public byte[] Fields(HashSet<string> fields)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)'{');
        bool any = false;
        string? current = null;
        foreach (var k in _order)
        {
            var f = ViewSegments.FieldOf(k);
            if (f.Length == 0 || !fields.Contains(f)) { current = null; continue; }
            var b = _segs[k].Bytes.AsSpan();
            if (f != current)
            {
                // The field's first segment: separator from the previous field, then "name":.
                current = f;
                if (b.Length > 0 && b[0] == (byte)',') b = b[1..];
                if (any) ms.WriteByte((byte)',');
                any = true;
            }
            ms.Write(b);
        }
        ms.WriteByte((byte)'}');
        return ms.ToArray();
    }

    byte[] Concat(Func<string, bool> include)
    {
        int n = 0;
        foreach (var k in _order) n += _segs[k].Bytes.Length;
        var doc = new byte[n];
        int at = 0;
        foreach (var k in _order) { var b = _segs[k].Bytes; b.CopyTo(doc, at); at += b.Length; }
        return doc;
    }
}
