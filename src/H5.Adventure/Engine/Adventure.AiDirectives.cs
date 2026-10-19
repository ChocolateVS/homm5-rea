using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Readers of the map-script AI/hiring directives (natives in H5.Scripting/Api/AdvApi.Ai.cs). The state
// lives in PlayerState/HeroState/TownState so it is saved, hashed and replicated with the game.
public sealed partial class Adventure
{
    /// <summary>Whether a hero passes a hiring filter (exe FUN_00c069d0: script name + hero race).</summary>
    public static bool FilterAccepts(GameDatabase core, HeroFilter f, string defId) =>
        core.Heroes.TryGetValue(defId, out var d) ? f.Accepts(defId, d.Town) : !f.DeniedHeroes.Contains(defId);

    /// <summary>
    /// The heroes a town's tavern shows the player (exe FUN_00636a10): the player's offers minus those the
    /// town's filter (AllowHeroHiringByRaceInTown / AllowHiringOfHeroInTown) rejects; when the first is
    /// rejected the second moves into slot 0. Applies to human and AI players alike.
    /// </summary>
    public static string?[] TownTavernOffers(GameDatabase core, PlayerState p, TownState t)
    {
        var offers = new string?[p.Tavern.Length];
        int n = 0;
        foreach (var id in p.Tavern)
            if (id != null && FilterAccepts(core, t.HireFilter, id)) offers[n++] = id;
        return offers;
    }

    public string?[] TownTavernOffers(PlayerState p, TownState t) => TownTavernOffers(Core, p, t);

    /// <summary>Hero count up to which the player may hire (exe FUN_004f4570: heroes &lt; max − SetPlayerHeroesCountNotForHire).</summary>
    public int HiringHeroLimit(PlayerState p) => MaxHeroesPerPlayer - p.HeroesNotForHire;

    /// <summary>
    /// Whether a player is given a turn (exe FUN_0098e410): an active player with a town always plays; one
    /// with neither towns nor heroes never does; without towns, an AI player flagged by
    /// DoNotGiveTurnToPlayerAIIfNoTownsAndActiveHeroes plays only while one of its heroes is AI-enabled.
    /// </summary>
    public bool GetsTurn(PlayerState p)
    {
        if (!p.Alive) return false;
        if (S.Towns.Any(t => t.Owner == p.Index)) return true;
        var heroes = S.Heroes.Where(h => h.Alive && h.Owner == p.Index).ToList();
        if (heroes.Count == 0) return false;
        if (!p.Human && p.AiNoTurnWithoutTownsAndActiveHeroes) return heroes.Any(h => h.AiEnabled);
        return true;
    }

    /// <summary>
    /// DenyAIHeroFlee / DenyAIHeroesFlee / SetAIHeroFleeControl (exe FUN_00c972d0 head, list test
    /// FUN_00c97b10): an AI hero never flees nor surrenders when its own list holds "" or the enemy hero's
    /// name, or — unless its flee control is set — when its player's list does.
    /// </summary>
    public bool AiFleeDenied(HeroState own, HeroState? enemy)
    {
        var p = PlayerByIndex(own.Owner);
        if (p == null || p.Human) return false;
        bool Lists(List<string> l) => l.Contains("") || (enemy != null && (l.Contains(enemy.DefId) || l.Contains(enemy.Name)));
        if (Lists(own.AiNoFleeFrom)) return true;
        return !own.AiFleeControl && Lists(p.AiNoFleeFrom);
    }

    /// <summary>
    /// The tavern-generation filter of AllowPlayerTavernRace / AllowPlayerTavernHero. GAP: the exe reader of
    /// the player filter (+0x644) is not traced; the CHeroesFilter rule (FUN_00c069d0) is applied when the
    /// player's offers are rolled, and offers the new filter rejects are re-rolled when it changes.
    /// </summary>
    bool TavernAccepts(PlayerState p, string defId) => FilterAccepts(Core, p.TavernFilter, defId);

    /// <summary>Re-rolls the offers of <paramref name="p"/> that its tavern filter now rejects (see <see cref="TavernAccepts"/>).</summary>
    public void ScriptRefreshTavern(PlayerState p)
    {
        if (p.Tavern.All(id => id == null || TavernAccepts(p, id))) return;
        for (int i = 0; i < p.Tavern.Length; i++)
            if (p.Tavern[i] is { } id && !TavernAccepts(p, id)) p.Tavern[i] = null;
        for (int i = 0; i < p.Tavern.Length; i++)
            if (p.Tavern[i] == null) RollTavernSlot(p, i);
    }
}
