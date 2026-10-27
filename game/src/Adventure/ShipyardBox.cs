using Godot;
using H5.Adventure.State;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

public static partial class AdvDialogs
{
    const string BuyShipTexts = "UI/BuyShip/Texts.(WindowRelatedTexts).xdb";

    /// <summary>
    /// The shipyard box (exe 006a7840, town and map shipyards). Window by state (00676290): Box2p when the boat can be
    /// built (enough gold and wood, slipway free water), Box1p when resources are short, Box0p otherwise.
    /// UI/BuyShip texts: ID_TITLE_TEXT, ID_HEADER_TEXT, ID_NORMAL / ID_BLOCKED / ID_DESERTED_DESCRIPTION; Face
    /// ID_SHIPYARD_FACE_TEXTURE; left ID_SHIPYARD_ACCEPT_TEXTURE + ID_ACCEPT_TEXT; right ID_SHIPYARD_DECLINE_TEXTURE
    /// + ID_DECLINE_TEXT, or ID_NORES / ID_NOGOLD / ID_NOWOOD_TEXT when short; T_HEADER, T_BUILD_BOAT, T_REFUSE,
    /// T_NOT_ENOUGH_RESOURCES tooltips. <paramref name="answer"/> gets true only for Build Boat + OK.
    /// </summary>
    public static void Shipyard(Control parent, PlayerState p, string status, Action<bool> answer)
    {
        string T(string id) => UiGameRoot.RelatedTextAt(BuyShipTexts, id) ?? "";
        bool gold = p.Purse[Purse.Gold] >= H5.Adventure.Engine.Adventure.ShipCostGold;
        bool wood = p.Purse[Purse.Wood] >= H5.Adventure.Engine.Adventure.ShipCostWood;
        bool resources = gold && wood, place = status == "ok", canBuild = resources && place;
        Control? blocker = null;
        var s = Open(parent, canBuild ? Box2p : !resources ? "UI/CommonBoxes/box1p_windowsimple.xdb" : Box0p, b => blocker = b);
        s.Find("Title")?.SetText($"<h3_header><center>{T("ID_TITLE_TEXT")}");
        s.Find("Title")?.SetTooltip(T("T_HEADER_TOOLTIP"));
        s.Find("HeaderMain")?.SetText(T("ID_HEADER_TEXT"));
        s.Find("MsgText")?.SetValue("text", T(status == "deserted" ? "ID_DESERTED_DESCRIPTION" : place ? "ID_NORMAL_DESCRIPTION" : "ID_BLOCKED_DESCRIPTION"));
        SetFace(s, "Face", UiResources.Instance.Texture(UiGameRoot.Texture("ID_SHIPYARD_FACE_TEXTURE")));
        var left = s.Find("ButtonLeft");
        var right = s.Find("ButtonRight");
        if (left != null)
        {
            SetFace(s, "ButtonLeft/Icon", UiResources.Instance.Texture(UiGameRoot.Texture("ID_SHIPYARD_ACCEPT_TEXTURE")));
            s.Find("ButtonLeft/Text")?.SetText(T("ID_ACCEPT_TEXT"));
            left.SetTooltip(T("T_BUILD_BOAT_TOOLTIP"));
        }
        if (right != null)
        {
            SetFace(s, "ButtonRight/Icon", UiResources.Instance.Texture(UiGameRoot.Texture("ID_SHIPYARD_DECLINE_TEXTURE")));
            s.Find("ButtonRight/Text")?.SetText(T(!gold && !wood ? "ID_NORES_TEXT" : !gold ? "ID_NOGOLD_TEXT" : !wood ? "ID_NOWOOD_TEXT" : "ID_DECLINE_TEXT"));
            right.SetTooltip(T(canBuild ? "T_REFUSE_TOOLTIP" : "T_NOT_ENOUGH_RESOURCES_TOOLTIP"));
        }
        // Both choices start unpressed (vtable +0xc(0)); OK builds only with Build Boat picked. The data ships
        // the CommonBoxes buttons disabled; the box enables the ones it uses (see EnableButtons): on Box2p the
        // choices, OK once one is picked (00675eb0 / 00675f60); on Box0p / Box1p the OK button.
        var okButton = s.Find("ButtonOk");
        if (canBuild) { EnableButtons(s, "ButtonLeft", "ButtonRight"); if (okButton != null) okButton.Enabled = false; }
        else EnableButtons(s, "ButtonOk");
        int choice = -1;
        bool closed = false;
        void Pick(int c) { if (!canBuild) return; choice = c; left?.SetState(c == 0 ? 1 : 0); right?.SetState(c == 1 ? 1 : 0); if (okButton != null) okButton.Enabled = true; }
        s.Bus.On("left", () => Pick(0));
        s.Bus.On("right", () => Pick(1));
        s.Bus.On("left_force", () => { Pick(0); Done(); });
        s.Bus.On("right_force", () => { Pick(1); Done(); });
        void Done() { if (closed) return; closed = true; blocker?.QueueFree(); answer(canBuild && choice == 0); }
        s.Bus.On("confirm", Done);
        s.Bus.On("cancel", () => { choice = -1; Done(); });
        Bind(s, Done, () => { choice = -1; Done(); });
    }
}
