using Godot;
using H5.Combat;
using H5.Core.Xdb;
using H5Reborn.Ui;

namespace H5Reborn.Combat;

/// <summary>
/// The two hover predictions of the original combat screen (exe 00a63020 builds them, 00545ae0
/// shows them):
/// the PredictionBox line at the bottom ("Attack. Damage: 30-45 (Kills: 2-3). Will retaliate",
/// Text/Game/CombatStrings/Predictions_Formats) and the small cursor_prediction label next to the
/// cursor ("Kills: 2-3" / "Retaliate: Yes", UI/CombatScreen-Heavy *-cursor-prediction.txt).
/// </summary>
public static class CombatPredictions
{
    public sealed record Prediction(string BoxMarkup, string? CursorMarkup);

    static Dictionary<string, string>? _ids;

    /// <summary>CombatStrings id (RPGRoot.xdb) → text, e.g. COMBAT_WILL_RETALIATE → "Will retaliate".</summary>
    public static string Str(string id)
    {
        if (_ids == null)
        {
            _ids = new(StringComparer.Ordinal);
            const string root = "GameMechanics/RPGStats/RPGRoot.xdb";
            foreach (var item in Game.Instance.Xdb.Load(root).Descendants("Item"))
            {
                var key = (string?)item.Element("ID");
                var href = (string?)item.Element("TextFileRef")?.Attribute("href");
                if (key != null && href != null && key.StartsWith("COMBAT_")) _ids[key] = XdbRef.Parse(href, root).Path;
            }
        }
        return _ids.TryGetValue(id, out var path) ? (UiResources.Instance.Text(path) ?? "").Trim() : "";
    }

    static string UiText(string path) => (UiResources.Instance.Text(path) ?? "").Trim();

    static string Fill(string template, params (string Name, string Value)[] values)
    {
        foreach (var (n, v) in values) template = template.Replace($"<value={n}>", v);
        return template;
    }

    static string Range(string simpleId, string separatedId, string minName, string maxName, int min, int max) =>
        min == max
            ? Fill(Str(simpleId), (minName, min.ToString()))
            : Fill(Str(separatedId), (minName, min.ToString()), (maxName, max.ToString()));

    /// <summary>Melee attack or shot (exe 00a63020).</summary>
    public static Prediction? Attack(Battle b, int attacker, int target, Cell? from, bool ranged)
    {
        if (b.PredictAttack(attacker, target, from, ranged) is not { } p) return null;
        string damage = Range("COMBAT_PREDICTION_DAMAGE_SIMPLE", "COMBAT_PREDICTION_DAMAGE_SEPARATED", "min_damage", "max_damage", p.MinDamage, p.MaxDamage);
        string kills = Range("COMBAT_PREDICTION_KILLS_SIMPLE", "COMBAT_PREDICTION_KILLS_SEPARATED", "min_kill", "max_kill", p.MinKills, p.MaxKills);
        string retaliate = Str(p.RetaliateKey);
        string box = p.Strikes < 2
            ? Fill(Str("COMBAT_PREDICTION_NORMAL_ATTACK"), ("damage", damage), ("kills", kills), ("retaliate", retaliate))
            : Fill(Str("COMBAT_PREDICTION_NORMAL_ATTACK_MULTIPLE"), ("damage", damage), ("kills", kills), ("retaliate", retaliate), ("attacks_count", p.Strikes.ToString()));
        string yesNo = Str(p.Retaliates ? "COMBAT_PREDICTION_YES" : "COMBAT_PREDICTION_NO");
        string cursor = Fill(UiText("UI/CombatScreen-Heavy/kills-cursor-prediction-1.txt"), ("kills", kills))
                      + Fill(UiText("UI/CombatScreen-Heavy/retaliation-cursor-prediction.txt"), ("retaliate_cursor", yesNo));
        return new Prediction(box, cursor);
    }

    /// <summary>
    /// A spell aimed at a stack or a cell. Damage and kills come from resolving the cast on a copy
    /// of the battle (the original evaluates the same formulas without rolling); other spells show
    /// "Cast: Name" / "Cast: Name on unit X".
    /// </summary>
    public static Prediction? Spell(Battle b, CastSpellCmd cmd, string spellName, string? targetName)
    {
        int damage = 0, killed = 0, healed = 0, resurrected = 0;
        bool anyDamage = false, anyHeal = false;
        try
        {
            var sim = new Battle(b.Db, b.State.Clone());
            var r = sim.Apply(cmd);
            if (r.Ok)
                foreach (var e in r.Events)
                {
                    if (e is Damaged d && (cmd.TargetId == null || d.Target == cmd.TargetId)) { damage += d.Damage; killed += d.Killed; anyDamage = true; }
                    else if (e is Healed h && (cmd.TargetId == null || h.Target == cmd.TargetId)) { healed += h.Amount; resurrected += h.Resurrected; anyHeal = true; }
                }
        }
        catch (Exception e) { GD.PushWarning($"[H5] spell prediction: {e.Message}"); }

        string box, cursor;
        if (anyDamage)
        {
            string dmg = Fill(Str("COMBAT_PREDICTION_DAMAGE_SIMPLE"), ("min_damage", damage.ToString()));
            string kills = Fill(Str("COMBAT_PREDICTION_KILLS_SIMPLE"), ("min_kill", killed.ToString()));
            box = Fill(Str("COMBAT_PREDICTION_DIRECT_DAMAGE_SPELL"), ("spell", spellName), ("damage", dmg), ("kills", kills));
            cursor = Fill(UiText("UI/CombatScreen-Heavy/kills-cursor-prediction-1.txt"), ("kills", kills));
            return new Prediction(box, cursor);
        }
        if (anyHeal && targetName != null)
        {
            box = Fill(Str("COMBAT_PREDICTION_FORMAT_RESURRECT"), ("target", targetName), ("hits_received", healed.ToString()), ("resurrected", resurrected.ToString()));
            cursor = Fill(UiText("UI/CombatScreen-Heavy/heal-cursor-prediction.txt"), ("hits_received", healed.ToString()))
                   + (resurrected > 0 ? Fill(UiText("UI/CombatScreen-Heavy/resurrect-cursor-prediction.txt"), ("resurrected", resurrected.ToString())) : "");
            return new Prediction(box, cursor);
        }
        box = targetName != null
            ? Fill(Str("COMBAT_CAST_SPELL_TARGETED"), ("spell", spellName), ("target", targetName))
            : Fill(Str("COMBAT_CAST_SPELL"), ("spell", spellName));
        return new Prediction(box, null);
    }

    /// <summary>
    /// A creature or hero ability: "Use: Name. Damage: x (Kills: y)." when it damages the target
    /// (COMBAT_PREDICTION_DIRECT_DAMAGE_ABILITY), else "Use: Name [on unit X]" (COMBAT_APPLY_ABILITY[_TARGETED]).
    /// </summary>
    public static Prediction Ability(Battle b, UseAbilityCmd cmd, string abilityName, string? targetName)
    {
        int damage = 0, killed = 0;
        bool any = false;
        try
        {
            var sim = new Battle(b.Db, b.State.Clone());
            var r = sim.Apply(cmd);
            if (r.Ok)
                foreach (var e in r.Events)
                    if (e is Damaged d && cmd.TargetId is int tid && d.Target == tid) { damage += d.Damage; killed += d.Killed; any = true; }
        }
        catch (Exception e) { GD.PushWarning($"[H5] ability prediction: {e.Message}"); }
        if (any)
        {
            string dmg = Fill(Str("COMBAT_PREDICTION_DAMAGE_SIMPLE"), ("min_damage", damage.ToString()));
            string kills = Fill(Str("COMBAT_PREDICTION_KILLS_SIMPLE"), ("min_kill", killed.ToString()));
            return new Prediction(Fill(Str("COMBAT_PREDICTION_DIRECT_DAMAGE_ABILITY"), ("spell", abilityName), ("damage", dmg), ("kills", kills)),
                Fill(UiText("UI/CombatScreen-Heavy/kills-cursor-prediction-1.txt"), ("kills", kills)));
        }
        return new(targetName != null
            ? Fill(Str("COMBAT_APPLY_ABILITY_TARGETED"), ("spell", abilityName), ("target", targetName))
            : Fill(Str("COMBAT_APPLY_ABILITY"), ("spell", abilityName)), null);
    }

    /// <summary>
    /// Hero strike (exe 00a62e50): the hero kills a level- and tier-based number of creatures, so
    /// the damage is a single value; heroes never draw retaliation.
    /// </summary>
    public static Prediction? HeroAttack(Battle b, int side, int target)
    {
        var h = b.State.Heroes[side];
        if (b.State.Unit(target) is not { } t) return null;
        int dmg = b.HeroStrikeDamage(h, t, 0);
        int kills = Battle.KillsFor(dmg, t);
        string damage = Fill(Str("COMBAT_PREDICTION_DAMAGE_SIMPLE"), ("min_damage", dmg.ToString()));
        string k = Fill(Str("COMBAT_PREDICTION_KILLS_SIMPLE"), ("min_kill", kills.ToString()));
        string box = Fill(Str("COMBAT_PREDICTION_NORMAL_ATTACK"), ("damage", damage), ("kills", k), ("retaliate", Str("COMBAT_NO_RETALIATE")));
        string cursor = Fill(UiText("UI/CombatScreen-Heavy/kills-cursor-prediction-1.txt"), ("kills", k))
                      + Fill(UiText("UI/CombatScreen-Heavy/retaliation-cursor-prediction.txt"), ("retaliate_cursor", Str("COMBAT_PREDICTION_NO")));
        return new Prediction(box, cursor);
    }
}
