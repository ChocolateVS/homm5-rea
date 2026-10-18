using Godot;
using H5.Combat;
using H5Reborn.Ui;

namespace H5Reborn.Combat;

/// <summary>
/// The retreat button's availability and the stock "Retreat from Combat" choice box.
///
/// Availability (exe FUN_0053feb0 → FUN_00540090): the OnJog-Flee button is enabled only for
/// code 0, and its tooltip is the matching UI/CombatScreen-Heavy/OnJog-Flee-Tooltips text:
/// 5 tactics, 2/7 army not ready (not the local player's turn), 3 auto-combat, 4 duel mode,
/// 9 defending a town, 10 no hero, 11 Shackles of the Last Man, 14 no town with a tavern;
/// codes 1/6/8 (input busy) keep RETREAT_NORMAL but disabled.
///
/// The box (exe FUN_00570cf0, base FUN_00676de0 on N_BOX2P: UI/CommonBoxes/Box2p): UIGameRoot
/// ID_RETREAT_FACE_TEXTURE in Face, ID_RETREAT_TITLE_TEXT in Title, ID_RETREAT_HEADER_TEXT in
/// HeaderMain, ID_RETREAT_DESCRIPTION (with &lt;value=cost&gt;) when surrender is possible else
/// ID_RETREAT_DESCRIPTION_FLEE; left choice ID_FLEE_TEXTURE/ID_FLEE_TEXT, right choice
/// ID_SURRENDER_TEXTURE with ID_RETREAT_SURRENDER_TEXT, or ID_RETREAT_NOGOLD_TEXT (disabled) when
/// the ransom can't be paid; ID_COMBAT_RETREAT tooltips T_HEADER/T_FLEE/T_SURRENDER_TOOLTIP;
/// input_Combat_Retreat_Close (R) and ESC close it.
/// </summary>
public static class RetreatDialog
{
    public const string Box2p = "UI/CommonBoxes/Box2p_WindowSimple.xdb";
    const string FleeTooltips = "UI/CombatScreen-Heavy/OnJog-Flee-Tooltips.(WindowRelatedTexts).xdb";

    /// <summary>The exe's refusal code for the local player's retreat (0 = allowed).</summary>
    public static int RefusalCode(CombatScreen screen, bool busy)
    {
        var session = screen.Session;
        var s = session.Battle.State;
        if (s.Phase == BattlePhase.Tactics) return 5;
        int side = session.ViewSide;
        if (!session.LocalSides.Contains(side)) return 2;
        if (CombatScreen.Autoplay) return 3;
        if (session.IsDuelMode) return 4;
        if (session.Ended || session.Battle.SideToAct != side || session.Busy) return 7;
        if (busy) return 6;
        var hero = s.Heroes[side];
        if (s.Siege != null && side == 1) return 9;
        if (!hero.Present || hero.Fled) return 10;
        if (s.Heroes.Any(h => h.Present && h.Artifacts.Contains("SHACKLES_OF_WAR"))) return 11;
        if (!session.HasTavernTown(side)) return 14;
        return 0;
    }

    static string TooltipName(int code) => code switch
    {
        2 or 7 => "CANT_RETREAT_ARMY_NOT_READY",
        3 => "CANT_RETREAT_AUTO_COMBAT",
        4 => "CANT_RETREAT_CANT_FLEE",
        5 => "CANT_RETREAT_TACTICS_MODE",
        9 => "CANT_RETREAT_IN_TOWN",
        10 => "CANT_RETREAT_NO_HERO",
        11 => "CANT_RETREAT_SHACKLES_OF_WAR",
        14 => "CANT_RETREAT_NO_TOWNS",
        _ => "RETREAT_NORMAL",
    };

    /// <summary>Applies the code to the jog button: enabled state and tooltip text.</summary>
    public static void ApplyButton(UiNode button, int code)
    {
        button.Enabled = code == 0;
        button.SetTooltip(UiGameRoot.RelatedTextAt(FleeTooltips, TooltipName(code)));
    }

    /// <summary>Opens the choice box over the HUD; the chosen command is submitted on OK.</summary>
    public static void Open(CombatScreen screen, Control parent)
    {
        var session = screen.Session;
        int side = session.ViewSide;
        int? cost = session.SurrenderCost(side);
        long gold = session.Gold(side);
        if (screen.DevRansom is { } dev) { cost = dev.Cost < 0 ? null : dev.Cost; gold = dev.Gold; }
        bool canSurrender = cost != null;
        bool hasGold = canSurrender && gold >= cost!.Value;

        var blocker = new Control { Name = "RetreatModal", AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Stop };
        var s = new UiScreen(Box2p);
        var size = new Vector2(s.Root.Rect.W, s.Root.Rect.H);
        s.AnchorRight = 0;
        s.AnchorBottom = 0;
        s.Size = size;
        blocker.AddChild(s);
        parent.AddChild(blocker);
        void Center() => s.Position = ((blocker.Size - size) * 0.5f).Floor();
        blocker.Resized += Center;
        Callable.From(Center).CallDeferred();
        if (s.Find("MsgText") is { } msg && s.Find("ScrollableText") is { } area)
        {
            msg.GetParent()?.RemoveChild(msg);
            area.AddChild(msg);
            msg.Position = Vector2.Zero;
            msg.Size = area.Size;
            area.Resized += () => msg.Size = area.Size;
        }

        s.Find("Title")?.SetText(UiGameRoot.Text("ID_RETREAT_TITLE_TEXT"));
        var header = s.Find("HeaderMain");
        header?.SetText(UiGameRoot.Text("ID_RETREAT_HEADER_TEXT"));
        header?.SetTooltip(UiGameRoot.RelatedText("ID_COMBAT_RETREAT", "T_HEADER_TOOLTIP"));
        var desc = canSurrender
            ? (UiGameRoot.Text("ID_RETREAT_DESCRIPTION") ?? "").Replace("<value=cost>", cost!.Value.ToString())
            : UiGameRoot.Text("ID_RETREAT_DESCRIPTION_FLEE") ?? "";
        s.Find("MsgText")?.SetValue("text", desc);
        SetIcon(s, "Face", UiGameRoot.Texture("ID_RETREAT_FACE_TEXTURE"));

        var left = s.Find("ButtonLeft");
        var right = s.Find("ButtonRight");
        SetIcon(s, "ButtonLeft/Icon", UiGameRoot.Texture("ID_FLEE_TEXTURE"));
        SetIcon(s, "ButtonRight/Icon", UiGameRoot.Texture("ID_SURRENDER_TEXTURE"));
        s.Find("ButtonLeft/Text")?.SetText(UiGameRoot.Text("ID_FLEE_TEXT"));
        left?.SetTooltip(UiGameRoot.RelatedText("ID_COMBAT_RETREAT", "T_FLEE_TOOLTIP"));
        if (canSurrender && !hasGold)
        {
            s.Find("ButtonRight/Text")?.SetText(UiGameRoot.Text("ID_RETREAT_NOGOLD_TEXT"));
            right?.SetTooltip(UiGameRoot.Text("ID_RETREAT_NOGOLD_TOOLTIP"));
        }
        else
        {
            s.Find("ButtonRight/Text")?.SetText(UiGameRoot.Text("ID_RETREAT_SURRENDER_TEXT"));
            right?.SetTooltip(UiGameRoot.RelatedText("ID_COMBAT_RETREAT", "T_SURRENDER_TOOLTIP"));
        }
        if (right != null) right.Enabled = canSurrender && hasGold;

        int choice = 0;
        void Pick(int c)
        {
            if (c == 1 && !(canSurrender && hasGold)) return;
            choice = c;
            left?.SetState(c == 0 ? 1 : 0);
            right?.SetState(c == 1 ? 1 : 0);
        }
        s.Bus.On("left", () => Pick(0));
        s.Bus.On("right", () => Pick(1));
        Pick(0);
        void Close() => blocker.QueueFree();
        s.Bus.On("confirm", () =>
        {
            Close();
            screen.Submit(new SurrenderCmd { Side = side, PayRansom = choice == 1 });
        });
        s.Bus.On("cancel", Close);
        s.AddChild(new RetreatKeys { Name = "Keys", OnClose = Close });
    }

    static void SetIcon(UiScreen s, string node, string? texture)
    {
        if (s.Find(node) is not { } n || UiResources.Instance.Texture(texture) is not { } tex) return;
        n.AddChild(new TextureRect
        {
            Texture = tex, AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        });
    }
}

/// <summary>ESC and the retreat key (input_Combat_Retreat_Close 'R') close the retreat box.</summary>
public partial class RetreatKeys : Node
{
    public Action? OnClose;

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k && (k.Keycode == Key.Escape || k.Keycode == Key.R))
        {
            OnClose?.Invoke();
            GetViewport().SetInputAsHandled();
        }
    }
}
