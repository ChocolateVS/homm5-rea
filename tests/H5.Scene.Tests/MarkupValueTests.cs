using H5.Core.Ui;

namespace H5.Scene.Tests;

public class MarkupValueTests(InstallFixture install) : IClassFixture<InstallFixture>
{
    [Fact]
    public void InsertedValuesAreParsedAsMarkup()
    {
        if (install.Xdb == null) return;
        var values = new Dictionary<string, string> { ["who"] = "<color=FFFF1212>Red", ["plain"] = "Footman" };
        var runs = new UiMarkup(install.Xdb).Parse("Owned by <value=who> and <value=plain>", null, values);
        var red = Assert.Single(runs, r => r.Text.Contains("Red"));
        Assert.Equal(0xFFFF1212u, red.Style.Color);
        Assert.DoesNotContain(runs, r => r.Text.Contains("<color"));
        // Plain values stay a single run tagged with their value name.
        Assert.Contains(runs, r => r.Text == "Footman" && r.ValueName == "plain");
    }

    [Fact]
    public void HotkeyTagExpandsLikeTheExe()
    {
        if (install.Xdb == null) return;
        var prev = UiMarkup.HotkeyResolver;
        UiMarkup.HotkeyResolver = a => a == "input_Combat_Wait" ? new[] { "W" } : null;
        try
        {
            var runs = new UiMarkup(install.Xdb).Parse("Wait<hotkey=input_Combat_Wait>");
            // hotkeys_prefix "." + hotkey_prefix " '" + key + hotkey_postfix "'" (UI/Consts/mlTags).
            Assert.Equal("Wait. 'W'", string.Concat(runs.Select(r => r.Text)));
            var none = new UiMarkup(install.Xdb).Parse("Menu<hotkey=input_unbound>");
            Assert.Equal("Menu", string.Concat(none.Select(r => r.Text)));
        }
        finally { UiMarkup.HotkeyResolver = prev; }
    }
}
