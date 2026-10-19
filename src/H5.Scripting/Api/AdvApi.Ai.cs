using H5.Adventure.State;
using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

// AI and hiring directives (table 0x00f35000, SetPlayerHeroesCountNotForHire in 0x00f34c28). Each native
// validates like its exe wrapper and posts a world command; the state lives in the adventure state
// (HeroState/PlayerState/TownState) so it is saved and replicated, and the readers are the engine's
// (Adventure.AiDirectives.cs, OriginalAdventureAI, tavern/hiring, turn order, battle setup).
public sealed partial class AdventureScripting
{
    /// <summary>Script town type 0..7 → TOWN_* (exe FUN_00af10f0 maps it to race 3..10; anything else is "all").</summary>
    static string? RaceArg(int townType) => townType is >= 0 and <= 7 ? HeroFilter.Races[townType] : null;

    /// <summary>The "Invalid player id #%i" / "Player #%i is not exists" check most AI natives share.</summary>
    PlayerState AiPlayerArg(HostContext c, int i, string invalid = "Invalid player id #{0}")
    {
        int id = c.Int(i);
        if (id < 1 || id > 8) throw c.Error(string.Format(invalid, id));
        return Game.Player(id - 1) ?? throw c.Error($"Player #{id} is not exists");
    }

    /// <summary>Hero by script name, with the wrappers' "Hero name is not specified" / "Hero \"%s\" is not exists".</summary>
    HeroState AiHeroArg(HostContext c, int i)
    {
        var name = c.Str(i);
        if (name.Length == 0) throw c.Error("Hero name is not specified");
        return Game.HeroByNameAnywhere(name) ?? throw c.Error($"Hero \"{name}\" is not exists");
    }

    /// <summary>An attractor target (map object or hero, vf 0x1c) by name, or null.</summary>
    string? AttractorObject(HostContext c, int i)
    {
        var name = c.Str(i);
        return Game.ObjectByName(name) != null || Game.HeroByName(name) != null ? name : null;
    }

    /// <summary>A town for the town-filter natives (0x005e4270/0x005e4530).</summary>
    TownState FilterTownArg(HostContext c, int i)
    {
        var name = c.Str(i);
        var o = Game.ObjectByName(name);
        if (o == null && Game.HeroByName(name) == null) throw c.Error($"Object \"{name}\" does not exist!");
        return (o != null ? S.Town(o.Index) : null) ?? throw c.Error($"Object \"{name}\" is not a town!");
    }

    void RegisterAiApi()
    {
        // 0x005da500, command CEnableHeroAICmd (Execute 0x0042f2f0: AI settings +0).
        Reg("EnableHeroAI", c =>
        {
            var h = AiHeroArg(c, 0);
            if (h.Owner >= 0 && Game.Player(h.Owner) is { Human: true }) throw c.Error($"Hero \"{c.Str(0)}\" is not belong to AI player");
            h.AiEnabled = c.Bool(1);
        });

        // 0x005dfe10, CSetAIHeroAttractor (Execute 0x0042bd70: clamp -1..2, FUN_00b4e880 on settings +0x14).
        Reg("SetAIHeroAttractor", c =>
        {
            int priority = c.Int(2);
            if (priority < -1 || priority > 2) throw c.Error("Priority must be between -1 and 2");
            var h = Game.HeroByNameAnywhere(c.Str(1)) ?? throw c.Error($"Hero '{c.Str(1)}' not found");
            var obj = AttractorObject(c, 0) ?? throw c.Error($"Object \"{c.Str(0)}\" not found");
            AiAttractor.Set(h.AiAttractors, obj, priority);
        });

        // 0x005e00e0, CSetAIPlayerAttractor (Execute 0x00428a60: player vf 0x1e4).
        Reg("SetAIPlayerAttractor", c =>
        {
            int priority = c.Int(2);
            if (priority < -1 || priority > 2) throw c.Error("Priority must be between -1 and 2");
            int id = c.Int(1);
            if (id < 1 || id > 8) throw c.Error($"Invalid player id #{id}");
            var p = Game.Player(id - 1);
            if (p is not { Alive: true }) throw c.Error($"Player #{id} is not active");
            var obj = AttractorObject(c, 0) ?? throw c.Error($"Object \"{c.Str(0)}\" not found");
            AiAttractor.Set(p.AiAttractors, obj, priority);
        });

        // 0x005da7a0, CEnablePlayerAIHiringHeroesInTownCmd; read by the AI hiring (FUN_0086aa50, AI vf 0x54).
        Reg("EnableAIHeroHiring", c =>
        {
            var p = AiPlayerArg(c, 0);
            if (p.Human) throw c.Error($"Player #{c.Int(0)} is human");
            var name = c.Str(1);
            if (name.Length == 0) throw c.Error("Town name is not specified");
            var o = Game.ObjectByName(name);
            var t = (o != null ? S.Town(o.Index) : null) ?? throw c.Error($"Town \"{name}\" is not exists on map");
            if (c.Bool(2)) p.AiHiringDisabledTowns.Remove(t.Object);
            else if (!p.AiHiringDisabledTowns.Contains(t.Object)) p.AiHiringDisabledTowns.Add(t.Object);
        });

        // 0x005e22b0 (Execute 0x0042f660 on settings +0x20): deny ≠ 0 adds the enemy name, 0 removes it.
        Reg("DenyAIHeroFlee", c =>
        {
            var h = AiHeroArg(c, 0);
            SetDenyFlee(h.AiNoFleeFrom, c.Int(1) != 0, c.Str(2));
        });

        // 0x005e2760: the player's list (AI info +0x104), read when the hero's flee control is off.
        Reg("DenyAIHeroesFlee", c =>
        {
            var p = AiPlayerArg(c, 0);
            SetDenyFlee(p.AiNoFleeFrom, c.Int(1) != 0, c.Str(2));
        });

        // 0x005e2510 (Execute 0x0042c5c0: settings +0x2c).
        Reg("SetAIHeroFleeControl", c =>
        {
            var h = AiHeroArg(c, 0);
            h.AiFleeControl = c.Int(1) != 0;
        });

        // 0x005e3aa0, CBlockTownGarrisonForAICmd.
        Reg("BlockTownGarrisonForAI", c =>
        {
            var name = c.Str(0);
            // GAP: an empty name dereferences a null object in the exe; nothing happens here.
            if (name.Length == 0) return;
            var o = Game.ObjectByName(name);
            if (o == null && Game.HeroByName(name) == null) throw c.Error($"Object \"{name}\" does not exist!");
            var t = (o != null ? S.Town(o.Index) : null) ?? throw c.Error($"Object \"{name}\" is not town!");
            t.AiGarrisonBlocked = c.Int(1) != 0;
        });

        // 0x005e3d40 (Execute 0x00429ff0 on AI info +0x13c); players without AI info (humans) are ignored.
        Reg("AllowHeroHiringByRaceForAI", c =>
        {
            var p = AiPlayerArg(c, 0);
            if (!p.Human) SetRaceFilter(p.AiHireFilter, c.Int(1), c.Int(2) != 0);
        });

        // 0x005e4000 (Execute 0x0042a040): -1 → race rule again, 0 → deny, other → allow despite the race.
        Reg("AllowHiringOfHeroForAI", c =>
        {
            var p = AiPlayerArg(c, 0);
            if (!p.Human) SetHeroFilter(p.AiHireFilter, c.Str(1), c.Int(2));
        });

        // 0x005e4270 / 0x005e4530: the town's filter (town +0x224), for every player.
        Reg("AllowHeroHiringByRaceInTown", c => SetRaceFilter(FilterTownArg(c, 0).HireFilter, c.Int(1), c.Int(2) != 0));
        Reg("AllowHiringOfHeroInTown", c => SetHeroFilter(FilterTownArg(c, 0).HireFilter, c.Str(1), c.Int(2)));

        // 0x005e47f0 (Execute 0x0042fb70 on the player's tavern filter +0x644).
        Reg("AllowPlayerTavernRace", c =>
        {
            var p = AiPlayerArg(c, 0);
            int type = c.Int(1);
            var race = RaceArg(type) ?? throw c.Error($"Invalid town type {type}!");
            p.TavernFilter.SetRace(race, c.Int(2) != 0);
            Game.ScriptRefreshTavern(p);
        });

        // 0x005e4ac0 (Execute 0x0042fc40): 0 → denied, 1 → allowed despite the race, other → neither.
        Reg("AllowPlayerTavernHero", c =>
        {
            var p = AiPlayerArg(c, 0);
            var hero = c.Str(1);
            switch (c.Int(2))
            {
                case 0: p.TavernFilter.SetHero(hero, false); break;
                case 1: p.TavernFilter.SetHero(hero, true); break;
                default: p.TavernFilter.ClearHero(hero); break;
            }
            Game.ScriptRefreshTavern(p);
        });

        // 0x005d11c0 (Execute 0x00428d30: clamp 0..max, player vf 0xac → +0x610), read by the hire check
        // FUN_004f4570 and the AI hiring FUN_0086b0a0: heroes < max − count.
        Reg("SetPlayerHeroesCountNotForHire", c =>
        {
            int id = c.Int(0), count = c.Int(1);
            if (id < 1 || id > 8) throw c.Error("Player ID must be 1 through 8");
            // GAP: the exe dereferences a missing player (crash); nothing happens here.
            if (Game.Player(id - 1) is not { } p) return;
            int max = H5.Adventure.Engine.Adventure.MaxHeroesPerPlayer;   // FUN_0098de60 (8 in normal play)
            if (count < 0 || count > max) throw c.Error($"Invalid heroes count, must be 0..{max}");
            p.HeroesNotForHire = count;
        });

        // 0x005e7750, CDoNotGiveTurnToPlayerAIIfNoTownsAndActiveHeroesCmd; read by the turn order (FUN_0098e410).
        Reg("DoNotGiveTurnToPlayerAIIfNoTownsAndActiveHeroes", c =>
        {
            var p = AiPlayerArg(c, 0, "Incorrect player ID #{0}");
            p.AiNoTurnWithoutTownsAndActiveHeroes = c.Int(1) != 0;
        });
    }

    /// <summary>FUN_0042f660: deny adds the name when absent, allow removes it ("" = every enemy).</summary>
    static void SetDenyFlee(List<string> list, bool deny, string enemy)
    {
        if (deny) { if (!list.Contains(enemy)) list.Add(enemy); }
        else list.Remove(enemy);
    }

    /// <summary>FUN_00429ff0: a town type 0..7 sets that race; any other value sets every race.</summary>
    static void SetRaceFilter(HeroFilter f, int townType, bool allow)
    {
        if (RaceArg(townType) is { } race) f.SetRace(race, allow);
        else f.SetAllRaces(allow);
    }

    /// <summary>FUN_0042a040: -1 clears the hero's entries, otherwise allow = mode ≠ 0.</summary>
    static void SetHeroFilter(HeroFilter f, string hero, int mode)
    {
        if (mode == -1) f.ClearHero(hero);
        else f.SetHero(hero, mode != 0);
    }
}
