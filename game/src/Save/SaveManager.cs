using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using H5.Adventure.Map;
using H5.Save;
using H5Reborn.Adventure;
using H5Reborn.Campaign;
using H5Reborn.Options;
using H5Reborn.Profiles;
using H5Reborn.Ui;

namespace H5Reborn.Save;

/// <summary>
/// Saving and loading adventure games for the client. Saves go to the selected profile's
/// folder (the original's profiles\&lt;name&gt;\Saves\) through <see cref="SaveSlots"/>, which
/// follows the stock naming (save_N.sav, "quick" quicksave, "Autosave N").
/// </summary>
public static class SaveManager
{
    static readonly ConditionalWeakTable<AdventureSession, string> _pendingScripts = new();

    public static ISaveStorage Storage => new GodotSaveStorage(ProfileManager.SavesDir(ProfileManager.CurrentName));

    public static string Text(string path, string fallback) => UiResources.Instance.Text(path)?.Trim() is { Length: > 0 } t ? t : fallback;

    /// <summary>UI/Common/defaultsavename.txt — the quicksave's name.</summary>
    public static string QuickName => Text("UI/Common/defaultsavename.txt", "quick");

    // ---------------------------------------------------------------- saving

    /// <summary>Why the game cannot be saved right now, or null.</summary>
    /// <remarks>
    /// The stock exe also saves network games (CSaveScreen&lt;SLanSaveHeaderInfo/SUbicomSaveHeaderInfo&gt;,
    /// reloaded by the host through CMultiplayerHostLoadLogic); H5Reborn cannot restore a network game
    /// yet, so it refuses instead of writing a single-player save of the replica (docs/rules/AUDIT_CAMPAIGN.md).
    /// </remarks>
    public static string? CannotSave(AdventureScreen screen, bool beforeCombat = false) =>
        screen.Session.Remote != null ? null
        : beforeCombat && screen.Session.S.Battle != null && !screen.Session.S.Finished ? null
        : AdventureSave.CannotSaveReason(screen.Session.Game);

    /// <summary>
    /// The save list's thumbnail (SaveInfo/ScreenShoot, 274x180): the current frame,
    /// scaled to cover and centre-cropped.
    /// </summary>
    public static byte[]? CaptureThumbnail(Viewport viewport)
    {
        try
        {
            var img = viewport.GetTexture().GetImage();
            if (img == null || img.IsEmpty()) return null;
            const int w = 274, h = 180;
            float scale = Math.Max((float)w / img.GetWidth(), (float)h / img.GetHeight());
            int sw = Math.Max(w, (int)Math.Ceiling(img.GetWidth() * scale)), sh = Math.Max(h, (int)Math.Ceiling(img.GetHeight() * scale));
            img.Resize(sw, sh, Image.Interpolation.Bilinear);
            var crop = img.GetRegion(new Rect2I((sw - w) / 2, (sh - h) / 2, w, h));
            return crop.SavePngToBuffer();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[H5] save thumbnail: {e.Message}");
            return null;
        }
    }

    /// <summary>Saves the game under <paramref name="name"/> (overwriting a save of that name). Returns an error or null.</summary>
    public static string? Save(AdventureScreen screen, string name, byte[]? thumbnail, bool beforeCombat = false)
    {
        if (CannotSave(screen, beforeCombat) is { } reason) return reason;
        var session = screen.Session;
        // Network game: the save is ordered from the host, which writes it when every player has ended the turn
        // (CAnnounceSaveGameOrderedCmd); the order and the result come back as notices (ShowNetSaveNotice).
        if (session.Remote is { } remote)
        {
            if (beforeCombat) return null;
            remote.Net.OrderSave(name);
            return null;
        }
        try
        {
            var bridge = screen.GetNodeOrNull<ScriptUiBridge>("ScriptUiBridge");
            var campaign = CampaignFlow.Active is { } a && a.Session == session ? a : null;
            var client = new JsonObject
            {
                ["seed"] = session.Seed.ToString(),
                ["battleSeq"] = session.BattleSeq.ToString(),
            };
            string? campaignJson = campaign == null ? null : new JsonObject
            {
                ["campaign"] = campaign.Campaign.InternalName,
                ["mission"] = campaign.Mission.Index,
                ["bonus"] = campaign.BonusIndex,
            }.ToJsonString();
            var save = AdventureSave.Capture(new AdventureSave.CaptureInput
            {
                Game = session.Game,
                Map = session.Generator != null ? GeneratedSpec(session.Map, session.Generator) : AdventureSave.SpecFor(session.Map),
                Name = name,
                Kind = campaign != null ? SaveKind.Campaign : session.IsHotSeat ? SaveKind.HotSeat : SaveKind.Scenario,
                DataFingerprint = Game.Instance.Install.DataFingerprint(),
                Scripts = bridge?.Host?.Save(),
                Campaign = campaignJson,
                Client = client.ToJsonString(),
                Thumbnail = thumbnail,
                CampaignName = campaign?.Campaign.Name,
                CampaignDescription = campaign?.Campaign.Comment,
                MissionIndex = campaign?.Mission.Index ?? -1,
                MissionsCount = campaign?.Campaign.Missions.Count ?? 0,
            });
            var file = SaveSlots.Write(Storage, save);
            GD.Print($"[H5] saved \"{name}\" to {file} (day {session.S.Day})");
            ShowSaveComplete(screen);
            return null;
        }
        catch (Exception e)
        {
            GD.PushError($"[H5] save failed: {e}");
            return e.Message;
        }
    }

    public static void QuickSave(AdventureScreen screen)
    {
        if (Save(screen, QuickName, CaptureThumbnail(screen.GetViewport())) is { } error)
            screen.Message(Text("UI/SaveLoad/SaveErrorTitle.txt", "Cannot create a save"), error);
    }

    /// <summary>
    /// Autosave when a human player is to act (H5_Game.exe 006082f0 at the new turn, 00607d50 at each player change
    /// when world settings +0x1c — the hot-seat flag — is set; both need the "autosave" var). Named by
    /// <see cref="RotatingName"/> from UI/Common/autosavename.txt.
    /// </summary>
    public static void AutoSave(AdventureScreen screen)
    {
        if (CannotSave(screen) != null) return;
        Save(screen, RotatingName(Storage, Text("UI/Common/autosavename.txt", "Autosave <value=id>")), CaptureThumbnail(screen.GetViewport()));
    }

    /// <summary>
    /// "save_before_combat" (var, default 0 in user_a2.cfg): 005a5d90 saves when a battle starts between a human
    /// and a computer side (player vf 0x190 differs), named from UI/Common/save_before_combat.txt. The save holds
    /// the pending battle, which starts again when the save is loaded.
    /// </summary>
    public static void SaveBeforeCombat(AdventureScreen screen)
    {
        var s = screen.Session;
        if (s.IsRemote || s.S.Battle is not { } pb || !OptionsStore.GetBool("save_before_combat")) return;
        if (AdvNet.HotSeatBattles.HumanSides(s.S, pb).Length != 1) return;
        Save(screen, RotatingName(Storage, Text("UI/Common/save_before_combat.txt", "Before Combat <value=id>")), CaptureThumbnail(screen.GetViewport()), beforeCombat: true);
    }

    /// <summary>
    /// CSavesCreator vf 0x1c (007f9e10): the template is formatted with ids 1..3; the first name without a save is
    /// used, else the oldest of the three is overwritten.
    /// </summary>
    public static string RotatingName(ISaveStorage storage, string template) => SaveSlots.RotatingName(storage, template);

    /// <summary>
    /// 007ea770 → 007f84a0: after a save the "ID_SAVE_COMPLETE" text (UI/ID_SAVE_COMPLETE.txt) is shown for
    /// "save_ok_time" ms (var default 1000, 007ea1c0).
    /// </summary>
    static void ShowSaveComplete(AdventureScreen screen) =>
        ShowNotice(screen, Text("UI/ID_SAVE_COMPLETE.txt", "Save completed"), null);

    /// <summary>
    /// Network save notices: the order (ID_SAVEGAMEORDERED_EVENTMESSAGE, Text/Interface/FadedText/SaveGameOrdered
    /// "Save was ordered and will be done when every player will end the turn.") for every player, then the
    /// save complete text (or the host's error).
    /// </summary>
    public static void ShowNetSaveNotice(AdventureScreen screen, JsonObject n)
    {
        if (!GodotObject.IsInstanceValid(screen)) return;
        if (n["ordered"] != null)
            ShowNotice(screen, Text("Text/Interface/FadedText/SaveGameOrdered.(Text).txt", "Save was ordered and will be done when every player will end the turn."), 4000);
        else if (n["saved"] != null)
            ShowNotice(screen, Text("UI/ID_SAVE_COMPLETE.txt", "Save completed"), null);
        else if ((string?)n["error"] is { } error)
            screen.Message(Text("UI/SaveLoad/SaveErrorTitle.txt", "Cannot create a save"), error);
    }

    static void ShowNotice(AdventureScreen screen, string text, float? fixedMs)
    {
        if (!screen.IsInsideTree()) return;
        var label = new BitmapText
        {
            Markup = $"<h3><center>{Ui.Screens.ChatLogView.Escape(text)}", AnchorLeft = 0.25f, AnchorRight = 0.75f,
            AnchorTop = 0.45f, AnchorBottom = 0.5f, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        screen.AddChild(label);
        float ms = fixedMs ?? (float.TryParse(OptionsStore.Get("save_ok_time", "1000"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1000);
        screen.GetTree().CreateTimer(Math.Max(0.1, ms / 1000.0)).Timeout += () => { if (GodotObject.IsInstanceValid(label)) label.QueueFree(); };
    }

    /// <summary>A generated map is reopened by regenerating it from the generator options (deterministic, seeded).</summary>
    static MapSpec GeneratedSpec(AdvMap map, H5.AdvNet.RmgSettings rmg) => new()
    {
        Kind = "rmg", Id = map.Id, Source = "", Generator = new H5.AdvNet.AdvNetSettings { Rmg = rmg }.ToJson(),
    };

    // ---------------------------------------------------------------- loading

    /// <summary>The Lua state saved with the game, consumed once by ScriptUiBridge when the loaded map's scripts start.</summary>
    public static string? TakePendingScripts(AdventureSession session)
    {
        if (!_pendingScripts.TryGetValue(session, out var s)) return null;
        _pendingScripts.Remove(session);
        return s;
    }

    /// <summary>Loads a save and opens its adventure screen. Returns an error message or null.</summary>
    public static string? Load(string fileName)
    {
        SaveArchive save;
        try { save = SaveArchive.FromBytes(Storage.Read(fileName)); }
        catch (Exception e) when (e is SaveCorruptException or IOException)
        {
            GD.PushWarning($"[H5] load {fileName}: {e.Message}");
            return Text("UI/Common/save_wrong.txt", "Map no longer exists or the save is corrupted");
        }
        return Load(save);
    }

    public static string? Load(SaveArchive save)
    {
        try
        {
            var fs = Game.Instance.Fs;
            if (save.Header.DataFingerprint.Length > 0 && save.Header.DataFingerprint != Game.Instance.Install.DataFingerprint())
                GD.PushWarning("[H5] this save was made with different game data or mods");
            MapFiles files;
            AdvMap map;
            H5.AdvNet.RmgSettings? generator = null;
            if (save.Header.Map.Kind == "rmg" && save.Header.Map.Generator is JsonObject gen && H5.AdvNet.AdvNetSettings.From(gen).Rmg is { } rmg)
            {
                // Generated maps are regenerated from their options (the exe reopens Maps/RMG/<name>.h5m).
                var lm = AdvNet.AdvNetGame.Maps.Load(new H5.AdvNet.AdvNetSettings { Rmg = rmg });
                files = lm.Files ?? MapFiles.FromPak(fs, "Maps/Generated/map.xdb");
                map = lm.Map;
                generator = rmg;
            }
            else
            {
                files = AdventureSave.OpenMap(save.Header.Map, fs);
                map = new MapLoader(Game.Instance.Db, fs).Load(files);
            }
            var game = AdventureSave.Restore(save, AdventureSession.SharedDb, map);
            var client = JsonNode.Parse(save.GetText(AdventureSave.ClientPart) ?? "{}")!;
            ulong seed = ulong.TryParse((string?)client["seed"], out var sd) ? sd : 1;
            ulong battleSeq = ulong.TryParse((string?)client["battleSeq"], out var bs) ? bs : 0;
            var session = AdventureSession.Restore(files, game, seed, battleSeq, generator);
            if (save.GetText(AdventureSave.ScriptsPart) is { } scripts) _pendingScripts.AddOrUpdate(session, scripts);

            if (save.GetText(AdventureSave.CampaignPart) is { } cj && JsonNode.Parse(cj) is JsonObject c)
                CampaignFlow.Resume((string?)c["campaign"] ?? "", (int?)c["mission"] ?? -1, (int?)c["bonus"] ?? -1, session);
            else
                CampaignFlow.ClearActive();

            AdventureScreen.Open(session);
            GD.Print($"[H5] loaded \"{save.Header.Name}\" ({save.Header.MapName}, day {game.S.Day})");
            return null;
        }
        catch (Exception e) when (e is SaveCorruptException or IOException or InvalidDataException or JsonException or NotSupportedException or FileNotFoundException)
        {
            GD.PushWarning($"[H5] load failed: {e}");
            return Text("UI/Common/save_wrong.txt", "Map no longer exists or the save is corrupted");
        }
    }

    /// <summary>Quickload: the save named "quick" (UI/Common/defaultsavename.txt), as F8 does in the original.</summary>
    public static string? QuickLoad()
    {
        var entry = SaveSlots.FindByName(Storage, QuickName);
        return entry == null ? Text("UI/Common/save_wrong.txt", "Map no longer exists or the save is corrupted") : Load(entry.FileName);
    }

    /// <summary>Restart Mission: the same map (or campaign mission with its bonus) from the beginning.</summary>
    public static void Restart(AdventureScreen screen)
    {
        var session = screen.Session;
        if (CampaignFlow.Active is { } a && a.Session == session)
        {
            CampaignFlow.StartMission(a.Campaign, a.Mission, a.BonusIndex);
            return;
        }
        var source = session.Map.Source;
        var fresh = AdventureSession.Start(source, (ulong)Time.GetTicksUsec(), session.Human.Index, session.S.Difficulty);
        AdventureScreen.Open(fresh);
    }
}
