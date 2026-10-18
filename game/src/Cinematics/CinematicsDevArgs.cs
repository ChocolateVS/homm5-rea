using Godot;

namespace H5Reborn.Cinematics;

/// <summary>
/// Dev launch switches handled by the boot scene (after `--`):
/// `--dialog-scene=&lt;DialogScene.xdb&gt;` plays a dialog scene (optionally
/// `--dialog-sentence=N --dialog-at=0.5` to freeze on a sentence for screenshots),
/// `--campaign-menu` opens Single Player → Campaign, `--pwl=&lt;campaign&gt;,&lt;mission&gt;`
/// opens the pre-mission screen, `--mission-results=win|lose` shows the results window.
/// </summary>
public static class CinematicsDevArgs
{
    static string? Arg(string name) =>
        OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    static bool Flag(string name) => OS.GetCmdlineUserArgs().Contains(name);

    /// <summary>Returns true if a cinematics/campaign dev switch took over the boot scene.</summary>
    public static bool TryStart(Control boot)
    {
        if (Arg("--dialog-scene") is { } scene)
        {
            var player = DialogScenePlayer.Open(boot, scene, () => GD.Print("[H5] dialog scene finished"));
            if (player != null && Arg("--dialog-sentence") is { } s)
            {
                int n = int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                float at = float.Parse(Arg("--dialog-at") ?? "0.5", System.Globalization.CultureInfo.InvariantCulture);
                // _Ready already ran inside AddChild; seek on the next frame, then freeze.
                Callable.From(() => { player.Seek(n, at); player.SetProcess(false); }).CallDeferred();
            }
            return true;
        }
        if (Flag("--campaign-menu"))
        {
            Callable.From(() =>
            {
                var tree = (SceneTree)Engine.GetMainLoop();
                tree.ChangeSceneToFile("res://scenes/MainMenu.tscn");
                Campaign.CampaignMenu.OpenOnStart = true;
            }).CallDeferred();
            return true;
        }
        if (Arg("--pwl") is { } pwl)
        {
            var parts = pwl.Split(',');
            Callable.From(() => Campaign.CampaignFlow.OpenPwl(boot, parts[0], int.Parse(parts.ElementAtOrDefault(1) ?? "0"))).CallDeferred();
            return true;
        }
        if (Arg("--mission-results") is { } res)
        {
            Callable.From(() => boot.AddChild(new Campaign.MissionResultsScreen(res == "win", "A2C1", 0, () => GD.Print("[H5] results closed")))).CallDeferred();
            return true;
        }
        return false;
    }
}
