using H5.Adventure.Data;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Save;

/// <summary>
/// Captures and restores an adventure game. The rules state (AdvState, including its
/// RNG, fog and every object's state) is the core; the Lua script state (heap and
/// threads mid-sleep, from AdventureScripting.Save), the campaign context and the
/// client's own bookkeeping (battle seed counter etc.) are stored as separate parts so
/// each layer restores itself. Restoring verifies the state hash recorded at save time.
/// </summary>
public static class AdventureSave
{
    public const string AdventurePart = "adventure.json";
    public const string ScriptsPart = "scripts.json";
    public const string CampaignPart = "campaign.json";
    public const string ClientPart = "client.json";
    public const string ThumbnailPart = "thumbnail.png";

    /// <summary>Stock map sizes (tiles per side) for SaveInfo's "Map Size" field.</summary>
    static readonly int[] SizeTiles = { 72, 96, 136, 176, 256 };

    public static int MapSizeClass(AdvMap map)
    {
        int side = Math.Max(map.TilesX, map.TilesY);
        for (int i = 0; i < SizeTiles.Length; i++) if (side <= SizeTiles[i]) return i;
        return SizeTiles.Length - 1;
    }

    public sealed class CaptureInput
    {
        public required H5.Adventure.Engine.Adventure Game;
        public required MapSpec Map;
        public required string Name;
        public SaveKind Kind = SaveKind.Scenario;
        public string DataFingerprint = "";
        public DateTime SavedUtc = DateTime.UtcNow;
        public string? Scripts;
        public string? Campaign;
        public string? Client;
        public byte[]? Thumbnail;
        public string? CampaignName, CampaignDescription;
        public int MissionIndex = -1, MissionsCount;
    }

    /// <summary>
    /// The original refuses to save while a battle is being fought; the engine marks that
    /// with a pending battle. Returns a reason when saving is not possible now.
    /// </summary>
    public static string? CannotSaveReason(H5.Adventure.Engine.Adventure game) =>
        game.S.Battle != null ? "battle in progress"
        : game.S.Finished ? "game is over"
        : null;

    public static SaveArchive Capture(CaptureInput c)
    {
        var s = c.Game.S;
        var map = c.Game.Map;
        var save = new SaveArchive
        {
            Header = new SaveHeader
            {
                Name = c.Name,
                Kind = c.Kind,
                SavedUtc = c.SavedUtc,
                DataFingerprint = c.DataFingerprint,
                Map = c.Map,
                MapName = map.Name,
                MapDescription = map.Description,
                MapSize = MapSizeClass(map),
                Players = s.Players.Count,
                Difficulty = s.Difficulty,
                Day = s.Day,
                CampaignName = c.CampaignName,
                CampaignDescription = c.CampaignDescription,
                MissionIndex = c.MissionIndex,
                MissionsCount = c.MissionsCount,
                StateHash = s.ComputeHash(),
                HasThumbnail = c.Thumbnail is { Length: > 0 },
            },
        };
        save.SetText(AdventurePart, s.Serialize());
        if (c.Scripts != null) save.SetText(ScriptsPart, c.Scripts);
        if (c.Campaign != null) save.SetText(CampaignPart, c.Campaign);
        if (c.Client != null) save.SetText(ClientPart, c.Client);
        if (c.Thumbnail is { Length: > 0 } png) save.Parts[ThumbnailPart] = png;
        return save;
    }

    /// <summary>Recreates the rules engine on a freshly loaded copy of the save's map.</summary>
    public static H5.Adventure.Engine.Adventure Restore(SaveArchive save, AdventureDb db, AdvMap map)
    {
        var json = save.GetText(AdventurePart) ?? throw new SaveCorruptException("save holds no adventure state");
        var state = AdvState.Deserialize(json);
        if (state.ComputeHash() != save.Header.StateHash)
            throw new SaveCorruptException("restored state does not match the saved state");
        return new H5.Adventure.Engine.Adventure(db, map, state);
    }

    /// <summary>Reopens the map named by a save (stock pak map or user .h5m).</summary>
    public static MapFiles OpenMap(MapSpec spec, H5.Core.IO.PakFileSystem fs)
    {
        if (spec.Kind != "file") throw new NotSupportedException($"cannot reopen a '{spec.Kind}' map yet");
        return spec.Source.EndsWith(".h5m", StringComparison.OrdinalIgnoreCase)
            ? MapFiles.FromH5m(fs, spec.Source)
            : MapFiles.FromPak(fs, spec.Source);
    }

    public static MapSpec SpecFor(AdvMap map) => new()
    {
        Kind = "file",
        Id = map.Id,
        Source = map.Source,
        IsUserMap = map.Source.EndsWith(".h5m", StringComparison.OrdinalIgnoreCase),
    };
}
