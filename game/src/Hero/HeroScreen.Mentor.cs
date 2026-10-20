using Godot;
using H5.Adventure.Engine;
using H5.Core.Data;
using H5Reborn.Adventure;
using H5Reborn.Town;
using H5Reborn.Ui;
using HeroState = H5.Adventure.State.HeroState;

namespace H5Reborn.Hero;

/// <summary>
/// Memory Mentor mode of the hero screen (CMemoryMentorDialogLogEntry → 006c4460 → hero screen 006cc450; the jog dial
/// 006c5380 in the unlearn mode +0x7dc == 6: button 0 exit, button 2 UnlearnSkillOrPerk with
/// HeroScreen_UnlearnSkillOrPerk / UnlearnSkillOrPerk_Tooltip). A skill or perk is picked on the Abilities page;
/// button 2 runs 006c0fb0: price = hero vfunc 0x33c; gold below it → UnlearnHeader + UnlearnNoMoney (OK box), else
/// UnlearnHeader + UnlearnWarning (skill_name, price; yes/no) → CUnlearnHeroSkillCmd.
/// </summary>
public partial class HeroScreen
{
    const string HeroTexts = "UI/HeroScreen3/HeroScreen.(WindowRelatedTexts).xdb";
    bool _mentor;
    string? _forget;
    UiScreen? _mentorJog;

    /// <summary>Opens the hero screen in the Memory Mentor's unlearn mode.</summary>
    public static HeroScreen OpenMentor(AdventureScreen adv, HeroState hero)
    {
        var s = new HeroScreen(adv, hero) { _mentor = true };
        adv.DialogLayer.AddChild(s);
        return s;
    }

    static string HT(string key) => UiGameRoot.RelatedTextAt(HeroTexts, key) ?? "";

    /// <summary>Called from _Ready in mentor mode.</summary>
    void SetUpMentor()
    {
        _mentorJog = new UiScreen("UI/Common/JogDial/JogDial.xdb", _ui.Bus);
        AddChild(_mentorJog);
        foreach (var (name, path, tip) in new[]
        {
            ("button_0", "UI/Common/JogDial/Buttons/Exit.(JogDialButton).xdb", ""),
            ("button_2", "UI/HeroScreen3/JogDial/UnlearnSkillOrPerk.(JogDialButton).xdb", HT("UnlearnSkillOrPerk_Tooltip")),
        })
            if (_mentorJog.Find(name) is { } btn && Game.Instance.Fs.Exists(path))
            {
                UiDrawableHelper.Apply(btn, Game.Instance.Xdb.Load(path).Root!, path);
                btn.SetTooltip(tip);
            }
        foreach (var unused in new[] { "button_1", "button_3", "button_4" }) _mentorJog.SetVisible(unused, false);
        _ui.Bus.On("jd_button_0_reaction", CloseHeroScreen);
        _ui.Bus.On("jd_button_2_reaction", AskUnlearn);

        // Picking: the skill icon or a perk icon of an Abilities row.
        var panel = _panels[0];
        for (int row = 0; row < 6; row++)
        {
            if (Find(panel, $"Abilities/Abilities{row}") is not { } line) continue;
            int r = row;
            Hook(line.FindChild("Skill", false, false)?.FindChild("Icon", false, false) as Control, () => SkillOfRow(r));
            for (int k = 0; k < 3; k++)
            {
                int kk = k;
                Hook(line.FindChild($"Perk{k}", false, false)?.FindChild("Icon", false, false) as Control, () => PerksOfRow(r).ElementAtOrDefault(kk));
            }
        }
        ShowPageByName("Abilities");
        RefreshMentor();
    }

    void Hook(Control? host, Func<string?> pick)
    {
        if (host == null) return;
        host.MouseFilter = MouseFilterEnum.Stop;
        host.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && pick() is { } id)
            {
                _forget = id;
                RefreshMentor();
            }
        };
    }

    List<SkillEntry> LearnedSkills() => _hero.Skills.Where(s => s.Mastery > Mastery.None).ToList();
    string? SkillOfRow(int row) => LearnedSkills().ElementAtOrDefault(row)?.SkillId;
    List<string> PerksOfRow(int row) => SkillOfRow(row) is { } s
        ? _hero.Perks.Where(p => _adv.Session.Db.Perks.TryGetValue(p, out var d) && d.BasicSkill == s).Take(3).ToList() : new();

    void RefreshMentor()
    {
        if (!_mentor) return;
        var panel = _panels[0];
        for (int row = 0; row < 6; row++)
        {
            if (Find(panel, $"Abilities/Abilities{row}") is not { } line) continue;
            if (line.FindChild("Skill", false, false)?.FindChild("Icon", false, false) is Control si)
                si.Modulate = SkillOfRow(row) is { } s && s == _forget ? new Color(1.4f, 1.4f, 0.8f) : Colors.White;
            var perks = PerksOfRow(row);
            for (int k = 0; k < 3; k++)
                if (line.FindChild($"Perk{k}", false, false)?.FindChild("Icon", false, false) is Control pi)
                    pi.Modulate = k < perks.Count && perks[k] == _forget ? new Color(1.4f, 1.4f, 0.8f) : Colors.White;
        }
        if (_mentorJog?.Find("button_2") is { } b) b.Enabled = _forget != null && _adv.Session.Game.CanUnlearn(_hero, _forget);
    }

    string ForgetName(string id)
    {
        bool skill = _adv.Session.Db.Perks.TryGetValue(id, out var d) && d.IsSkill;
        return UiResources.StripMarkup(AdvDialogs.SkillName(id, skill ? (int)_hero.MasteryOf(id) : 1));
    }

    void AskUnlearn()
    {
        if (_forget is not { } id || !_adv.Session.Game.CanUnlearn(_hero, id)) return;
        int price = H5.Adventure.Engine.Adventure.UnlearnCost(_hero);
        var owner = _adv.Session.S.Players.First(p => p.Index == _hero.Owner);
        string Fill(string key) => HT(key).Replace("<value=skill_name>", ForgetName(id)).Replace("<value=price>", price.ToString());
        if (owner.Purse[H5.Adventure.State.Purse.Gold] < price)
        {
            AdvDialogs.Message(this, HT("UnlearnHeader"), Fill("UnlearnNoMoney"), null);
            return;
        }
        AdvDialogs.YesNo(this, HT("UnlearnHeader"), Fill("UnlearnWarning"), null, yes =>
        {
            if (!yes) return;
            Apply(new UnlearnHeroSkillCmd(_hero.Id, id) { Player = _hero.Owner });
            _forget = null;
            RefreshMentor();
        });
    }
}
