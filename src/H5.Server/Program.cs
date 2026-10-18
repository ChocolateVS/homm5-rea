using System.Net;
using H5.Core.IO;
using H5.Net;
using H5.Net.Hosting;

namespace H5.Server;

/// <summary>
/// Dedicated headless server:
///   h5server [--port N] [--name "My server"] [--game-dir PATH] [--bind ADDR]
///            [--no-lan] [--public MASTER_URL] [--map-ports] [--password]
/// One port serves native clients (TCP), browsers/proxies (WebSocket on the same TCP
/// port) and NAT-traversed players (UDP). --public lists the server on a master so
/// internet players can find it and join by code.
/// Requires a local game install to verify clients run identical game data and to
/// load rules for authoritative game sessions.
/// </summary>
public static class Program
{
    const string Usage = "usage: h5server [--port N] [--name NAME] [--game-dir PATH] [--bind ADDR] [--no-lan] [--public MASTER_URL] [--map-ports]";

    public static async Task<int> Main(string[] args)
    {
        int port = ProtocolInfo.DefaultPort;
        string name = "H5Reborn server";
        string? gameDir = null, master = Environment.GetEnvironmentVariable("H5_MASTER");
        var bind = IPAddress.Any;
        bool lan = true, mapPorts = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--game-dir" when i + 1 < args.Length: gameDir = args[++i]; break;
                case "--bind" when i + 1 < args.Length: bind = IPAddress.Parse(args[++i]); break;
                case "--public" when i + 1 < args.Length: master = args[++i]; break;
                case "--no-lan": lan = false; break;
                case "--map-ports": mapPorts = true; break;
                default: Console.WriteLine(Usage); return 1;
            }
        }

        var install = GameInstall.Locate(gameDir);
        var fingerprint = install.DataFingerprint();
        static void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");
        Log($"game data {install.Root} fingerprint {fingerprint}");

        await using var host = GameHost.Start(new GameHostOptions
        {
            Name = name, Port = port, Bind = bind, Fingerprint = fingerprint, LanDiscovery = lan,
        }, SessionFactories.All(install), Log);

        if (!string.IsNullOrWhiteSpace(master))
        {
            try
            {
                var online = await host.PublishOnlineAsync(H5.Net.Master.MasterClient.ParseAddress(master), "duel", 8, false, mapPorts);
                Log($"listed on {master} — join code {online.Lobby.Code}");
            }
            catch (Exception e) { Log($"could not publish on {master}: {e.Message}"); }
        }

        var quit = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => quit.TrySetResult();
        await quit.Task;
        Log("shutting down");
        return 0;
    }
}

/// <summary>Game modes offered by this server.</summary>
public static class SessionFactories
{
    public static IEnumerable<IGameSessionFactory> All(GameInstall install)
    {
        var fs = PakFileSystem.OpenInstall(install);
        var db = H5.Core.Data.GameDatabase.Load(fs);
        yield return new H5.Duel.DuelSessionFactory(db);
        yield return new H5.AdvNet.AdventureSessionFactory(new H5.AdvNet.AdvMapSource(db, fs, install));
    }
}
