using System.Globalization;
using H5.Core.IO;
using H5.Scripting;

namespace H5Reborn.Options;

/// <summary>
/// MMH 5.5 player options in the options screen (MMH 5.5 target only). MMH55's own switches are the globals of
/// scripts/H55-Settings.lua (edited by hand or by MMH55_Utility); this lists every one of them from the mounted file,
/// in file order, with the file's names, values as defaults and comments as tooltips, and stores the player's changes in
/// the profile (<c>h55.&lt;name&gt;</c>). Changed values reach the scripts through
/// <see cref="H5.Scripting.Runtime.AdventureScripting.SettingOverrides"/> (single player) and
/// <c>AdvNetSettings.ScriptSettings</c> (the host's, network / hot-seat).
/// Value lists for the non-yes/no settings follow the file's comments where they give them ("Only accepts numbers
/// 0,1,2,3,4,5", the AICheatMode guidelines, the obelisk reward list); free numbers (multipliers, counts) get an
/// H5Reborn list of steps around the default, and a value already in the file is always offered.
/// H55_DimensionDoorsOnThisMap is not listed: the file says not to set it there (map scripts only).
/// The 55.dll lobby options (EE_*) and MMH55Stats.xml are DLL-only and not available (docs/mmh55/OPTIONS.md).
/// </summary>
public static class Mmh55Options
{
    public const string VarPrefix = "h55.";
    /// <summary>Page height in check-box rows (a combo box row is two): what fits in the GameOptions page panel.</summary>
    const int PageUnits = 14;

    static readonly HashSet<string> Bools = new(StringComparer.Ordinal)
    {
        "H55_TownGateEnabled", "H55_TownConvEnabled", "H55_GovernanceEnabled", "H55_MineControl", "H55_DimensionDoors",
        "H55_IgnoreExpAdjustment", "H55_NoMentoring", "H55_NoHillFortsOnARMG", "H55_CompetitiveARMGTowns",
        "H55_NoAutoTeamHumans", "H55_LowResGame", "H55_GenerateMixedStacks", "H55_AskForMixedStacks", "H55_ForceMixedStacks",
        "H55_PrisonersAdjustToHeroRace", "H55_ManaObservatories", "H55_NoArtifactMerchantsInTowns", "H55_NegativeArtifacts",
        "H55_MoonDiscQuest", "H55_MoonDiscOnARMGMaps", "H55_RPGPotions",
    };

    static readonly HashSet<string> Hidden = new(StringComparer.Ordinal) { "H55_DimensionDoorsOnThisMap" };

    static IEnumerable<(string Name, string Value)> Range(int from, int to, int step = 1) =>
        Enumerable.Range(0, (to - from) / step + 1).Select(i => (from + i * step).ToString(CultureInfo.InvariantCulture)).Select(v => (v, v));

    static IEnumerable<(string, string)> Multipliers() =>
        new[] { 0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 2.0, 2.5, 3.0 }
            .Select(d => d.ToString("0.0", CultureInfo.InvariantCulture)).Select(s => (s, s));

    static readonly string[] Rewards = { "random", "sword", "armor", "shield", "staff", "helmet", "wings", "book", "ring", "tear of asha" };

    /// <summary>Items (label, value) for a non-yes/no setting.</summary>
    static List<(string Name, string Value)> ItemsFor(string name) => name switch
    {
        // "Only accepts numbers 0,1,2,3,4,5"
        "H55_BuildNeutralTowns" or "H55_BuildPlayerTowns" => Range(0, 5).ToList(),
        // "Only accepts numbers 0,1,2,3,4,5,6" + GUIDELINES: 0 = always off, 1 = 40% … 4 = 100% default … 6 = 140%.
        "H55_AICheatMode" => new[] { "Off", "40%", "60%", "80%", "100%", "120%", "140%" }.Select((l, i) => (l, i.ToString(CultureInfo.InvariantCulture))).ToList(),
        // "only accepts numbers 0,…,9" / "Setting 0 = random, 1 = sword, … 9 = tear of asha."
        "H55_ForcedObelisk" or "H55_ForcedShantiri" => Rewards.Select((l, i) => (l, i.ToString(CultureInfo.InvariantCulture))).ToList(),
        // Multipliers ("do not set to zero"): H5Reborn steps.
        "H55_GovernorExpCoef" or "H55_BanksDifficulty" or "H55_NeutralStackSize" => Multipliers().ToList(),
        // "Max level = 50!"
        "H55_Duel_HeroLevel" => Range(1, 50).ToList(),
        // Counts ("zero can also be used" / "can be set to zero"): H5Reborn ranges.
        "H55_AmountExtraTownsDwellings" => Range(0, 10).ToList(),
        "H55_Duel_Relics" or "H55_Duel_Majors" or "H55_Duel_Minors" => Range(0, 20).ToList(),
        "H55_Duel_WeeksGrowth" or "H55_Duel_BankDifficulty" => Range(1, 20).ToList(),
        "H55_MixStacksThreshold" => Range(50, 1000, 50).ToList(),
        _ => new(),
    };

    static string? _loadedFor;
    static List<Mmh55Setting> _settings = new();

    /// <summary>The settings of the mounted install's scripts/H55-Settings.lua (empty on the stock target).</summary>
    public static IReadOnlyList<Mmh55Setting> Settings
    {
        get
        {
            var root = Game.Instance.Install.Root;
            if (_loadedFor == root) return _settings;
            _loadedFor = root;
            _settings = Game.Instance.Target == GameTarget.Mmh55 && Game.Instance.Fs.TryReadAllBytes(Mmh55Settings.ScriptPath, out var b)
                ? Mmh55Settings.Parse(H5.Scripting.Runtime.ScriptText.FromBytes(b))
                : new();
            return _settings;
        }
    }

    static bool SameNumber(string a, string b) =>
        double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
        double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ? Math.Abs(x - y) < 1e-9 : a == b;

    /// <summary>The player's values that differ from the file (null when none): what the scripts get appended.</summary>
    public static Dictionary<string, string>? Overrides()
    {
        Dictionary<string, string>? result = null;
        foreach (var s in Settings)
        {
            if (Hidden.Contains(s.Name)) continue;
            var v = OptionsStore.Get(VarPrefix + s.Name, s.Default);
            if (SameNumber(v, s.Default) || !Mmh55Settings.Valid(s.Name, v)) continue;
            (result ??= new())[s.Name] = v;
        }
        // Test: `--h55-set=H55_Name=value` (repeatable) overrides a setting without touching the profile.
        foreach (var a in Godot.OS.GetCmdlineUserArgs().Where(a => a.StartsWith("--h55-set=", StringComparison.Ordinal)))
            if (a["--h55-set=".Length..].Split('=') is [var n, var v] && Mmh55Settings.Valid(n, v)) (result ??= new())[n] = v;
        return result;
    }

    /// <summary>Options-screen categories built from the settings: file order, pages filled up to <see cref="PageUnits"/>, two pages per category.</summary>
    public static List<OptionsData.Category> Categories()
    {
        var pages = new List<List<OptionsData.Option>>();
        int used = PageUnits;
        foreach (var o in Settings.Where(s => !Hidden.Contains(s.Name)).Select(ToOption))
        {
            int units = o.Type == OptionsData.Kind.CheckBox ? 1 : 2;
            if (used + units > PageUnits) { pages.Add(new()); used = 0; }
            pages[^1].Add(o);
            used += units;
        }
        var cats = new List<OptionsData.Category>();
        int count = (pages.Count + 1) / 2;
        for (int c = 0; c < count; c++)
        {
            string suffix = count > 1 ? $" ({c + 1}/{count})" : "";
            var cat = new OptionsData.Category
            {
                Id = $"mmh55_{c + 1}",
                MenuButtonName = "MMH 5.5" + suffix,
                Name = "MMH 5.5 settings" + suffix,
                Tooltip = "Might & Magic: Heroes 5.5 player settings (scripts/H55-Settings.lua). Changes apply to new games.",
            };
            for (int p = c * 2; p < Math.Min(pages.Count, c * 2 + 2); p++)
                cat.Pages.Add(new OptionsData.Page { Id = $"mmh55_page_{p + 1}", Name = "H55-Settings.lua", Options = pages[p].ToList() });
            cats.Add(cat);
        }
        return cats;
    }

    static OptionsData.Option ToOption(Mmh55Setting s)
    {
        var tip = s.Name + " = " + s.Default + (s.Comment.Length > 0 ? "\n" + s.Comment : "") + (s.Notes.Length > 0 ? "\n\n" + s.Notes : "");
        var o = new OptionsData.Option
        {
            Id = VarPrefix + s.Name,
            Var = VarPrefix + s.Name,
            Name = s.Name.StartsWith("H55_") ? s.Name[4..] : s.Name,
            Tooltip = tip,
            FallbackValue = s.Default,
        };
        bool isBool = Bools.Contains(s.Name) || (ItemsFor(s.Name).Count == 0 && (s.Default == "0" || s.Default == "1"));
        if (isBool)
        {
            o.Type = OptionsData.Kind.CheckBox;
            o.Default = s.Default == "0" ? 0 : 1;
            return o;
        }
        var items = ItemsFor(s.Name);
        // The file's value (and the profile's) is always selectable, even outside the H5Reborn steps.
        foreach (var v in new[] { s.Default, OptionsStore.Get(VarPrefix + s.Name, s.Default) })
            if (Mmh55Settings.Valid(s.Name, v) && !items.Any(i => SameNumber(i.Value, v)))
                items.Add((v, v));
        items = items.OrderBy(i => double.Parse(i.Value, CultureInfo.InvariantCulture)).ToList();
        o.Type = OptionsData.Kind.ComboBox;
        o.Items = items.Select(i => new OptionsData.Item(i.Name, i.Value)).ToList();
        o.Default = Math.Max(0, items.FindIndex(i => SameNumber(i.Value, s.Default)));
        return o;
    }
}
