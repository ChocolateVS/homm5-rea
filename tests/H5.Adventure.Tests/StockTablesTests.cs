using H5.Core.Data;
using Xunit;

namespace H5.Adventure.Tests;

/// <summary>The stock engine's fixed table sizes on either fixture (StockTables, docs/mmh55/W1_DATA.md §1).</summary>
[Collection("adv")]
public class StockTablesTests(Fixture f)
{
    [Fact]
    public void OnlyTheStockCountTakesPartInPools()
    {
        // Every pool member has an enum value below the stock count.
        Assert.All(f.Core.Creatures.Values.Where(c => c.Stock), c =>
            Assert.True(StockTables.EnumValue(f.Fs, "CreatureType", c.Id) is not { } v || v < 180, c.Id));
        Assert.All(f.Db.ArtifactClass.Keys, a =>
            Assert.True(StockTables.EnumValue(f.Fs, "ArtifactEffect", a) is not { } v || v < 97, a));
        int nonStockCreatures = f.Core.Creatures.Values.Count(c => !c.Stock);
        int nonStockSpells = f.Core.Spells.Values.Count(s => !s.Stock);
        int nonStockArtifacts = f.Core.Artifacts.Values.Count(a => !a.Stock);
        if (DataFixture.Mmh55)
        {
            // types.xml CreatureType 1000 (820 placeholders from 180), SpellID 379, ArtifactEffect 196.
            Assert.True(nonStockCreatures > 0, "MMH55 creatures beyond the stock 180 are listed");
            Assert.All(f.Core.Creatures.Values.Where(c => !c.Stock), c => Assert.True(StockTables.EnumValue(f.Fs, "CreatureType", c.Id) >= 180));
            Assert.True(nonStockSpells > 0);
            Assert.True(nonStockArtifacts > 0);
            Assert.DoesNotContain("ARTIFACT_LEGION_T1", f.Db.ArtifactClass.Keys);
        }
        else
        {
            Assert.Equal(0, nonStockCreatures);
            Assert.Equal(0, nonStockSpells);
            Assert.Equal(0, nonStockArtifacts);
        }
    }

    [Fact]
    public void TypesXmlParsesWithItsByteOrderMark()
    {
        // MMH55-Frame.pak types.xml starts with a UTF-8 BOM; stock has none. Both must parse.
        Assert.Equal(180, StockTables.EnumValue(f.Fs, "CreatureType", "SANC_101") ?? 180);
        Assert.NotNull(StockTables.EnumValue(f.Fs, "CreatureType", "CREATURE_PEASANT"));
        Assert.NotNull(f.Db.EnumName("CreatureType", 1));
    }
}
