using H5.Core.Xdb;

namespace H5Reborn.Adventure;

/// <summary>
/// The stock adventure-map strings (Text/Interface/*, Text/Game/HeroStrings/*, Text/Chest/*,
/// Text/WeekTexts/*), with the original &lt;value=name&gt; placeholders filled in. Every popup and
/// sign the adventure screen shows takes its wording from here, never from hard-coded English.
/// </summary>
public static class AdvTexts
{
    static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Get(string path, params (string Name, object Value)[] values)
    {
        if (!_cache.TryGetValue(path, out var text))
        {
            text = Game.Instance.Xdb.ReadText("/" + path)?.Replace("\r", "").Replace("\\n", "\n").TrimEnd('\n') ?? "";
            _cache[path] = text;
        }
        foreach (var (name, value) in values)
            text = text.Replace($"<value={name}>", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        return text;
    }

    public const string CreaturesJoin = "Text/Game/HeroStrings/Dialogs/CreaturesJoin.txt";
    public const string CreaturesJoinMoney = "Text/Game/HeroStrings/Dialogs/CreaturesJoinMoney.txt";
    public const string FleeTitle = "Text/Interface/JoinFlee/Flee_Title_Text.txt";
    public const string FleeDesc = "Text/Interface/JoinFlee/Flee_Desc_Text.txt";
    public const string ChestTitle = "Text/Chest/TreasureChest.txt";
    public const string ChestDesc = "Text/Chest/Desc.txt";
    public const string ChestGold = "Text/Chest/GoldPieces.txt";
    public const string ChestExp = "Text/Chest/Experience.txt";
    public const string StartOfWeek = "Text/Game/start-of-week.txt";
    public const string StartOfWeekFade = "Text/Interface/FadedText/StartOfWeek.(Text).txt";
    public const string Victory = "Text/Interface/WinLoose/DefaultVictoryMessage.(Text).txt";
    public const string Defeat = "Text/Interface/WinLoose/DefaultDefeatMessage.(Text).txt";
    public const string LevelUp = "Text/Interface/LevelUPBox/LevelUP.txt";
    public const string FlyingSignFormat = "Text/Interface/FlyingSignFormat_Text.txt";
    public const string AlreadyVisited = "Text/Game/BuildingsCommon/AlreadyVisitedThisWeek.txt";

    /// <summary>Resource names as the treasure piles name them (Text/Game/Treasures/&lt;Res&gt;/Name.txt).</summary>
    static readonly string[] ResourceFolders = { "Wood", "Ore", "Mercury", "Crystals", "Sulfur", "Gems", "Gold" };

    static Dictionary<string, string>? _rpg;

    /// <summary>A text id of GameMechanics/RPGStats/RPGRoot.xdb (e.g. GRAAL_NOTHING_HERE), or "" when unknown.</summary>
    public static string Rpg(string id)
    {
        var path = RpgPath(id);
        return path.Length > 0 ? (Ui.UiResources.Instance.Text(path) ?? "").Trim() : "";
    }

    static Dictionary<string, string>? _rpgIcons;

    /// <summary>
    /// An RPGRoot.xdb icon (e.g. ICON_MANA) by ID — FUN_00bcb8b0 searches SRPGRoot +0x80 (0x18-byte items: ID, Texture).
    /// </summary>
    public static Godot.Texture2D? RpgIcon(string id)
    {
        if (_rpgIcons == null)
        {
            _rpgIcons = new(StringComparer.Ordinal);
            const string root = "GameMechanics/RPGStats/RPGRoot.xdb";
            foreach (var item in Game.Instance.Xdb.Load(root).Descendants("Item"))
                if ((string?)item.Element("ID") is { } key && (string?)item.Element("Texture")?.Attribute("href") is { Length: > 0 } href)
                    _rpgIcons.TryAdd(key, href);
        }
        return _rpgIcons.TryGetValue(id, out var h) ? Game.Instance.LoadTexture(h) : null;
    }

    /// <summary>The text file behind an RPGRoot.xdb text id, or "".</summary>
    public static string RpgPath(string id)
    {
        if (_rpg == null)
        {
            _rpg = new(StringComparer.Ordinal);
            const string root = "GameMechanics/RPGStats/RPGRoot.xdb";
            foreach (var item in Game.Instance.Xdb.Load(root).Descendants("Item"))
                if ((string?)item.Element("ID") is { } key && (string?)item.Element("TextFileRef")?.Attribute("href") is { } href)
                    _rpg.TryAdd(key, XdbRef.Parse(href, root).Path);
        }
        return _rpg.TryGetValue(id, out var path) ? path.TrimStart('/') : "";
    }

    public static string ResourceName(int index)
    {
        if (index < 0 || index >= ResourceFolders.Length) return "";
        var name = Get($"Text/Game/Treasures/{ResourceFolders[index]}/Name.txt");
        return name.Length > 0 ? name : ResourceFolders[index];
    }

    /// <summary>Moon week display name (Text/WeekTexts/&lt;id&gt;-name.txt), e.g. "Week of Air".</summary>
    public static string WeekName(string moonWeek) =>
        string.IsNullOrEmpty(moonWeek) ? "" : Get($"Text/WeekTexts/{moonWeek}-name.txt");

    public static string WeekDescription(string moonWeek) =>
        string.IsNullOrEmpty(moonWeek) ? "" : Get($"Text/WeekTexts/{moonWeek}-desc.txt");
}
