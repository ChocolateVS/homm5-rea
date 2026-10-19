using Godot;
using H5.Combat;
using H5Reborn.Town;
using H5Reborn.Ui;

namespace H5Reborn.Combat;

/// <summary>
/// Tactics split (exe NUI::CCombatSplitStackController, FUN_0057a350, opened by FUN_005779d0 when a stack is dropped on an
/// army bar slot with the modifier key held): the stock UI/SplitStack window between the dragged stack and the slot's
/// stack (or an empty slot). The value is the target's new size: by default unchanged, or half the total (rounded down)
/// for an empty slot (FUN_0057a2c0, FUN_007866a0 = (n + 1) / 2 kept on the dragged side), within 0..total (FUN_0057a1a0);
/// Split is refused for two different creatures (FUN_0057a4a0). Applying moves the difference (FUN_0057a290 → FUN_00577e10).
/// </summary>
public static class TacticsSplitDialog
{
    public static void Open(CombatScreen screen, Control parent, Unit src, Unit? dst, int side, int barSlot)
    {
        int total = src.Count + (dst?.Count ?? 0);
        int start = dst is { Count: > 0 } ? dst.Count : total - (total + 1) / 2;
        // Modal and centred like the other combat boxes (RetreatDialog).
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0), AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Stop };
        var ui = new UiScreen("UI/SplitStack/SplitStack.(WindowSimple).xdb");
        var size = new Vector2(ui.Root.Rect.W, ui.Root.Rect.H);
        ui.AnchorRight = 0;
        ui.AnchorBottom = 0;
        ui.Size = size;
        dim.AddChild(ui);
        parent.AddChild(dim);
        void Center() => ui.Position = ((dim.Size - size) * 0.5f).Floor();
        dim.Resized += Center;
        Callable.From(Center).CallDeferred();
        TownUi.SetPicture(ui.Find("Icon1"), TownUi.CreatureIcon(src.CreatureId));
        TownUi.SetPicture(ui.Find("Icon2"), TownUi.CreatureIcon(dst?.CreatureId ?? src.CreatureId));
        bool same = dst == null || dst.CreatureId == src.CreatureId;
        if (ui.Find("Split") is { } ok) ok.Enabled = same;
        int Value() => Math.Clamp(int.TryParse(((ui.Find("Edit") as UiEditLine)?.Value ?? "").Trim(), out var v) ? v : 0, 0, total);
        void Show() => ui.Find("Cell")?.SetText($"<center>{total - Value()}");
        if (ui.Find("Edit") is UiEditLine edit)
        {
            edit.Value = start.ToString();
            edit.Changed += _ => Show();
        }
        Show();
        void Close() => dim.QueueFree();
        ui.Bus.On("split", () =>
        {
            if (!same) return;
            int diff = Value() - (dst?.Count ?? 0);
            Close();
            if (diff > 0)
            {
                if (dst == null) screen.Hud.PendingNewSlot = barSlot;
                screen.Submit(new SplitStackCmd(src.Id, diff, dst?.Id) { Side = side });
            }
            else if (diff < 0 && dst != null) screen.Submit(new SplitStackCmd(dst.Id, -diff, src.Id) { Side = side });
            screen.Hud.FillPlacementBar();
        });
        ui.Bus.On("split_close", Close);
    }
}
