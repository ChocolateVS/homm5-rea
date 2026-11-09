using Godot;
using Engine = twodog.Engine;

/// <summary>
/// Browser entry point (2dog): waits until the page has the player's install files (picked or from the
/// browser's storage), exposes them to the game as a virtual install, then starts Godot with the game's own
/// project (Boot scene, Game autoload) exactly as on the desktop.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("H5Reborn (web) starting...");
        // The game assembly owns the source-generated plugins initializer; there is no GodotPlugins.dll on web.
        Engine.RegisterWebPluginsInitializer(TwoDogWebBoot.PluginsInitializer());

        await H5Reborn.Web.BrowserData.PrepareAsync();

        // args: the page's GODOT_CONFIG.args ("--" + the game's dev args from ?args=) after the
        // '--main-pack godot.pck' the engine loader prepends.
        var engine = new Engine("H5Reborn", args: args);
        try
        {
            engine.Start();
            GD.Print("[H5] web: engine started");
            // The browser's frame loop owns the lifetime after Run() returns.
            engine.Run();
        }
        catch
        {
            await engine.DisposeAsync();
            throw;
        }
        return 0;
    }
}
