using H5.Core.Data;
using H5.Core.Xdb;

namespace H5Reborn.Ui.Net;

/// <summary>Lobby-facing view of a duel preset: what the selection UI needs, read straight from the data.</summary>
public sealed record DuelPresetInfo(int Index, string Name, string HeroName, int Level, int Stacks,
    string? LeftFace, string? RightFace, string? RoundedFace);

/// <summary>
/// The 24 stock duel presets from UI/MPDMLobby/presets.(DuelPresets).xdb. Kept separate from the
/// full rules database so the lobby opens instantly.
/// </summary>
public static class DuelPresetList
{
    public const string File = "UI/MPDMLobby/presets.(DuelPresets).xdb";
    static List<DuelPresetInfo>? _all;

    public static IReadOnlyList<DuelPresetInfo> All => _all ??= Load();

    static List<DuelPresetInfo> Load()
    {
        var xdb = Game.Instance.Xdb;
        var exp = new ExperienceTable();
        var list = new List<DuelPresetInfo>();
        int index = 0;
        foreach (var item in xdb.Load(File).Root!.Element("presets")!.Elements("Item"))
        {
            string? Href(string el) => (string?)item.Element(el)?.Attribute("href");
            var name = xdb.ReadText(Href("PresetNameFileRef"), File)?.Trim() ?? $"Preset {index + 1}";
            string heroName = "";
            int level = 1, stacks = 0;
            var heroRef = XdbRef.Parse(Href("PresetHero"), File);
            if (!heroRef.IsEmpty && Game.Instance.Fs.Exists(heroRef.Path))
            {
                var hero = xdb.Load(heroRef.Path).Root!;
                heroName = ((string?)hero.Element("Name") ?? "").Split(" - ")[0].Trim();
                level = exp.LevelFor((long?)hero.Element("Experience") ?? 0);
                stacks = hero.Element("armySlots")?.Elements("Item").Count(i => (int?)i.Element("Count") > 0) ?? 0;
            }
            list.Add(new DuelPresetInfo(index++, name, heroName, level, stacks,
                Tex(Href("LeftFace")), Tex(Href("RightFace")), Tex(Href("RoundedFace"))));
        }
        return list;

        static string? Tex(string? href) { var r = XdbRef.Parse(href, File); return r.IsEmpty ? null : r.Path; }
    }
}
