using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>Deep copies of window trees for template instantiation; immutable parts (drawables, texts, commands) are shared.</summary>
public static class UiWindowCopy
{
    public static UiWindow Deep(UiWindow w)
    {
        var c = new UiWindow
        {
            Type = w.Type, Name = w.Name, SourcePath = w.SourcePath, SharedPath = w.SharedPath,
            Placement = w.Placement, Visible = w.Visible, Enabled = w.Enabled, Transparent = w.Transparent,
            Priority = w.Priority, TooltipPath = w.TooltipPath, Background = w.Background, Foreground = w.Foreground,
            Text = w.Text, ButtonStates = w.ButtonStates, ButtonGroupId = w.ButtonGroupId, TriggerMode = w.TriggerMode,
            RelatedTexts = w.RelatedTexts, RelatedTextures = w.RelatedTextures,
            RawInstance = w.RawInstance, RawShared = w.RawShared, Rect = w.Rect,
        };
        foreach (var child in w.Children) c.Children.Add(Deep(child));
        return c;
    }
}
