using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

public sealed partial class AdventureScripting
{
    /// <summary>Profile option tutorial_show (default 1 in the stock profile).</summary>
    public bool TutorialsEnabled { get; set; } = true;

    /// <summary>Length of one script sleep segment: GetSoundTimeInSleeps divides milliseconds by 200.</summary>
    public const int SegmentMilliseconds = 200;

    /// <summary>
    /// Functions shared by script contexts (registration table 0x00f39a0c) and the tutorial API
    /// (0x00f3e004), both registered in adventure-map scripts too.
    /// </summary>
    void RegisterSharedApi()
    {
        Reg("GetMapDataPath", c =>
        {
            var p = Data.MapXdb;
            int slash = p.LastIndexOf('/');
            c.Return("/" + (slash >= 0 ? p[..(slash + 1)] : ""));
        });
        Reg("GetSoundTimeInSleeps", c =>
        {
            int ms = SoundMilliseconds(c.Str(0));
            if (ms < 0) Log.Add($"GetSoundTimeInSleeps: Sound \"{c.Str(0)}\" was not found!");
            c.Return(Math.Max(0, ms) / SegmentMilliseconds);
        });

        Reg("IsTutorialEnabled", c => c.ReturnBool(TutorialsEnabled));
        Reg("IsTutorialItemEnabled", c => c.ReturnBool(TutorialsEnabled && !World.Settings.ContainsKey("tutorial_done:" + c.Str(0))));
        Reg("TutorialActivateHint", c => { if (TutorialsEnabled) Emit("tutorial_hint", ("id", c.Str(0))); });
        // 0x00773ee0: the second argument is a number, blinking while it is non-zero.
        Reg("TutorialSetBlink", c => Emit("tutorial_blink", ("id", c.Str(0)), ("on", c.Int(1) != 0)));
        Reg("TutorialMessageBox", c =>
        {
            if (!TutorialsEnabled) return;
            World.Settings["tutorial_done:" + c.Str(0)] = "1";
            RequestUi("tutorial_message", c.Str(0));
        });
        Reg("IsTutorialMessageBoxOpen", c => c.ReturnBool(World.PendingUi.Any(r => r.Kind == "tutorial_message")));
    }

    /// <summary>Duration of a (Sound) xdb's wave in ms, from its RIFF header; -1 if missing.</summary>
    int SoundMilliseconds(string href)
    {
        var r = H5.Core.Xdb.XdbRef.Parse(href);
        if (r.IsEmpty || !_game.TryReadAllBytes(r.Path, out var xdb)) return -1;
        var text = System.Text.Encoding.UTF8.GetString(xdb);
        var m = System.Text.RegularExpressions.Regex.Match(text, "<(?:Wave|SoundFile|FileName)[^>]*href=\"([^\"#]+)\"");
        if (!m.Success) return -1;
        var wav = H5.Core.Xdb.XdbRef.Parse(m.Groups[1].Value, r.Path).Path;
        if (!_game.TryReadAllBytes(wav, out var data) || data.Length < 44) return -1;
        // RIFF/WAVE: average bytes per second at offset 28, then find the "data" chunk size.
        int byteRate = BitConverter.ToInt32(data, 28);
        int p = 12;
        while (p + 8 <= data.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(data, p, 4);
            int size = BitConverter.ToInt32(data, p + 4);
            if (id == "data") return byteRate > 0 ? (int)(size * 1000L / byteRate) : -1;
            p += 8 + size + (size & 1);
        }
        return -1;
    }
}
