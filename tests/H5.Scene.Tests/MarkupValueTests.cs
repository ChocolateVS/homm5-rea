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
}
