using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

public sealed partial class Adventure
{
    /// <summary>
    /// Custom-game starting bonuses (H5_Game.exe 00c9fa80, after every player and the map's heroes exist): for each
    /// setup slot that is not closed — a computer slot only when the map player CanBeComputerPlayer (slot +0x21) — and
    /// unless the setup model's flag +0x1c is set (vf 0x54 = 00ca11c0), the slot's bonus (+0x4c) goes to the player's
    /// vf 0x1c = 004fd860. The flag is set only by the campaign mission start (006948c0 → vf 0x5c = 00ca0c60, which
    /// copies the campaign start info to model +0x20), so campaign missions get none. Players without a setup bonus
    /// (map started directly, without a setup screen) get none.
    /// </summary>
    void ApplyStartBonuses(GameSetup setup)
    {
        if (setup.Campaign) return;
        foreach (var ps in setup.Players)
        {
            if (ps.Bonus is not { } bonus || PlayerByIndex(ps.MapPlayer) is not { } p) continue;
            var mp = Map.Players[ps.MapPlayer];
            if (!p.Human && !mp.CanBeComputer) continue;
            ApplyStartBonus(p, bonus);
        }
    }

    /// <summary>
    /// The setup model's slot bonus before any change (00ca0240, slot +0x4c from map player +0x98 DefaultBonus): ARTIFACT
    /// only for a player with a hero (one placed on the map, the map's main hero or HeroInTown), GOLD, RESOURCE, else random.
    /// The custom-game, hot-seat and network setups all start from this model.
    /// </summary>
    public static int DefaultStartBonus(AdvMap map, MapPlayer mp)
    {
        bool hasHero = map.Objects.Any(o => o.Kind == ObjectKind.Hero && o.Owner == mp.Index) || mp.MainHeroUid != null || mp.HeroInTown;
        return mp.DefaultBonus switch
        {
            "PLAYER_BONUS_ARTIFACT" => hasHero ? 1 : 0,
            "PLAYER_BONUS_GOLD" => 2,
            "PLAYER_BONUS_RESOURCE" => 3,
            _ => 0,
        };
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
