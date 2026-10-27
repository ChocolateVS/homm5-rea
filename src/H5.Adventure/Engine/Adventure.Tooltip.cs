using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.Engine;

// What a player's adventure-map tooltip may reveal about a wandering monster (exe tooltip producer 004efc30,
// IAdvMapMonster slot +0x28, filling NWorld::SAdvMapMonsterTooltip for the window builder 005fc420).
public sealed partial class Adventure
{
    /// <summary>
    /// CHero vcall 0x230 (00b4bfd0): 12; Scouting (skill 0x14) → 12 + HeroSkills Scouting_VisibilityIncrease
    /// (stats +0x118); Disguise and Reckon (0x70) adds DisguiseAndReckon_VisibilityIncrease (+0x11c); each Crown of
    /// Many Eyes (artifact 12) +4; never below 0. The monster tooltip measures its reach with it (004fcf90).
    /// </summary>
    public int ReckonRadius(HeroState h)
    {
        var skills = Db.Stats.Root.Element("adventure")?.Element("HeroSkills");
        int r = 12;
        if (h.HasPerk("HERO_SKILL_SCOUTING")) r = skills.Int("Scouting_VisibilityIncrease", 0) + 12;
        if (h.HasPerk("HERO_SKILL_DISGUISE_AND_RECKON")) r += skills.Int("DisguiseAndReckon_VisibilityIncrease", 0);
        r += 4 * h.Artifacts.Count(a => a == "CROWN_OF_MANY_EYES");
        return Math.Max(0, r);
    }

    /// <summary>
    /// 004fcf90: every live hero of <paramref name="player"/> that has Scouting or Disguise and Reckon and stands on the
    /// monster's floor within <see cref="ReckonRadius"/> (dx² + dy² ≤ r²) lets the tooltip show exact counts
    /// (Scouting) and the monster's courage (Disguise and Reckon).
    /// </summary>
    public (bool ExactCounts, bool Courage) MonsterInsight(int player, Tile monster)
    {
        bool exact = false, courage = false;
        foreach (var h in S.Heroes.Where(h => h.Alive && h.Owner == player && h.Owner >= 0))
        {
            bool scouting = h.HasPerk("HERO_SKILL_SCOUTING"), reckon = h.HasPerk("HERO_SKILL_DISGUISE_AND_RECKON");
            if (!scouting && !reckon || h.Pos.Floor != monster.Floor) continue;
            long dx = monster.X - h.Pos.X, dy = monster.Y - h.Pos.Y, r = ReckonRadius(h);
            if (dx * dx + dy * dy > r * r) continue;
            exact |= scouting;
            courage |= reckon;
        }
        return (exact, courage);
    }

    /// <summary>004e9ac0: ARMY_DANGER_* index of the monsters' share of the total strength (0.2 / 0.4 / 0.6 / 0.8).</summary>
    public static int DangerLevel(double monsterShare)
    {
        float v = (float)monsterShare;
        return v < 0.2f ? 0 : v < 0.4f ? 1 : v < 0.6f ? 2 : v < 0.8f ? 3 : 4;
    }

    /// <summary>
    /// 004ea7a0: the threat level of <paramref name="guards"/> for <paramref name="hero"/> — monster strength M (no hero)
    /// and hero strength H (its army with the hero, 004ea5c0), level of M / (H + M); null without a hero or live
    /// monsters (004ea750). TODO(exe): 004ea5c0's hero term (vcall 0x184 / 0x120) and per-stack bonuses are not
    /// compared with <see cref="ArmyPower"/>.
    /// </summary>
    public int? MonsterDanger(HeroState? hero, IEnumerable<Stack> guards)
    {
        var live = guards.Where(g => g is { Count: > 0 } && !string.IsNullOrEmpty(g.Creature)).ToList();
        if (hero == null || !hero.Alive || live.Count == 0) return null;
        double m = ArmyPower(live, null), h = ArmyPower(hero.Army.Live(), hero);
        return DangerLevel(m / (h + m));
    }
}
