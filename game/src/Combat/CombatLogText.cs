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
    static string FillEntry(CombatScreen s, string template, int? source, int? target, string? spell, int damage, int dead, int? colorSide = null)
    {
        var vals = new List<(string, string)> { ("damage", damage.ToString()), ("dead", dead.ToString()), ("spell", spell ?? "") };
        if (source is int sr) { vals.Add(("source_color", Color(SideOfActor(s, sr)))); vals.Add(("source", ActorName(s, sr))); }
        else if (colorSide is int cs) vals.Add(("source_color", Color(cs)));
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
    public static string? Damage(CombatScreen s, Damaged d, int luck, SpellCast? lastCast, bool favored = false)
    {
        if (d.Kind is AttackKind.Spell or AttackKind.Other or AttackKind.Ability)
        {
            int src = d.Source;
            if (lastCast != null && d.By != null && lastCast.Spell.EndsWith(d.By)) src = lastCast.Caster;
            var spellId = ResolveSpell(d.By);
            var texts = d.By == null ? null : AbilityLogTexts(d.By) ?? (spellId != null ? SpellLogTexts(spellId) : null);
            string? name = d.By == null ? null : Game.Instance.Db.Abilities.GetValueOrDefault(d.By)?.Name is { Length: > 0 } an ? an
                : spellId != null ? s.SpellName(spellId) : null;
            // Fiery Wrath / Cold Steel: CCombatLogBonusFireDamage / ColdDamage (FUN_00a5ed90 / FUN_00a5c000 build them with the
            // attacker, target, damage and dead; selectors FUN_00c9a020 / FUN_00c9a060 take COMBAT_LOG_BONUS_ELEMENTAL_DAMAGE
            // texts 2 and 3).
            // Hellfire is dealt in the same function as Fiery Wrath (00a5ed90) and logs the same entry.
            if (d.By is "FIERY_WRATH" or "COLD_STEEL" or "HELLFIRE")
                return FillEntry(s, Template(d.By == "COLD_STEEL" ? "BonusColdDamage" : "BonusFireDamage"), src, d.Target, null, d.Damage, d.Killed);
            if (texts is { Count: > 0 } && HasDamage(texts[0]))
                return FillEntry(s, texts[0], src, d.Target, name != null ? Escape(name) : null, d.Damage, d.Killed);
            if (spellId != null && name != null)
                return FillEntry(s, Template(d.Damage > 0 ? "CastSpellTargetedDamage" : "CastSpellTargetedNoDamage"), src, d.Target, Escape(name), d.Damage, d.Killed);
            // Sources without a traced entry (moat: COMBAT_LOG_MOAT_DAMAGE needs its <value=mine>; tower shots; Banish;
            // Arcane Crystal; Armageddon rocks; scripted kills) write nothing rather than an invented line.
            return null;
        }
        string tname = d.Kind == AttackKind.Retaliation ? "Retaliate" : "Attack";
        if (luck > 0) tname += "GoodLuck"; else if (luck < 0) tname += "BadLuck";
        // +3 (the "Favored" texts) when the favoured-enemy critical fired (FUN_00a5ae40 result passed as the entry's +0x48).
        if (favored) tname += "Favored";
        return FillEntry(s, Template(tname), d.Source, d.Target, null, d.Damage, d.Killed);
    }

    static bool HasDamage(string template) => template.Contains("<value=damage>") || template.Contains("<value=dead>");

    /// <summary>Engine damage/heal sources ("FIRST_AID", "SOIL_BURN", spell ids) to the spell that carries the log texts.</summary>
    static string? ResolveSpell(string? by)
    {
        if (by == null) return null;
        if (by == "FIRST_AID") return "SPELL_EFFECT_FIRST_AID_TENT_HEAL";
        if (by == "ABILITY_REGENERATION") return "SPELL_EFFECT_REGENRATION";   // the stock id is misspelt
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
    public static string? Ability(CombatScreen s, AbilityTriggered a, SpellCast? lastCast)
    {
        string id = a.Ability == "LAST_STAND" ? "HERO_SKILL_LAST_STAND" : a.Ability;
        if (id.StartsWith("HERO_SKILL_", StringComparison.Ordinal) || id.StartsWith("HERO_SPEC_", StringComparison.Ordinal))
            return HeroSkill(s, a, id, lastCast);
        if (CreatureSpell(s, a) is { } spellLine) return spellLine;
        // Purger names the removed spell: written when the effect goes (CombatScreen, PurgedLine).
        if (a.Ability == "ABILITY_PURGER") return null;
        if (AbilityLogTexts(a.Ability) is { Count: > 0 } texts && !texts[0].Contains("<value=spell>"))
            return HasDamage(texts[0]) ? null : FillEntry(s, texts[0], a.UnitId, a.Target, null, 0, 0);
        var name = Game.Instance.Db.Abilities.TryGetValue(a.Ability, out var def) && def.Name.Length > 0 ? def.Name : a.Ability;
        return FillEntry(s, Template(a.Target is int ? "ApplyAbilityTargeted" : "ApplyAbility"), a.UnitId, a.Target, Escape(name), 0, 0);
    }

    /// <summary>
    /// A summon logs its spell's own text (CCombatLogSpell, spell CombatLogTexts index 0 — e.g. FUN_00861630 builds it
    /// for Summon Elementals: caster, summoned stack as target, count as dead); summons without texts log nothing.
    /// </summary>
    public static string? Summoned(CombatScreen s, UnitSummoned us, SpellCast? lastCast)
    {
        if (ResolveSpell(us.By) is not { } spellId || SpellLogTexts(spellId) is not { Count: > 0 } texts) return null;
        int? caster = lastCast != null && lastCast.Spell == spellId ? lastCast.Caster : null;
        return FillEntry(s, texts[0], caster, us.UnitId, Escape(s.SpellName(spellId)), 0, us.Count, us.Side);
    }

    /// <summary>
    /// Heals with their own spell text (CCombatLogSpell): Resurrect / Animate Dead "Resurrect" (spell, dead = raised),
    /// Resurrect Allies, Lay Hands, the Hydra's regeneration (FUN_008a6e20 → SPELL_EFFECT_REGENRATION, damage = healed).
    /// Heals whose source has no text log nothing; the tent keeps its own entry (TentHeal).
    /// </summary>
    public static string? Heal(CombatScreen s, Healed h, SpellCast? lastCast, AbilityTriggered? lastAbility)
    {
        if (h.By is null or "FIRST_AID") return null;
        string? spellId = h.By == "RESURRECT" && lastCast != null && SpellLogTexts(lastCast.Spell) != null ? lastCast.Spell : ResolveSpell(h.By);
        if (spellId == null || SpellLogTexts(spellId) is not { Count: > 0 } texts) return null;
        int? caster = lastCast != null && lastCast.Spell == spellId ? lastCast.Caster : null;
        var side = s.Session.Battle.State.Unit(h.Target)?.Side;
        if (lastAbility != null && lastAbility.Ability == h.By)
        {
            // An ability's heal: its user is the source; a heal returned to the user takes the second text when there is
            // one (Treant Union: "<target> restored <source> …, reciprocally").
            if (h.Target == lastAbility.UnitId && lastAbility.Target is int other && texts.Count > 1)
                return FillEntry(s, texts[1], lastAbility.UnitId, other, Escape(s.SpellName(spellId)), h.Amount, h.Resurrected);
            caster = lastAbility.UnitId;
        }
        return FillEntry(s, texts[0], caster, h.Target, Escape(s.SpellName(spellId)), h.Amount, h.Resurrected, side);
    }

    static Dictionary<string, List<string>>? _skillTexts, _specTexts;

    /// <summary>RPGRoot SkillTexts / SpecializationTexts (GameMechanics/RPGStats/RPGRoot.xdb), read by FUN_00bcb970 / FUN_00bcba80.</summary>
    static List<string>? HeroTexts(string id)
    {
        if (_skillTexts == null)
        {
            _skillTexts = new(); _specTexts = new();
            const string file = "GameMechanics/RPGStats/RPGRoot.xdb";
            var root = Game.Instance.Xdb.Load(file).Root;
            foreach (var item in root?.Element("SkillTexts")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                if (item.Element("SkillID")?.Value is { } k) _skillTexts[k] = ReadTexts(item.Element("Texts"), file);
            foreach (var item in root?.Element("SpecializationTexts")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                if (item.Element("Specialization")?.Value is { } k) _specTexts![k] = ReadTexts(item.Element("Texts"), file);
        }
        return _skillTexts.GetValueOrDefault(id) ?? _specTexts!.GetValueOrDefault(id);
    }

    /// <summary>
    /// COMBAT_LOG_HERO_USES_SKILL / _SPECIALIZATION_WORKS have no texts of their own: CCombatLogHeroUsesSkill (+0x4c skill,
    /// +0x50 index) takes RPGRoot SkillTexts[skill][index] (selector FUN_00c999d0 → FUN_00bcb970), CCombatLogSpecializationWorks
    /// SpecializationTexts (FUN_00c999e0 → FUN_00bcba80). Only the exe's call sites build them: Tap Runes 00419730, Stunning
    /// Blow / Critical Strike / Offender 008cf270, Last Stand 00a594f0, Elemental Balance 00861630 / 00862130, Magic Mirror
    /// 00983150, Lucky Spells 00983790, Remote Control 00bc0a30, Shake Ground 0097d7b0, Exploding Corpses 0097b690, Demonic
    /// Rage 00a5b9d0, Sacred Hammer 00bbebd0 — any other skill logs nothing.
    /// </summary>
    static string? HeroSkill(CombatScreen s, AbilityTriggered a, string id, SpellCast? lastCast)
    {
        if (HeroTexts(id) is not { Count: > 0 } t || t[0].Length == 0) return null;
        var st = s.Session.Battle.State;
        switch (id)
        {
            case "HERO_SKILL_LAST_STAND":
                return FillEntry(s, t[0], null, a.UnitId, null, 0, 0);
            case "HERO_SKILL_MAGIC_MIRROR":   // the mirrored spell is the one just cast
                return FillEntry(s, t[0], a.UnitId, a.Target, lastCast != null ? Escape(s.SpellName(lastCast.Spell)) : null, 0, 0);
            case "HERO_SKILL_ELEMENTAL_BALANCE":
                return FillEntry(s, t[0], a.UnitId, a.Target, null, 0, a.Target is int u && st.Unit(u) is { } su ? su.Count : 0);
            case "HERO_SPEC_SACRED_HAMMER":
                var hammer = Game.Instance.Db.Stats.Str("combat/HeroSpecializations/Runemage/SacredHammer_SpellToCast", "SPELL_BLOODLUST");
                return FillEntry(s, t[0], a.UnitId, a.Target, hammer.Length > 0 ? Escape(s.SpellName(hammer)) : null, 0, 0);
            case "HERO_SKILL_CRITICAL_STRIKE":
                return null;   // the strike's damage entry uses the text (CombatScreen)
            default:
                return FillEntry(s, t[0], a.UnitId, a.Target, null, 0, 0);
        }
    }

    /// <summary>Shake Ground (Tremors): one SkillTexts entry for the whole quake (0097d7b0 → 0097e6f3) with the total damage and dead.</summary>
    public static string? Tremors(CombatScreen s, int hero, int damage, int dead) =>
        HeroTexts("HERO_SKILL_SHAKE_GROUND") is { Count: > 0 } t ? FillEntry(s, t[0], hero, null, null, damage, dead) : null;

    /// <summary>Hero strike with Excruciating Strike: SkillTexts HERO_SKILL_CRITICAL_STRIKE (008cfa47, index 1 against a favoured enemy).</summary>
    public static string? CriticalStrike(CombatScreen s, Damaged d, bool favored) =>
        HeroTexts("HERO_SKILL_CRITICAL_STRIKE") is { Count: > 0 } t
            ? FillEntry(s, t[Math.Min(favored ? 1 : 0, t.Count - 1)], d.Source, d.Target, null, d.Damage, d.Killed) : null;

    /// <summary>
    /// Spells a creature puts on its target with an on-hit ability log that ability's text, with the spell named where the
    /// text asks for it (Hexing Attack, Whip Strike): CCombatLogCombatAbility index 0 (FUN_00c99fa0).
    /// </summary>
    static readonly (string Ability, string[] Spells)[] SpellAbilities =
    {
        ("ABILITY_HEXING_ATTACK", new[] { "SPELL_CURSE", "SPELL_SLOW", "SPELL_WEAKNESS", "SPELL_DISRUPTING_RAY" }),
        ("ABILITY_WHIP_STRIKE", new[] { "SPELL_SLOW", "SPELL_WEAKNESS", "SPELL_BERSERK" }),
        ("ABILITY_CURSING_ATTACK", new[] { "SPELL_CURSE" }),
        ("ABILITY_BLINDING_ATTACK", new[] { "SPELL_BLIND" }),
        ("ABILITY_SORROW_STRIKE", new[] { "SPELL_SORROW" }),
    };

    static string? CreatureSpell(CombatScreen s, AbilityTriggered a)
    {
        if (!Game.Instance.Db.Spells.ContainsKey(a.Ability) || s.Session.Battle.State.Unit(a.UnitId) is not { } u) return null;
        foreach (var (ability, spells) in SpellAbilities)
            if (u.Abilities.Contains(ability) && spells.Contains(a.Ability) && AbilityLogTexts(ability) is { Count: > 0 } t)
                return FillEntry(s, t[0], a.UnitId, a.Target, Escape(s.SpellName(a.Ability)), 0, 0);
        return null;
    }

    /// <summary>Purger: "&lt;spell&gt; is purged from &lt;target&gt;" with the removed effect's spell.</summary>
    public static string? Purged(CombatScreen s, AbilityTriggered a, string effect) =>
        AbilityLogTexts("ABILITY_PURGER") is { Count: > 0 } t && Game.Instance.Db.Spells.ContainsKey(effect)
            ? FillEntry(s, t[0], a.UnitId, a.Target, Escape(s.SpellName(effect)), 0, 0) : null;

    /// <summary>
    /// COMBAT_LOG_END_COMBAT (CCombatLogEndCombat, FUN_00af6680 → FUN_00c9af60 with the winner's and the loser's heroes;
    /// selector FUN_00c9a300): both heroes → EndCombatPVP, no losing hero → EndCombatWin ("&lt;source&gt; wins"), no
    /// winning hero → EndCombatDefeat ("&lt;target&gt; is defeated"). The exe always names a winner (FUN_00af2380 picks one
    /// when the combat ends without), so a draw writes nothing here.
    /// </summary>
    public static string? Ended(CombatScreen s, int? winner)
    {
        if (winner is not int w) return null;
        var st = s.Session.Battle.State;
        var wh = st.Heroes[w]; var lh = st.Heroes[1 - w];
        string Name(int side) => Escape(st.Heroes[side].Name);
        if (wh.Present)
            return Fill(Template(lh.Present ? "EndCombatPVP" : "EndCombatWin"), ("source_color", Color(w)), ("source", Name(w)));
        if (lh.Present)
            return Fill(Template("EndCombatDefeat"), ("target_color", Color(1 - w)), ("target", Name(1 - w)));
        return null;
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
