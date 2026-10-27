using H5.Adventure.State;

namespace H5.Adventure.Engine;

public sealed partial class Adventure
{
    /// <summary>
    /// Custom-game starting bonuses (H5_Game.exe 00c9fa80, after every player and the map's heroes exist): for each
    /// setup slot that is not closed — a computer slot only when the map player CanBeComputerPlayer (slot +0x21) — and
    /// unless the setup model's flag +0x1c is set (vf 0x54 = 00ca11c0), the slot's bonus (+0x4c) goes to the player's
    /// vf 0x1c = 004fd860. Players without a setup bonus (scenario/campaign/network starts) get none.
    /// </summary>
    void ApplyStartBonuses(GameSetup setup)
    {
        foreach (var ps in setup.Players)
        {
            if (ps.Bonus is not { } bonus || PlayerByIndex(ps.MapPlayer) is not { } p) continue;
            var mp = Map.Players[ps.MapPlayer];
            if (!p.Human && !mp.CanBeComputer) continue;
            ApplyStartBonus(p, bonus);
        }
    }

    /// <summary>
    /// 004fd860 (CPlayer vtable 00e1a09c slot 0x1c). Bonus 0 random, 1 artifact, 2 gold, 3 resource; anything outside
    /// 1..3 is drawn: with heroes 1 + rng % 3, without 2 + rng % 2. Artifact: a random hero of the player
    /// (list CPlayer +0x34, rng % count) gets a minor artifact from the map's generator (world +0xf8 vf 0xc(0, 0, seed)
    /// = classes 0..0), nothing without heroes. Gold: (rng % 6) · 100 + 500. Resource by the player's race (+0x198,
    /// jump table 004fdadc): (rng &amp; 3) + 3 of wood and ore (Haven, Necropolis; ore drawn first), crystal (Preserve),
    /// gems (Academy, Fortress), sulfur (Dungeon), mercury (Inferno, Stronghold). Returns the bonus applied.
    /// </summary>
    public int ApplyStartBonus(PlayerState p, int bonus)
    {
        var heroes = S.Heroes.Where(h => h.Owner == p.Index).ToList();
        if (bonus < 1 || bonus > 3)
        {
            int lo = heroes.Count > 0 ? 1 : 2;
            bonus = lo + S.Rng.Next(4 - lo);
        }
        switch (bonus)
        {
            case 1:
            {
                if (heroes.Count == 0) break;
                var h = heroes[S.Rng.Next(heroes.Count)];
                // hero vf 0x80 (can take) / vf 0x84 (take) not decoded; no message is posted by 004fd860 itself.
                if (GenerateArtifact(0, 0) is { } art) h.Artifacts.Add(art);
                break;
            }
            case 2:
                p.Purse[Purse.Gold] += S.Rng.Next(6) * 100 + 500;
                break;
            case 3:
                switch (p.Race)
                {
                    case "TOWN_HEAVEN" or "TOWN_NECROMANCY":
                        p.Purse[Purse.Ore] += S.Rng.Next(4) + 3;
                        p.Purse[Purse.Wood] += S.Rng.Next(4) + 3;
                        break;
                    case "TOWN_PRESERVE": p.Purse[Purse.Crystal] += S.Rng.Next(4) + 3; break;
                    case "TOWN_ACADEMY" or "TOWN_FORTRESS": p.Purse[Purse.Gem] += S.Rng.Next(4) + 3; break;
                    case "TOWN_DUNGEON": p.Purse[Purse.Sulfur] += S.Rng.Next(4) + 3; break;
                    case "TOWN_INFERNO" or "TOWN_STRONGHOLD": p.Purse[Purse.Mercury] += S.Rng.Next(4) + 3; break;
                }
                break;
        }
        return bonus;
    }
}
