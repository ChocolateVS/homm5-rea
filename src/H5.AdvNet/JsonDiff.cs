using System.Text.Json.Nodes;

namespace H5.AdvNet;

/// <summary>
/// Structural diff/patch for JSON views, so each update carries only what changed in a
/// player's view of the game. A patch is an array of ops: {"p":[path…],"v":value} sets a
/// value (creating it), {"p":[path…],"d":1} deletes it. Path segments are object keys
/// (strings) or array indices (numbers). Arrays whose length changed are replaced whole.
/// </summary>
public static class JsonDiff
{
    public static JsonArray Diff(JsonNode? from, JsonNode? to)
    {
        var ops = new JsonArray();
        Walk(from, to, new List<JsonNode>(), ops);
        return ops;
    }

    static void Walk(JsonNode? a, JsonNode? b, List<JsonNode> path, JsonArray ops)
    {
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var (k, _) in oa)
                if (!ob.ContainsKey(k)) ops.Add(new JsonObject { ["p"] = PathOf(path, k), ["d"] = 1 });
            foreach (var (k, vb) in ob)
            {
                if (!oa.TryGetPropertyValue(k, out var va)) { ops.Add(Set(PathOf(path, k), vb)); continue; }
                path.Add(JsonValue.Create(k)!);
                Walk(va, vb, path, ops);
                path.RemoveAt(path.Count - 1);
            }
            return;
        }
        if (a is JsonArray aa && b is JsonArray ab && aa.Count == ab.Count)
        {
            for (int i = 0; i < aa.Count; i++)
            {
                path.Add(JsonValue.Create(i)!);
                Walk(aa[i], ab[i], path, ops);
                path.RemoveAt(path.Count - 1);
            }
            return;
        }
        if (!JsonNode.DeepEquals(a, b)) ops.Add(Set(PathOf(path, null), b));
    }

    static JsonArray PathOf(List<JsonNode> path, string? last)
    {
        var p = new JsonArray();
        foreach (var s in path) p.Add(s.DeepClone());
        if (last != null) p.Add(last);
        return p;
    }

    static JsonObject Set(JsonArray path, JsonNode? value) => new() { ["p"] = path, ["v"] = value?.DeepClone() };

    /// <summary>Applies a patch in place where possible; returns the (possibly replaced) root.</summary>
    public static JsonNode? Apply(JsonNode? root, JsonArray ops)
    {
        foreach (var opNode in ops)
        {
            var op = opNode!.AsObject();
            var path = op["p"]!.AsArray();
            bool delete = op.ContainsKey("d");
            var value = op["v"]?.DeepClone();
            if (path.Count == 0) { root = delete ? null : value; continue; }
            JsonNode? cur = root;
            for (int i = 0; i < path.Count - 1; i++) cur = Child(cur, path[i]!);
            var last = path[^1]!;
            if (cur is JsonObject o)
            {
                var key = last.GetValue<string>();
                if (delete) o.Remove(key);
                else o[key] = value;
            }
            else if (cur is JsonArray a)
            {
                var idx = last.GetValue<int>();
                if (delete) a.RemoveAt(idx);
                else a[idx] = value;
            }
            else throw new InvalidOperationException("patch path does not exist in the view");
        }
        return root;
    }

    static JsonNode? Child(JsonNode? n, JsonNode seg) => n switch
    {
        JsonObject o => o[seg.GetValue<string>()],
        JsonArray a => a[seg.GetValue<int>()],
        _ => throw new InvalidOperationException("patch path does not exist in the view"),
    };

    /// <summary>
    /// Order-independent fingerprint of a JSON document (object keys sorted), so a patched
    /// view and a freshly serialised one hash identically.
    /// </summary>
    public static string CanonicalHash(JsonNode? n)
    {
        ulong h = 14695981039346656037UL;
        void Byte(byte b) { h ^= b; h *= 1099511628211UL; }
        void Str(string s) { foreach (var c in s) { Byte((byte)c); Byte((byte)(c >> 8)); } Byte(0); }
        void Walk(JsonNode? x)
        {
            switch (x)
            {
                case null: Byte(1); break;
                case JsonObject o:
                    Byte(2);
                    foreach (var (k, v) in o.OrderBy(kv => kv.Key, StringComparer.Ordinal)) { Str(k); Walk(v); }
                    Byte(3);
                    break;
                case JsonArray a:
                    Byte(4);
                    foreach (var v in a) Walk(v);
                    Byte(5);
                    break;
                default:
                    Byte(6);
                    Str(x.ToJsonString());
                    break;
            }
        }
        Walk(n);
        return h.ToString("x16");
    }
}
