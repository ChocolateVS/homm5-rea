using H5.Core.IO;
using H5.Net;

namespace H5.Server;

/// <summary>
/// Dedicated headless server: `h5server [--port N] [--name "My server"] [--game-dir path]`.
/// Requires a local game install only to verify clients run identical game data
/// and to load rules for authoritative game sessions.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        int port = ProtocolInfo.DefaultPort;
        string name = "H5Reborn server";
        string? gameDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--game-dir" when i + 1 < args.Length: gameDir = args[++i]; break;
                default:
                    Console.WriteLine("usage: h5server [--port N] [--name NAME] [--game-dir PATH]");
                    return 1;
            }
        }

        var install = GameInstall.Locate(gameDir);
        var fingerprint = install.DataFingerprint();
        static void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");
        Log($"game data {install.Root} fingerprint {fingerprint}");

        await using var server = new LobbyServer(SessionFactories.All(install), name, fingerprint, Log);
        server.Start(port);

        var quit = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
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
        var db = H5.Core.Data.GameDatabase.Load(PakFileSystem.OpenInstall(install));
        yield return new H5.Duel.DuelSessionFactory(db);
    }
}
