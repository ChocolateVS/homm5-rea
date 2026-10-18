using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using H5.Adventure.Map;
using H5.Save;
using H5Reborn.Adventure;
using H5Reborn.Campaign;
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
    public static string? CannotSave(AdventureScreen screen) => AdventureSave.CannotSaveReason(screen.Session.Game);

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
    public static string? Save(AdventureScreen screen, string name, byte[]? thumbnail)
    {
        if (CannotSave(screen) is { } reason) return reason;
        var session = screen.Session;
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
                Map = AdventureSave.SpecFor(session.Map),
                Name = name,
                Kind = campaign != null ? SaveKind.Campaign : SaveKind.Scenario,
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

    /// <summary>Autosave at the start of a human player's turn (H5_Game.exe FUN_00607d50, "autosave" option).</summary>
    public static void AutoSave(AdventureScreen screen)
    {
        if (CannotSave(screen) != null) return;
        var name = SaveSlots.Format(Text("UI/Common/autosavename.txt", "Autosave <value=id>"), screen.Session.S.Day.ToString());
        Save(screen, name, CaptureThumbnail(screen.GetViewport()));
    }

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
            var files = AdventureSave.OpenMap(save.Header.Map, fs);
            var map = new MapLoader(Game.Instance.Db, fs).Load(files);
            var game = AdventureSave.Restore(save, AdventureSession.SharedDb, map);
            var client = JsonNode.Parse(save.GetText(AdventureSave.ClientPart) ?? "{}")!;
            ulong seed = ulong.TryParse((string?)client["seed"], out var sd) ? sd : 1;
            ulong battleSeq = ulong.TryParse((string?)client["battleSeq"], out var bs) ? bs : 0;
            var session = AdventureSession.Restore(files, game, seed, battleSeq);
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
