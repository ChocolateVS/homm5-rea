using H5.Combat;

namespace H5.Adventure.Tests;

/// <summary>
/// Duel heroes are the preset map's AdvMapHero objects made by the map-hero creation (H5_Game.exe 009aacb0 → hero
/// set-up 00b56cd0, book entries of the worn artifacts 00b541f0); the preset screen 00749570 / 0072d430 is given
/// those hero objects.
/// </summary>
[Collection("adv")]
public class DuelHeroTests(Fixture f)
{
    [Fact]
    public void WornArtifactSpellsReachTheDuelHero()
    {
        var g = f.NewGame();
        int found = 0;
        for (int p = 0; p < f.Core.DuelPresets.Count; p++)
        {
            var rec = f.Core.DuelPresets[p].Hero;
            if (rec.Class == "HERO_CLASS_BARBARIAN") continue;
            var fromArtifacts = rec.Artifacts.SelectMany(a => g.SpellsOfArtifact(a)).Where(s => !rec.Spells.Contains(s)).ToList();
            if (fromArtifacts.Count == 0) continue;
            found++;
            var (a, _) = Engine.Adventure.DuelHeroes(f.Db, p, 0, 5);
            Assert.True(rec.Spells.All(a.Spells.Contains));
            Assert.Contains(a.Spells, fromArtifacts.Contains);
            var battle = BattleSetup.CreateDuel(f.Core, a, Engine.Adventure.DuelHeroes(f.Db, p, 0, 5).B, 5, skipTactics: true, arena: "Grass_Big_01");
            Assert.Contains(battle.Heroes[0].Spells, fromArtifacts.Contains);
        }
        Assert.True(found > 0, "no preset wears a spell artifact");
    }

    [Fact]
    public void DuelHeroesKeepTheRecordsArmySlotsAndAreDeterministic()
    {
        for (int p = 0; p < f.Core.DuelPresets.Count; p++)
        {
            var (a, b) = Engine.Adventure.DuelHeroes(f.Db, p, (p + 1) % f.Core.DuelPresets.Count, 9);
            Assert.Equal(f.Core.DuelPresets[p].Hero.Army.Select(s => (s.Creature, s.Count)), a.Army.Select(s => (s.Creature, s.Count)));
            var (a2, _) = Engine.Adventure.DuelHeroes(f.Db, p, (p + 1) % f.Core.DuelPresets.Count, 9);
            Assert.Equal(a.Spells, a2.Spells);
            Assert.Equal(-1, a.Mana);
        }
    }
}
