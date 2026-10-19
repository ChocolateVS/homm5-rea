using H5.Adventure.Engine;
using H5.Adventure.State;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Sylvan Avenger's Guild (UI/FavoredEnemy, CFavoredEnemyScreen@NUI vtable 0xe2c9a4; enter_special →
/// FUN_00618ff0 → FUN_00647c80). UpperPanel = the garrison hero, LowerPanel = the visiting hero (panel controller
/// vtable 0xe2c928). Panel refresh FUN_00647cb0: HeroIcon = the hero's face (hidden without a hero); slots ud1..ud4
/// are shown for the entries of the panel's list; Up/Down are enabled when cycling (FUN_00647ae0) changes the slot,
/// otherwise the icon is hidden as well (FUN_0063e9a0); an empty slot hides its icon; a creature shows its icon with
/// TTFavoredEnemy listing the creature and its upgrades (creature1..). Cycling walks the candidates ("" and every
/// creature, FUN_00648990, wrapping, FUN_00648a30) until the strict list check 009a8d30 passes. ok / close /
/// timeout_forced_close (FUN_00647c40) commit both panels (FUN_00647bb0: a hero, a non-empty list passing the strict
/// check → <see cref="SetFavoriteEnemiesCmd"/>) and close.
/// </summary>
public sealed partial class FavoredEnemyScreen : TownSubScreen
{
    const string Dir = "UI/FavoredEnemy/";
    static readonly string[] Panels = { "UpperPanel", "LowerPanel" };
    readonly List<string>?[] _lists = new List<string>?[2];
    readonly HeroState?[] _heroes = new HeroState?[2];
    List<string>? _candidates;
    bool _committed;

    public FavoredEnemyScreen(TownScreen owner) : base(owner, "UI/FavoredEnemy.(WindowScreen).xdb")
    {
        _heroes[0] = owner.GarrisonHero();
        _heroes[1] = owner.VisitingHero();
        for (int p = 0; p < 2; p++)
        {
            if (_heroes[p] is not { } h) continue;
            // TODO(exe): the panel's initial list (screen +8) is not decoded; here the hero's list padded with empty
            // entries to the Avenger mastery.
            var l = h.FavoriteEnemies.ToList();
            int max = H5.Adventure.Engine.Adventure.FavoriteEnemiesMax(h);
            while (l.Count < max) l.Add("");
            if (l.Count > max) l.RemoveRange(max, l.Count - max);
            _lists[p] = l;
        }
        for (int p = 0; p < 2; p++)
            for (int i = 0; i < 4; i++)
            {
                int panel = p, slot = i;
                if (Ui.Find($"{Panels[p]}/ud{i + 1}/Up") is { } up) up.Clicked += _ => Cycle(panel, slot, back: true);
                if (Ui.Find($"{Panels[p]}/ud{i + 1}/Down") is { } dn) dn.Clicked += _ => Cycle(panel, slot, back: false);
            }
        // FUN_00648000: ok, close and timeout_forced_close all run FUN_00647c40 (commit both panels, close).
        foreach (var m in new[] { "ok", "close", "timeout_forced_close" }) Ui.Bus.On(m, () => { Commit(); Close(); });
    }

    public static bool CanOpen(TownScreen owner) =>
        owner.Town.Type == "TOWN_PRESERVE" && owner.Town.Level(H5.Adventure.Engine.Adventure.AvengersGuild) > 0;

    H5.Adventure.Engine.Adventure Game => Owner.Session.Game;

    /// <summary>Candidates (FUN_00648990): an empty entry, then every creature. TODO(exe): sort order (comparator 006477e0) not decoded.</summary>
    List<string> Candidates() => _candidates ??=
        new[] { "" }.Concat(Game.Core.Creatures.Values.Where(c => c.Stock).Select(c => c.Id)).ToList();

    /// <summary>FUN_00647ae0: step through the candidates from the current entry until the strict check passes.</summary>
    string Next(int panel, int slot, bool back)
    {
        var list = _lists[panel]!;
        var h = _heroes[panel]!;
        var cand = Candidates();
        string start = list[slot];
        int idx = cand.IndexOf(start);
        if (idx < 0) return start;
        var trial = list.ToList();
        for (int k = 1; k < cand.Count; k++)
        {
            string c = cand[((idx + (back ? -k : k)) % cand.Count + cand.Count) % cand.Count];
            trial[slot] = c;
            if (Game.CheckFavoriteEnemies(h, trial, strict: true)) return c;
        }
        return start;
    }

    void Cycle(int panel, int slot, bool back)
    {
        if (_lists[panel] is not { } list || slot >= list.Count) return;
        list[slot] = Next(panel, slot, back);
        Refresh();
    }

    void Commit()
    {
        if (_committed) return;
        _committed = true;
        for (int p = 0; p < 2; p++)
            if (_heroes[p] is { } h && _lists[p] is { Count: > 0 } l && Game.CheckFavoriteEnemies(h, l.ToList(), strict: true))
                Owner.Session.Apply(new SetFavoriteEnemiesCmd(h.Id, l.ToList()) { Player = Me.Index });
    }

    public override void Refresh()
    {
        base.Refresh();
        for (int p = 0; p < 2; p++)
        {
            var h = _heroes[p];
            if (Ui.Find($"{Panels[p]}/HeroIcon") is { } icon)
            {
                icon.Visible = h != null;
                if (h != null)
                {
                    TownUi.SetPicture(icon, TownUi.HeroFace(h, large: false));
                    icon.SetTooltip(TownUi.Text(Dir + "HeroIconTooltip.(Text).txt").Replace("<value=val>", h.Name));
                }
            }
            var list = _lists[p];
            for (int i = 0; i < 4; i++)
            {
                string slot = $"{Panels[p]}/ud{i + 1}";
                bool shown = list != null && i < list.Count;
                foreach (var b in new[] { "Up", "Down" }) Ui.SetVisible($"{slot}/{b}", shown);
                ShowIcon(slot, false);
                if (!shown) continue;
                bool can = Next(p, i, back: true) != list![i];
                foreach (var b in new[] { "Up", "Down" })
                    if (Ui.Find($"{slot}/{b}") is { } bn) bn.Enabled = can;
                if (!can || list[i].Length == 0) continue;
                ShowIcon(slot, true);
                TownUi.SetPicture(Ui.Find($"{slot}/self/icon"), TownUi.CreatureIcon(list[i]));
                Ui.Find($"{slot}/self/button")?.SetTooltip(Tooltip(list[i]));
            }
        }
    }

    void ShowIcon(string slot, bool on)
    {
        foreach (var n in new[] { "icon", "background", "button" }) Ui.SetVisible($"{slot}/self/{n}", on);
    }

    /// <summary>TTFavoredEnemy with creature1.. = the creature and its upgrades (FUN_00648900, creature +0x104).</summary>
    string Tooltip(string creature)
    {
        var names = new List<string> { creature };
        if (Game.Core.Creatures.TryGetValue(creature, out var c)) names.AddRange(c.Upgrades);
        string t = TownUi.Text(Dir + "TTFavoredEnemy.txt");
        for (int k = 0; k < 3; k++)
            t = t.Replace($"<value=creature{k + 1}>", k < names.Count && Game.Core.Creatures.TryGetValue(names[k], out var d) ? d.Name : "");
        return t;
    }
}
