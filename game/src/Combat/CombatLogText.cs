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
        var name = Game.Instance.Db.Creatures.TryGetValue(u.CreatureId, out var c) && c.Name.Length > 0 ? c.Name
                 : WarMachineName(u.CreatureId) ?? u.Name;
        return Escape(name);
    }

    static readonly Dictionary<string, string?> _machineNames = new();

    /// <summary>
    /// A war machine's stock name: its CreatureVisual (RefTables/WarMachines.xdb Visual) CreatureNameFileRef, e.g. "Ballista".
    /// </summary>
    public static string? WarMachineName(string id)
    {
        if (!Game.Instance.Db.WarMachines.ContainsKey(id)) return null;
        if (_machineNames.TryGetValue(id, out var cached)) return cached;
        string? name = null;
        if (CreatureModels.VisualFor(id) is { } visual)
            name = Game.Instance.Xdb.ReadText((string?)Game.Instance.Xdb.Load(visual).Root?.Element("CreatureNameFileRef")?.Attribute("href"), visual)?.Trim();
        return _machineNames[id] = string.IsNullOrEmpty(name) ? null : name;
    }

    public static string ActorName(CombatScreen s, int actor)
    {
        if (!Actor.IsHero(actor)) return UnitName(s, actor);
        var h = s.Session.Battle.State.Heroes[Actor.HeroSide(actor)];
        return Escape(h.Name.Length > 0 ? h.Name : "Hero");
    }

    static int SideOfActor(CombatScreen s, int actor) =>
        Actor.IsHero(actor) ? Actor.HeroSide(actor) : s.Session.Battle.State.Unit(actor)?.Side ?? 0;

    /// <summary>
    /// &lt;value=source_color&gt; / &lt;value=target_color&gt;: the exe (FUN_00559df0) inserts UIGameRoot LOG_PLAYER_SELF
    /// (Text/CombatLog/PlayerSelf.txt "&lt;body&gt;&lt;color_positive&gt;") when the actor's owner is the viewing player,
    /// LOG_PLAYER_ENEMY (PlayerEnemy.txt "&lt;body&gt;&lt;color_negative&gt;") otherwise.
    /// </summary>
    static string Color(int side) =>
        Template(side == _viewSide ? "PlayerSelf" : "PlayerEnemy") is { Length: > 0 } c ? c : "<body>";

    static int _viewSide;

    /// <summary>The viewing player's side for the self/enemy log colours (set by the combat screen).</summary>
    public static void SetViewSide(int side) => _viewSide = side;

    public static string Simple(CombatScreen s, string template, int actor) => FillEntry(s, Template(template), actor, null, null, 0, 0);

    // FUN_00559df0 fills only source_color, source, target_color, target, damage, dead and spell (FUN_0055a710 adds
    // element); <value=font> in the stock templates is never set, so it stays empty and the line keeps its colour.
    static string FillEntry(CombatScreen s, string template, int? source, int? target, string? spell, int damage, int dead)
    {
        var vals = new List<(string, string)> { ("damage", damage.ToString()), ("dead", dead.ToString()), ("spell", spell ?? "") };
        if (source is int sr) { vals.Add(("source_color", Color(SideOfActor(s, sr)))); vals.Add(("source", ActorName(s, sr))); }
        if (target is int tg) { vals.Add(("target_color", Color(SideOfActor(s, tg)))); vals.Add(("target", UnitName(s, tg))); }
        return Fill(template, vals.ToArray());
    }

    /// <summary>
    /// A damage entry. Attacks and retaliations: COMBAT_LOG_ATTACK / _RETALIATE, text index luck + 1 (FUN_00c99f40;
    /// "Favored" variants +3 not produced). Spell and ability damage: the spell's or ability's own CombatLogTexts
    /// (CCombatLogSpell FUN_00c99fd0 index 0 = damage; CCombatLogCombatAbility FUN_00c99fa0 index 0), otherwise the
    /// spell's per-target CCombatLogCastSpell (FUN_004e6600 / FUN_0097efd0 create it with the damage flag; selector
    /// FUN_00c9a440: damage &gt; 0 CastSpellTargetedDamage, 0 _NoDamage).
    /// </summary>
    public static string Damage(CombatScreen s, Damaged d, int luck, SpellCast? lastCast)
    {
        if (d.Kind is AttackKind.Spell or AttackKind.Other or AttackKind.Ability)
        {
            int src = d.Source;
            if (lastCast != null && d.By != null && lastCast.Spell.EndsWith(d.By)) src = lastCast.Caster;
            var spellId = ResolveSpell(d.By);
            var texts = d.By == null ? null : AbilityLogTexts(d.By) ?? (spellId != null ? SpellLogTexts(spellId) : null);
            string? name = d.By == null ? null : Game.Instance.Db.Abilities.GetValueOrDefault(d.By)?.Name is { Length: > 0 } an ? an
                : spellId != null ? s.SpellName(spellId) : null;
            if (texts is { Count: > 0 } && HasDamage(texts[0]))
                return FillEntry(s, texts[0], src, d.Target, name != null ? Escape(name) : null, d.Damage, d.Killed);
            if (spellId != null && name != null)
                return FillEntry(s, Template(d.Damage > 0 ? "CastSpellTargetedDamage" : "CastSpellTargetedNoDamage"), src, d.Target, Escape(name), d.Damage, d.Killed);
            return $"<body>{UnitName(s, d.Target)}: {d.Damage} damage ({d.Killed} died).";
        }
        string tname = d.Kind == AttackKind.Retaliation ? "Retaliate" : "Attack";
        if (luck > 0) tname += "GoodLuck"; else if (luck < 0) tname += "BadLuck";
        return FillEntry(s, Template(tname), d.Source, d.Target, null, d.Damage, d.Killed);
    }

    static bool HasDamage(string template) => template.Contains("<value=damage>") || template.Contains("<value=dead>");

    /// <summary>Engine damage/heal sources ("FIRST_AID", "SOIL_BURN", spell ids) to the spell that carries the log texts.</summary>
    static string? ResolveSpell(string? by)
    {
        if (by == null) return null;
        if (by == "FIRST_AID") return "SPELL_EFFECT_FIRST_AID_TENT_HEAL";
        var spells = Game.Instance.Db.Spells;
        foreach (var id in new[] { by, "SPELL_" + by, "SPELL_EFFECT_" + by, "SPELL_ABILITY_" + by })
            if (spells.ContainsKey(id)) return id;
        return null;
    }

    static readonly Dictionary<string, List<string>?> _spellTexts = new();
    static Dictionary<string, List<string>>? _abilityTexts;

    /// <summary>Templates of a spell's CombatLogTexts (its GameMechanics/Spell xdb).</summary>
    static List<string>? SpellLogTexts(string spellId)
    {
        if (_spellTexts.TryGetValue(spellId, out var cached)) return cached;
        List<string>? list = null;
        if (Game.Instance.Db.Spells.GetValueOrDefault(spellId)?.SourcePath is { Length: > 0 } path && Game.Instance.Fs.Exists(path))
            list = ReadTexts(Game.Instance.Xdb.Load(path).Root?.Element("CombatLogTexts"), path);
        return _spellTexts[spellId] = list is { Count: > 0 } ? list : null;
    }

    /// <summary>Templates of a creature ability's CombatLogTexts (GameMechanics/RefTables/CombatAbilities.xdb).</summary>
    static List<string>? AbilityLogTexts(string abilityId)
    {
        if (_abilityTexts == null)
        {
            _abilityTexts = new();
            const string table = "GameMechanics/RefTables/CombatAbilities.xdb";
            foreach (var item in Game.Instance.Xdb.Load(table).Root?.Element("objects")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                if (item.Element("ID")?.Value is { } id && ReadTexts(item.Element("obj")?.Element("CombatLogTexts"), table) is { Count: > 0 } l)
                    _abilityTexts[id] = l;
        }
        return _abilityTexts.GetValueOrDefault(abilityId);
    }

    static List<string> ReadTexts(System.Xml.Linq.XElement? el, string file) =>
        el?.Elements("Item").Select(i => Game.Instance.Xdb.ReadText((string?)i.Attribute("href"), file)?.Trim() ?? "").ToList() ?? new();

    /// <summary>
    /// First Aid Tent heal: SPELL_EFFECT_FIRST_AID_TENT_HEAL's CombatLogTexts index 1 (EffectFirstAidTentHeal,
    /// CCombatLogSpell built with the non-damage flag, FUN_00c9a6a0).
    /// </summary>
    public static string? TentHeal(CombatScreen s, int tent, Healed h) =>
        SpellLogTexts("SPELL_EFFECT_FIRST_AID_TENT_HEAL") is { Count: > 1 } t
            ? FillEntry(s, t[1], tent, h.Target, null, h.Amount, h.Resurrected) : null;

    /// <summary>COMBAT_LOG_CAST_SPELL without damage: CastSpell, or CastSpellTargeted with a target (FUN_00c9a440).</summary>
    public static string Spell(CombatScreen s, SpellCast sc) =>
        FillEntry(s, Template(sc.Target is int ? "CastSpellTargeted" : "CastSpell"), sc.Caster, sc.Target, Escape(s.SpellName(sc.Spell)), 0, 0);

    public static string Resisted(CombatScreen s, SpellResisted r) =>
        FillEntry(s, Template("SpellResisted"), null, r.Target, Escape(s.SpellName(r.Spell)), 0, 0);

    public static string Morale(CombatScreen s, MoraleProc m) =>
        FillEntry(s, Template(m.Good ? "Spells/EffectGoodMorale" : "Spells/EffectBadMorale"), m.UnitId, null, null, 0, 0);

    /// <summary>
    /// An ability use. Abilities with CombatLogTexts log their own text (CCombatLogCombatAbility, index 0, FUN_00c99fa0);
    /// a text with damage values is written by the damage entry instead (null here), and one needing a spell name we
    /// don't have (Purge, Hexing Attack, Whip Strike) falls back to COMBAT_LOG_APPLY_ABILITY: ApplyAbility, or
    /// ApplyAbilityTargeted with a target (FUN_00c9a240).
    /// </summary>
    public static string? Ability(CombatScreen s, AbilityTriggered a)
    {
        if (AbilityLogTexts(a.Ability) is { Count: > 0 } texts && !texts[0].Contains("<value=spell>"))
            return HasDamage(texts[0]) ? null : FillEntry(s, texts[0], a.UnitId, a.Target, null, 0, 0);
        var name = Game.Instance.Db.Abilities.TryGetValue(a.Ability, out var def) && def.Name.Length > 0 ? def.Name : a.Ability;
        return FillEntry(s, Template(a.Target is int ? "ApplyAbilityTargeted" : "ApplyAbility"), a.UnitId, a.Target, Escape(name), 0, 0);
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
        if (Game.Instance.Db.WarMachines.ContainsKey(creatureId))
            visual = CreatureModels.VisualFor(creatureId);
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
