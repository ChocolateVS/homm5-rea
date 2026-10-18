using Godot;

namespace H5Reborn.Ui;

/// <summary>Command-line switches (after `--`) that drive the front end for automated tests.</summary>
public static class DevArgs
{
    public static string? Goto, Name, Connect, CreateRoom;
    public static bool Host, JoinFirst, Ready, AutoStart;
    /// <summary>Multiplayer test switches: `--master=URL`, `--host-online`, `--join-code=CODE`,
    /// `--join-lan-first`, `--net-loopback` (bind hosts/LAN discovery to 127.0.0.1 only, so automated
    /// tests never open the machine's network ports).</summary>
    public static string? Master, JoinCode;
    public static bool HostOnline, JoinLanFirst, NetLoopback;
    public static int? Preset;
    /// <summary>`--click=x,y@seconds` (repeatable): synthetic left clicks for UI tests.</summary>
    public static readonly List<(Vector2 Pos, double At)> Clicks = new();
    static bool _parsed;

    public static void Parse()
    {
        if (_parsed) return;
        _parsed = true;
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            string V(string p) => a[p.Length..];
            if (a.StartsWith("--goto=")) Goto = V("--goto=");
            else if (a.StartsWith("--name=")) Name = V("--name=");
            else if (a.StartsWith("--connect=")) Connect = V("--connect=");
            else if (a.StartsWith("--create-room=")) CreateRoom = V("--create-room=");
            else if (a.StartsWith("--preset=") && int.TryParse(V("--preset="), out var p)) Preset = p;
            else if (a.StartsWith("--click="))
            {
                var parts = V("--click=").Split('@');
                var xy = parts[0].Split(',');
                Clicks.Add((new Vector2(float.Parse(xy[0]), float.Parse(xy[1])),
                    parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1));
            }
            else if (a.StartsWith("--master=")) Master = V("--master=");
            else if (a.StartsWith("--join-code=")) JoinCode = V("--join-code=");
            else if (a == "--host-online") { Host = true; HostOnline = true; }
            else if (a == "--join-lan-first") JoinLanFirst = true;
            else if (a == "--net-loopback") NetLoopback = true;
            else if (a == "--host") Host = true;
            else if (a == "--join-first") JoinFirst = true;
            else if (a == "--ready") Ready = true;
            else if (a == "--autostart") AutoStart = true;
        }
    }
}
