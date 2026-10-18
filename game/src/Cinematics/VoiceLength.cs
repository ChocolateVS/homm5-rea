using H5.Core.Xdb;
using H5Reborn.Audio;

namespace H5Reborn.Cinematics;

/// <summary>
/// Length in seconds of an original sound (speech lines time dialog sentences).
/// Godot reports 0 for Ogg Vorbis streams loaded from memory, so Ogg lengths are read from
/// the file itself: the last page's granule position divided by the identification
/// header's sample rate.
/// </summary>
public static class VoiceLength
{
    static readonly Dictionary<string, double> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static double Of(string soundHref)
    {
        var path = XdbRef.Parse(soundHref).Path;
        if (Cache.TryGetValue(path, out var known)) return known;
        double len = SoundLibrary.Load(soundHref)?.Stream.GetLength() ?? 0;
        var bytes = len <= 0 ? ReadBytes(path) : null;
        if (bytes != null) len = OggSeconds(bytes);
        if (Godot.OS.GetCmdlineUserArgs().Contains("--script-verbose"))
            Godot.GD.Print($"[H5] voice {path}: bytes {bytes?.Length ?? -1} len {len:F2}");
        Cache[path] = len;
        return len;
    }

    static byte[]? ReadBytes(string path)
    {
        var fs = Game.Instance.Fs;
        if (!fs.Exists(path)) return null;
        var uid = Game.Instance.Xdb.Load(path).Root?.Element("uid")?.Value;
        if (string.IsNullOrEmpty(uid)) return null;
        foreach (var dir in new[] { "bin/SoundsLoc/", "bin/Sounds/" })
            if (fs.TryReadAllBytes(dir + uid, out var b)) return b;
        return null;
    }

    public static double OggSeconds(byte[] b)
    {
        if (b.Length < 64 || b[0] != 'O' || b[1] != 'g' || b[2] != 'g' || b[3] != 'S') return 0;
        // Identification header: "\x01vorbis", version (4), channels (1), sample rate (4).
        int id = IndexOf(b, "\u0001vorbis"u8.ToArray(), 0);
        if (id < 0 || id + 16 > b.Length) return 0;
        int rate = BitConverter.ToInt32(b, id + 7 + 4 + 1);
        if (rate <= 0) return 0;
        for (int p = b.Length - 27; p >= 0; p--)
            if (b[p] == 'O' && b[p + 1] == 'g' && b[p + 2] == 'g' && b[p + 3] == 'S')
            {
                long granule = BitConverter.ToInt64(b, p + 6);
                return granule > 0 ? granule / (double)rate : 0;
            }
        return 0;
    }

    static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        for (int i = start; i + needle.Length <= hay.Length; i++)
        {
            int k = 0;
            while (k < needle.Length && hay[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }
}
