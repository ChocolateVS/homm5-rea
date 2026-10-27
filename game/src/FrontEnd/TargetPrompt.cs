using Godot;
using H5.Core.IO;

namespace H5Reborn.FrontEnd;

/// <summary>
/// Startup choice between the stock Tribes of the East 3.1 install and the MMH 5.5 install (H5Reborn launcher feature,
/// user-requested; the original game has one install per exe). Asked on every start when both installs are found and
/// nothing on the command line or in the environment decides, before any other data is used (Boot shows it before the
/// intro movies). The last answer is the preselected side next time (user://launcher.cfg).
/// Skipped by: H5_GAME_DIR, <c>--target=stock|mmh55</c>, H5_TARGET, a single install, headless runs and any dev/test
/// switch (--adventure=, --combat-vs-ai=, --screenshot=, …); <c>--target-prompt</c> forces it (tests).
/// </summary>
public static class TargetPrompt
{
    const string File = "user://launcher.cfg";
    const string ForceArg = "--target-prompt";
    static readonly string[] PlayerArgs = { GameTargets.TargetArg, ForceArg, "--prompt-click=", "--play-intro", "--skip-intro", "--no-splash" };

    public const string StockLabel = "Heroes V: Tribes of the East 3.1";
    public const string Mmh55Label = "MMH 5.5";

    public static GameTargets.Selection Compute()
    {
        var args = OS.GetCmdlineUserArgs();
        bool force = args.Contains(ForceArg);
        bool dev = args.Any(a => !PlayerArgs.Any(p => a.StartsWith(p, StringComparison.Ordinal)));
        var explicitRoot = OS.GetEnvironment("H5_GAME_DIR");
        return GameTargets.Resolve(new GameTargets.Inputs
        {
            ExplicitRoot = string.IsNullOrEmpty(explicitRoot) ? null : explicitRoot,
            Forced = GameTargets.FromArgs(args) ?? GameTargets.Parse(OS.GetEnvironment("H5_TARGET")),
            Configs = GameInstall.ReadGameDirFiles(),
            Defaults = GameInstall.DefaultLocations(),
            Remembered = Remembered(),
            NonInteractive = !force && (dev || DisplayServer.GetName() == "headless"),
        });
    }

    static GameTarget? Remembered()
    {
        var cfg = new ConfigFile();
        return cfg.Load(File) == Error.Ok ? GameTargets.Parse(cfg.GetValue("target", "last", "").AsString()) : null;
    }

    static void Remember(GameTarget t)
    {
        var cfg = new ConfigFile();
        cfg.Load(File);
        cfg.SetValue("target", "last", GameTargets.Name(t));
        cfg.Save(File);
    }

    /// <summary>
    /// The prompt: the stock two-choice box (UI/CommonBoxes Box2p, as the exe's choice dialogs), the remembered side
    /// preselected so OK / Enter takes it. Remembers the answer and switches the mounted install before
    /// <paramref name="done"/> continues the boot.
    /// </summary>
    public static void Show(Control parent, GameTargets.Selection sel, Action done)
    {
        Adventure.AdvDialogs.Choose2(parent, "H5Reborn",
            $"<center>Which game do you want to play?<br><br>{StockLabel}:<br>{sel.StockRoot}<br><br>{Mmh55Label}:<br>{sel.Mmh55Root}",
            null,
            (null, StockLabel),
            (null, Mmh55Label),
            picked =>
            {
                var t = picked == 0 ? GameTarget.Stock : GameTarget.Mmh55;
                Remember(t);
                Game.Instance.ChooseTarget(t);
                GD.Print($"[H5] Startup target: {GameTargets.Name(t)}");
                done();
            },
            initial: sel.DefaultChoice == GameTarget.Stock ? 0 : 1);
        // Test: `--prompt-click=x,y@seconds` (repeatable) clicks the prompt (off-screen runs have no mouse).
        foreach (var a in OS.GetCmdlineUserArgs().Where(a => a.StartsWith("--prompt-click=", StringComparison.Ordinal)))
        {
            var parts = a["--prompt-click=".Length..].Split('@');
            var xy = parts[0].Split(',');
            var pos = new Vector2(float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture));
            double at = parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1;
            parent.GetTree().CreateTimer(at).Timeout += () => MainMenu.Click(pos);
        }
    }
}
