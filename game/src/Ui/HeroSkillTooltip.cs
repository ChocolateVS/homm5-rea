using Godot;
using H5.Adventure.State;
using H5.Core.Ui;
using H5.Core.Xdb;

namespace H5Reborn.Ui;

/// <summary>
/// The hero skill tooltip (NWorld::SHeroSkillTooltip 00a33960, filled into UI/Tooltips/HeroSkills by 005fdd50) shown
/// by a clicking tooltip creator (NUI::CClickingTooltipCreator 006b7a90): pressing the right button on the
/// window shows it, releasing hides it. The level-up box attaches one to each offered skill/perk (006a8f50).
/// Content: Name = the skill name at its mastery (value "name"), Description = its description (value
/// "description"), then for a skill "Allows to learn abilities:" (related text SkillText) with the perks based on
/// it, for a perk "Provided by:" (AbilityText) with its prerequisites for the hero's class, or its basic skill
/// when there are none. Each list entry is the Skill template: icon + name, formatted Learned / NotLearned by
/// whether the hero has it (param_4 vfunc +0x174).
/// GAP: which perks a skill lists (00876150) is not traced — the standard perks based on the skill are shown —
/// nor the tooltip manager's window width (TooltipContext); rows are not re-centred under the list.
/// </summary>
public static class HeroSkillTooltip
{
    const string Window = "UI/Tooltips/HeroSkills/HeroSkills.(WindowGathering).xdb";
    const string SkillsTable = "GameMechanics/RefTables/Skills.xdb";

    /// <summary>Shows the tooltip for <paramref name="skillId"/> while the right button is held on <paramref name="node"/>.</summary>
    public static void Attach(Control node, Func<(string SkillId, int Level, HeroState Hero)> what)
    {
        Control? shown = null;
        node.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right } mb) return;
            shown?.QueueFree();
            shown = null;
            if (!mb.Pressed) return;
            var (id, level, hero) = what();
            var host = node.GetTree().Root;
            shown = Build(id, level, hero, host.GetViewport().GetMousePosition());
            host.AddChild(shown);
        };
        node.MouseExited += () => { shown?.QueueFree(); shown = null; };
        node.TreeExiting += () => { shown?.QueueFree(); shown = null; };
    }

    static System.Xml.Linq.XElement? Item(string id) =>
        Game.Instance.Xdb.Load(SkillsTable).Root?.Element("objects")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == id)?.Element("obj");

    static string Text(string id, string field, int level)
    {
        var items = Item(id)?.Element(field)?.Elements("Item").ToList();
        if (items is not { Count: > 0 }) return "";
        var href = (string?)items[Math.Clamp(level - 1, 0, items.Count - 1)].Attribute("href");
        return UiResources.Instance.Text(href) ?? "";
    }

    /// <summary>The list under the description (00a33960).</summary>
    static List<string> Related(string id, HeroState hero)
    {
        var db = Game.Instance.Db;
        if (!db.Skills.TryGetValue(id, out var def)) return new();
        if (!def.IsPerk)
            return db.Skills.Values.Where(s => s.SkillType == "SKILLTYPE_STANDART_PERK" && s.BasicSkillId == id).Select(s => s.Id).ToList();
        var forClass = Item(id)?.Element("SkillPrerequisites")?.Elements("Item")
            .FirstOrDefault(i => (string?)i.Element("Class") == hero.Class)?.Element("dependenciesIDs")?.Elements("Item")
            .Select(i => i.Value.Trim()).Where(v => v.Length > 0).ToList();
        if (forClass is { Count: > 0 }) return forClass;
        return def.BasicSkillId is { Length: > 0 } b && b != "HERO_SKILL_NONE" ? new() { b } : new();
    }

    static UiNode? Child(UiScreen screen, UiNode row, string name) =>
        row.Window.Find(name) is { } w ? screen.NodeOf(w) : null;

    static bool Has(HeroState hero, string id) =>
        hero.Skills.Any(s => s.SkillId == id) || hero.Perks.Contains(id);

    static Control Build(string id, int level, HeroState hero, Vector2 at)
    {
        var screen = new UiScreen(Window) { MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 100 };
        // The gathering has no size of its own; its text rows (Name: margins 13/13, size hint 230) give the width.
        // GAP: the exe's tooltip manager sizing (TooltipContext) is not traced.
        screen.Root.Placement = screen.Root.Placement.MergedWith(new UiPlacement { Size = (new Int2(230 + 26, 0), true) });
        bool perk = Game.Instance.Db.Skills.TryGetValue(id, out var def) && def.IsPerk;
        screen.Find("Name")?.SetValue("name", Adventure.AdvDialogs.SkillName(id, level));
        screen.Find("Description")?.SetValue("description", Text(id, "DescriptionFileRef", level));
        var related = Related(id, hero);
        var header = screen.Find("Allows_Provided");
        string? Rel(string key) => screen.Root.RelatedTexts.TryGetValue(key, out var p) ? UiResources.Instance.Text(p) : null;
        if (related.Count == 0) header?.SetText("");
        else header?.SetValue("text", Rel(perk ? "AbilityText" : "SkillText") ?? "");
        if (screen.Find("SkillList") is { } list && list.Window.Children.FirstOrDefault() is { } template)
        {
            screen.NodeOf(template)!.Visible = false;
            template.Visible = false;
            foreach (var r in related)
            {
                var row = screen.Instantiate(template, list.Window, Int2.Zero);
                int lvl = Game.Instance.Db.Skills.TryGetValue(r, out var rd) && rd.IsPerk ? 1
                    : hero.Skills.FirstOrDefault(s => s.SkillId == r) is { } hs ? (int)hs.Mastery : 1;
                if (Child(screen, row, "Name") is { } name)
                {
                    name.SetValue("format", Rel(Has(hero, r) ? "Learned" : "NotLearned") ?? "");
                    name.SetValue("name", Adventure.AdvDialogs.SkillName(r, lvl));
                }
                if (Child(screen, row, "Icon") is { } icon && Adventure.AdvDialogs.SkillIcon(r, lvl) is { } tex)
                    icon.AddChild(new TextureRect
                    {
                        Texture = tex, AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore,
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                    });
            }
        }
        // Keep the window on screen next to the cursor.
        screen.Ready += () => Callable.From(() =>
        {
            var root = screen.Root.Rect;
            float s = screen.UiScale;
            var vp = screen.GetViewportRect().Size;
            var pos = at + new Vector2(16, 16) - new Vector2(root.X, root.Y) * s;
            pos.X = Math.Clamp(pos.X, -root.X * s, Math.Max(-root.X * s, vp.X - (root.X + root.W) * s));
            pos.Y = Math.Clamp(pos.Y, -root.Y * s, Math.Max(-root.Y * s, vp.Y - (root.Y + root.H) * s));
            screen.Position = pos;
        }).CallDeferred();
        return screen;
    }
}
