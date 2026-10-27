using Godot;
using H5.Adventure.Engine;
using H5Reborn.Adventure;
using H5Reborn.Town;
using H5Reborn.Ui;
using HeroState = H5.Adventure.State.HeroState;

namespace H5Reborn.Hero;

/// <summary>
/// The hero screen's jog dial outside the Memory Mentor mode (006c5380, normal branch; clicks reach 006c85b0 by
/// button index): 0 exit (00783710: UIGameRoot ExitWithDay_(day % 7 + 1), tooltip ExitBtnTooltip with Day / Week /
/// Month / WeekName, 00783370) → close (006d0960); 1 ToLeft (tooltip PrevHero) → 006c4a80; 3 ToRight (NextHero) →
/// 006c49c0; both enabled without a second hero. 2 HeroScreen_DismissHero (DismissHero_Tooltip) → 006c11a0, enabled
/// without a second hero, when the player allows it (player vf 0x3c) and +0x760 (006cae0e: the hero has a map object,
/// world vf 0x100, and the screen is not over the town screen). 4 HeroScreen_CreatureInfo (CreatureInfo_Tooltip) →
/// 006c6c70(0).
/// </summary>
public partial class HeroScreen
{
    UiScreen? _jog;
    string _page = "Army";

    void SetUpJogDial()
    {
        _jog = new UiScreen("UI/Common/JogDial/JogDial.xdb", _ui.Bus);
        AddChild(_jog);
        var w = _adv.Session.S;
        int day = w.Day - 1;
        SetJog("button_0", $"ExitWithDay_{day % 7 + 1}", (UiGameRoot.Text("ExitBtnTooltip") ?? "")
            .Replace("<value=Day>", (day % 7 + 1).ToString()).Replace("<value=Week>", (day / 7 % 4 + 1).ToString())
            .Replace("<value=Month>", (day / 28 + 1).ToString()).Replace("<value=WeekName>", AdvTexts.WeekName(w.MoonWeek)), true);
        bool single = _meet == null;
        SetJog("button_1", "ToLeft", HT("PrevHero"), single);
        SetJog("button_3", "ToRight", HT("NextHero"), single);
        SetJog("button_2", "HeroScreen_DismissHero", HT("DismissHero_Tooltip"), single && CanDismiss());
        SetJog("button_4", "HeroScreen_CreatureInfo", HT("CreatureInfo_Tooltip"), true);
        _ui.Bus.On("jd_button_0_reaction", CloseHeroScreen);
        _ui.Bus.On("jd_button_1_reaction", () => StepHero(-1));
        _ui.Bus.On("jd_button_3_reaction", () => StepHero(+1));
        _ui.Bus.On("jd_button_2_reaction", AskDismiss);
        _ui.Bus.On("jd_button_4_reaction", ShowCreatureInfo);
    }

    void SetJog(string name, string rootId, string tooltip, bool enabled)
    {
        if (_jog?.Find(name) is not { } btn) return;
        if (UiGameRoot.Texture(rootId) is { } path && Game.Instance.Fs.Exists(path))
            UiDrawableHelper.Apply(btn, Game.Instance.Xdb.Load(path).Root!, path);
        btn.SetTooltip(tooltip);
        btn.Enabled = enabled;
    }

    /// <summary>
    /// 006c5c80..006c5cf0: dismissing needs +0x760 (a map object for the hero and no town screen underneath) and the
    /// player's consent (vf 0x3c, untraced: ours is the engine's dismiss rule).
    /// </summary>
    bool CanDismiss() => _town == null && !_hero.Garrisoned && _adv.Session.Game.CanDismiss(_hero);

    /// <summary>
    /// 006c4a80 / 006c49c0: the previous / next hero in the owner's hero list (player vf 0xc0), wrapping around; the
    /// screen is closed and opened again for that hero on the same page (006c4880 → 006d0960, 006c43d0).
    /// </summary>
    void StepHero(int delta)
    {
        if (_meet != null) return;
        var list = _adv.Session.S.Heroes.Where(h => h.Alive && h.Owner == _hero.Owner).ToList();
        int i = list.IndexOf(_hero);
        if (i < 0 || list.Count == 0) return;
        var next = list[((i + delta) % list.Count + list.Count) % list.Count];
        if (next == _hero) return;
        var parent = GetParent();
        var screen = new HeroScreen(_adv, next, _town) { _startPage = _page };
        QueueFree();
        parent.AddChild(screen);
    }

    string? _startPage;

    /// <summary>
    /// 006c11a0: a question box (00679150, type 1) with the screen's related texts DismissHeader / DismissWarning.
    /// What the answer runs is not traced; ours sends DismissHeroCmd on Yes and closes the screen.
    /// </summary>
    void AskDismiss()
    {
        if (_meet != null || !CanDismiss()) return;
        AdvDialogs.YesNo(this, HT("DismissHeader"), HT("DismissWarning"), null, yes =>
        {
            if (!yes) return;
            if (Apply(new DismissHeroCmd(_hero.Id) { Player = _hero.Owner })) CloseHeroScreen();
        });
    }

    /// <summary>006c6c70(0): creature info for the hero's first stack.</summary>
    void ShowCreatureInfo()
    {
        if (_hero.Army.FirstOrDefault(s => s != null && s.Count > 0) is { } stack)
            Interaction.CreatureInfoWindow.Show(this, stack.Creature, stack.Count, _hero);
    }
}
