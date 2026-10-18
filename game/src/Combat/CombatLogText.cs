using H5.Combat;
using H5.Core.Xdb;
using H5Reborn.Arena;
using H5Reborn.Ui;

namespace H5Reborn.Combat;

/// <summary>
/// Combat log lines built from the original templates in Text/CombatLog (referenced by
/// GameMechanics/RefTables/CombatLog.xdb), with &lt;value=...&gt; placeholders filled.
/// </summary>
public static class CombatLogText
{
    // Player colours as the stock log uses them (player 1 red, player 2 blue).
    static readonly string[] SideColor = { "<color=FFFF5A46>", "<color=FF5A96FF>" };

    static string Template(string name) => UiResources.Instance.Text($"Text/CombatLog/{name}.txt") ?? "";

    public static string Escape(string s) => s.Replace('<', '‹').Replace('>', '›');

    static string Fill(string template, params (string Key, string Value)[] values)
    {
        foreach (var (k, v) in values) template = template.Replace($"<value={k}>", v);
        return template;
    }

    public static string StartCombat() => Template("StartCombat") is { Length: > 0 } t ? t : "<body>Combat started.";

    public static string UnitName(CombatScreen s, int id)
    {
        var u = s.Session.Battle.State.Unit(id);
        if (u == null) return $"#{id}";
        var name = Game.Instance.Db.Creatures.TryGetValue(u.CreatureId, out var c) && c.Name.Length > 0 ? c.Name : u.Name;
        return Escape(name);
    }

    public static string ActorName(CombatScreen s, int actor)
    {
        if (!Actor.IsHero(actor)) return UnitName(s, actor);
        var h = s.Session.Battle.State.Heroes[Actor.HeroSide(actor)];
        return Escape(h.Name.Length > 0 ? h.Name : "Hero");
    }

    static int SideOfActor(CombatScreen s, int actor) =>
        Actor.IsHero(actor) ? Actor.HeroSide(actor) : s.Session.Battle.State.Unit(actor)?.Side ?? 0;

    static string Color(int side) => SideColor[Math.Clamp(side, 0, 1)];

    public static string Simple(CombatScreen s, string template, int actor) =>
        Fill(Template(template), ("source_color", Color(SideOfActor(s, actor))), ("source", ActorName(s, actor)));

    public static string Damage(CombatScreen s, Damaged d, int luck)
    {
        string name = d.Kind == AttackKind.Retaliation ? "Retaliate" : "Attack";
        if (d.Kind is AttackKind.Spell or AttackKind.Other or AttackKind.Ability)
            return $"<body>{UnitName(s, d.Target)}: {d.Damage} damage ({d.Killed} died).";
        if (luck > 0) name += "GoodLuck"; else if (luck < 0) name += "BadLuck";
        int src = d.Source;
        return Fill(Template(name),
            ("source_color", Color(SideOfActor(s, src))), ("source", ActorName(s, src)),
            ("target", UnitName(s, d.Target)), ("font", "<body>"),
            ("damage", d.Damage.ToString()), ("dead", d.Killed.ToString()));
    }

    public static string Spell(CombatScreen s, SpellCast sc)
    {
        var spell = Escape(s.SpellName(sc.Spell));
        var t = sc.Target is int target ? Template("CastSpellTargeted") : Template("CastSpell");
        return Fill(t, ("source_color", Color(SideOfActor(s, sc.Caster))), ("source", ActorName(s, sc.Caster)),
            ("spell", spell), ("target", sc.Target is int tt ? UnitName(s, tt) : ""));
    }

    public static string Resisted(CombatScreen s, SpellResisted r) =>
        Fill(Template("SpellResisted"), ("target_color", Color(SideOfActor(s, r.Target))), ("target", UnitName(s, r.Target)),
            ("spell", Escape(s.SpellName(r.Spell))));

    public static string Morale(CombatScreen s, MoraleProc m) =>
        Fill(Template(m.Good ? "Spells/EffectGoodMorale" : "Spells/EffectBadMorale"),
            ("source_color", Color(SideOfActor(s, m.UnitId))), ("source", UnitName(s, m.UnitId)));

    public static string Ability(CombatScreen s, AbilityTriggered a)
    {
        var name = Game.Instance.Db.Abilities.TryGetValue(a.Ability, out var def) && def.Name.Length > 0 ? def.Name : a.Ability;
        var t = a.Target is int ? Template("ApplyAbilityTargeted") : Template("ApplyAbility");
        return Fill(t, ("source_color", Color(SideOfActor(s, a.UnitId))), ("source", ActorName(s, a.UnitId)),
            ("spell", Escape(name)), ("target", a.Target is int tt ? UnitName(s, tt) : ""));
    }

    public static string Summoned(CombatScreen s, UnitSummoned us) =>
        $"<body>{Color(us.Side)}{UnitName(s, us.UnitId)}<body> ({us.Count}) appear.";

    public static string Ended(CombatScreen s, int? winner)
    {
        if (winner is not int w) return "<body>The battle ends in a draw.";
        var h = s.Session.Battle.State.Heroes[w];
        return Fill(Template("EndCombatPVP"), ("source_color", Color(w)), ("source", Escape(h.Name.Length > 0 ? h.Name : $"Player {w + 1}")));
    }
}

/// <summary>Original portrait textures for units and heroes (ATB bar, info panels).</summary>
public static class CombatIcons
{
    static readonly Dictionary<string, string?> _creature = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>CreatureVisual Icon64/Icon128 texture xdb for a creature or war machine.</summary>
    public static string? Creature(string creatureId, bool large)
    {
        var key = creatureId + (large ? "#L" : "#S");
        if (_creature.TryGetValue(key, out var cached)) return cached;
        string? visual = null;
        if (creatureId.StartsWith("WAR_MACHINE_"))
        {
            var name = creatureId switch
            {
                "WAR_MACHINE_BALLISTA" => "Ballista", "WAR_MACHINE_FIRST_AID_TENT" => "FirstAidTent",
                "WAR_MACHINE_AMMO_CART" => "AmmoCart", _ => "Catapult",
            };
            visual = $"GameMechanics/CreatureVisual/WarMachines/{name}.xdb";
        }
        else if (CreatureModels.CreatureFile(creatureId) is { } file && Game.Instance.Fs.Exists(file))
        {
            var r = XdbRef.Parse((string?)Game.Instance.Xdb.Load(file).Root!.Element("Visual")?.Attribute("href"), file);
            if (!r.IsEmpty) visual = r.Path;
        }
        string? result = null;
        if (visual != null && Game.Instance.Fs.Exists(visual))
        {
            var root = Game.Instance.Xdb.Load(visual).Root!;
            foreach (var el in large ? new[] { "Icon128", "Icon64", "Icon" } : new[] { "Icon64", "Icon", "Icon128" })
            {
                var h = (string?)root.Element(el)?.Attribute("href");
                if (!string.IsNullOrEmpty(h)) { result = XdbRef.Parse(h, visual).Path; break; }
            }
        }
        return _creature[key] = result;
    }

    public static string? Hero(string heroId, bool large)
    {
        if (!Game.Instance.Db.Heroes.TryGetValue(heroId, out var def) || !Game.Instance.Fs.Exists(def.SourcePath)) return null;
        var root = Game.Instance.Xdb.Load(def.SourcePath).Root!;
        var h = (string?)root.Element(large ? "FaceTexture" : "FaceTextureSmall")?.Attribute("href");
        return string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, def.SourcePath).Path;
    }
}
