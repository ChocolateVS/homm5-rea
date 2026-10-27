using System.Globalization;
using System.Xml.Linq;
using H5.Core.Xdb;

namespace H5Reborn.Options;

/// <summary>
/// The original options definitions: UI/uiconsts.(UIGameConstsH5).xdb → GameOptions →
/// Categories → Pages → Options. Each option names the global variable it edits
/// (GlobalVar1, the setvar names saved in the profile cfg), its control type, the values
/// of its items, its default, and enable/disable rules. The options screen is generated
/// from this data exactly as the stock game does.
/// </summary>
public sealed class OptionsData
{
    public const string ConstsPath = "UI/uiconsts.(UIGameConstsH5).xdb";

    public enum Kind { CheckBox, Scroller, ComboBox, BlockHeader }

    public sealed record Item(string Name, string Value);

    public sealed class Option
    {
        public string Id = "";
        public Kind Type;
        public string Name = "", Tooltip = "";
        public List<Item> Items = new();
        /// <summary>Items come from a runtime list (gfx_availible_resolutions) instead of the data.</summary>
        public string? ItemsGlobalVar;
        public bool UseItemNameAsValue;
        public string? Var;
        /// <summary>Value when the profile has none (options not in the stock defaults, e.g. the MMH 5.5 settings).</summary>
        public string? FallbackValue;
        public float Default;
        /// <summary>This option controls another one (OptionToDisableID/…ByValue1/…Value1).</summary>
        public string? DisablesId;
        public float DisableByValue, DisabledValue;
        /// <summary>Disabled while this global flag is set ('~' prefix negates).</summary>
        public string? DisableByCode;
        public List<string> Labels = new();
        public bool Hide, Preview;
        public string? Group;
    }

    public sealed class Page
    {
        public string Id = "", Name = "", Tooltip = "";
        public List<Option> Options = new();
    }

    public sealed class Category
    {
        public string Id = "", MenuButtonName = "", Name = "", Tooltip = "";
        public List<Page> Pages = new();
    }

    public List<Category> Categories { get; } = new();

    public IEnumerable<Option> AllOptions => Categories.SelectMany(c => c.Pages).SelectMany(p => p.Options);

    public Option? Find(string id) => AllOptions.FirstOrDefault(o => o.Id == id);

    static OptionsData? _instance;
    public static OptionsData Instance => _instance ??= Load(Game.Instance.Xdb);

    /// <summary>Forgets the definitions (the startup target switch remounts another install).</summary>
    public static void Reset() => _instance = null;

    public static OptionsData Load(XdbLoader xdb)
    {
        var data = new OptionsData();
        var go = xdb.Load(ConstsPath).Root?.Element("GameOptions");
        if (go == null) return data;
        string Text(XElement? refEl) => xdb.ReadText((string?)refEl?.Attribute("href"), ConstsPath)?.Trim() ?? "";
        static float F(XElement? e) =>
            float.TryParse(e?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        foreach (var c in go.Element("Categories")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var cat = new Category
            {
                Id = (string?)c.Element("ID") ?? "",
                MenuButtonName = Text(c.Element("MenuButtonNameFileRef")),
                Name = Text(c.Element("NameFileRef")),
                Tooltip = Text(c.Element("TooltipFileRef")),
            };
            foreach (var p in c.Element("Pages")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var page = new Page
                {
                    Id = (string?)p.Element("ID") ?? "",
                    Name = Text(p.Element("NameFileRef")),
                    Tooltip = Text(p.Element("TooltipFileRef")),
                };
                foreach (var o in p.Element("Options")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                {
                    var opt = new Option
                    {
                        Id = (string?)o.Element("ID") ?? "",
                        Type = ((string?)o.Element("Type")) switch
                        {
                            "OPTION_SCROLLER" => Kind.Scroller,
                            "OPTION_COMBO_BOX" => Kind.ComboBox,
                            "OPTION_BLOCK_HEADER" => Kind.BlockHeader,
                            _ => Kind.CheckBox,
                        },
                        Name = Text(o.Element("NameFileRef")),
                        Tooltip = Text(o.Element("TooltipFileRef")),
                        ItemsGlobalVar = NonEmpty((string?)o.Element("ItemsGlobalVar")),
                        UseItemNameAsValue = (bool?)o.Element("UseItemNameAsValue") == true,
                        Var = NonEmpty((string?)o.Element("GlobalVar1")),
                        Default = F(o.Element("DefaultValue1")),
                        DisablesId = NonEmpty((string?)o.Element("OptionToDisableID")),
                        DisableByValue = F(o.Element("OptionDisableByValue1")),
                        DisabledValue = F(o.Element("OptionToDisableValue1")),
                        DisableByCode = NonEmpty((string?)o.Element("DisableByCodeGlobalVar")),
                        Hide = (bool?)o.Element("Hide") == true,
                        Preview = (bool?)o.Element("Preview") == true,
                        Group = NonEmpty((string?)o.Element("Group")),
                    };
                    foreach (var it in o.Element("Items")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                        opt.Items.Add(new Item(Text(it.Element("NameFileRef")), (string?)it.Element("Value") ?? "0"));
                    foreach (var l in o.Element("LabelsFileRef")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                        opt.Labels.Add(xdb.ReadText((string?)l.Attribute("href"), ConstsPath)?.Trim() ?? "");
                    page.Options.Add(opt);
                }
                cat.Pages.Add(page);
            }
            data.Categories.Add(cat);
        }
        return data;
    }

    static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
