using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>
/// Callbacks the map-script layer (H5.Scripting) installs on the rules engine. All optional;
/// with no hooks the engine behaves exactly as without scripts.
/// </summary>
public interface IAdventureScriptHooks
{
    /// <summary>SetRegionBlocked: the tile is closed to this player's heroes.</summary>
    bool IsTileBlocked(int player, Tile tile);
    /// <summary>
    /// A hero is about to interact with an object. Return true when scripts take over the
    /// interaction (object disabled with SetObjectEnabled / SetDisabledObjectMode), in which case
    /// the engine skips the normal effect.
    /// </summary>
    bool InterceptVisit(HeroState hero, MapObject obj);
    /// <summary>A hero stepped onto a tile; return true to stop the movement there (enter-and-stop regions).</summary>
    bool StopMovementAt(HeroState hero, Tile from, Tile to);
    /// <summary>
    /// SetDisabledObjectMode mode (0..3) of an object disabled with SetObjectEnabled, or null while it is enabled
    /// (read by the hover action 005ff8f0 through the getters 004544f0 / 004545e0).
    /// </summary>
    int? DisabledObjectMode(MapObject obj) => null;
}

public sealed partial class Adventure
{
    public IAdventureScriptHooks? ScriptHooks { get; set; }

    /// <summary>
    /// Attaches combat scripts to a battle about to be played out (null = none). The stock engine
    /// runs combat scripts only in the combat screen (FUN_0054c2f0), never in multiplayer/hotseat.
    /// </summary>
    public Func<H5.Combat.Battle, H5.Combat.IBattleScriptSession?>? BattleScripts { get; set; }

    /// <summary>
    /// Where drivers (bots, sessions) should send commands so that scripts see them; null means
    /// <see cref="Apply"/> directly. The script host sets this to its own Apply wrapper.
    /// </summary>
    public Func<AdvCommand, AdvResult>? CommandSink { get; set; }

    /// <summary>
    /// Runs a script-initiated change and returns the events it produced, without disturbing
    /// the events of a command currently being applied.
    /// </summary>
    public List<AdvEvent> RunScriptAction(Action action)
    {
        var saved = _ev;
        _ev = new List<AdvEvent>();
        try { action(); }
        finally
        {
            var produced = _ev;
            _ev = saved;
            _scriptCaptured = produced;
        }
        return _scriptCaptured;
    }

    List<AdvEvent> _scriptCaptured = new();

    // ---- wrappers over engine internals, for script bindings (call inside RunScriptAction) ----

    public HeroState? HeroByName(string name) =>
        S.Heroes.Find(h => h.Alive && (h.DefId == name || h.Name == name));

    /// <summary>
    /// A hero by script name wherever it is: on the map, in a player's reserve, or dead — hero
    /// script functions work "regardless of where the hero is" (script reference).
    /// </summary>
    public HeroState? HeroByNameAnywhere(string name) =>
        HeroByName(name)
        ?? S.OffMapHeroes.Find(h => h.DefId == name || h.Name == name)
        ?? S.Heroes.Find(h => h.DefId == name || h.Name == name);

    public MapObject? ObjectByName(string name) =>
        string.IsNullOrEmpty(name) ? null : Map.Objects.Find(o => o.Name == name && !(S.Objects.TryGetValue(o.Index, out var os) && os.Removed));

    public IEnumerable<MapObject> ObjectsAtTile(Tile t) => ObjectsAt(t);
    public HeroState? HeroAtTile(Tile t) => HeroAt(t);
    public bool ArePlayersAllied(int a, int b) => Allied(a, b);

    /// <summary>
    /// GiveArtefact (exe CGiveArtifact cmd Execute 0x00431fe0): the artifact is worn when its slot
    /// has room (hero vf 0x80/0x84), otherwise it goes to the backpack.
    /// </summary>
    public void ScriptGiveArtifact(HeroState h, string artifact)
    {
        if (Core.Artifacts.TryGetValue(artifact, out var def) && SlotCapacity(def.Slot) is var cap && cap > 0
            && h.Artifacts.Count(a => Core.Artifacts.TryGetValue(a, out var d) && d.Slot == def.Slot) < cap)
            GiveArtifact(h, artifact);
        else
        {
            h.Backpack.Add(artifact);
            Emit(new ArtifactGained(h.Id, artifact));
            RumourArtifactGained(h, artifact);
        }
    }

    /// <summary>
    /// RemoveArtefact (exe cmd Execute 0x0042b1d0, hero vf 0x7c = 0x00b4b6f0 / vf 0x90): the worn copy
    /// first, else one from the backpack. Artifact stat bonuses are not part of the base attributes
    /// (they apply in battle), so nothing is subtracted here.
    /// </summary>
    public bool ScriptRemoveArtifact(HeroState h, string artifact)
    {
        if (h.Artifacts.Remove(artifact)) TakeInstance(h, artifact, true);
        else if (h.Backpack.Remove(artifact)) TakeInstance(h, artifact, false);
        else return false;
        Emit(new HeroStatChanged(h.Id, "artifact_removed:" + artifact, 0));
        return true;
    }

    public bool ScriptLearnSpell(HeroState h, string spell) => LearnSpell(h, spell, force: true);

    public void ScriptAddStat(HeroState h, string stat, int delta) => AddStat(h, stat, delta);

    public void ScriptGainExperience(HeroState h, long amount) => GainExperience(h, amount);

    public void ScriptRemoveObject(int index) => RemoveObject(index);

    public void ScriptCaptureObject(MapObject o, int player)
    {
        var os = S.Obj(o.Index);
        var p = PlayerByIndex(player);
        if (p != null) Capture(o, os, p);
        else
        {
            os.Owner = -1;
            Emit(new ObjectCaptured(o.Index, -1));
        }
    }

    public void ScriptCaptureTown(HeroState? h, TownState t, int player)
    {
        if (h != null && h.Owner == player) { CaptureTown(h, t); return; }
        if (t.Owner != player && PlayerByIndex(player) != null) RumourCaptured(O(t.Object), player);
        t.Owner = player;
        t.OwnedSinceDay = S.Day;
        S.Obj(t.Object).Owner = player;
        Emit(new ObjectCaptured(t.Object, player));
    }

    public void ScriptRemoveHero(HeroState h, string reason) => RemoveHero(h, reason);

    public void ScriptReveal(int player, Tile center, int radius) => Reveal(ScriptPlayer(player, create: true), center, radius);

    /// <summary>OpenRegionFog (CModifyPlayerFogCmd): explores exactly the given cells.</summary>
    public void ScriptRevealTiles(int player, IEnumerable<Tile> tiles)
    {
        var p = ScriptPlayer(player, create: true);
        if (p == null) return;
        foreach (var g in tiles.GroupBy(t => t.Floor))
        {
            var f = Map.Floor(g.Key);
            if (f == null || g.Key >= p.Explored.Count) continue;
            var ex = p.Explored[g.Key];
            int n = 0;
            foreach (var t in g)
            {
                if (!f.InBounds(t.X, t.Y)) continue;
                int i = t.Y * f.Width + t.X;
                if (!ex[i]) { ex[i] = true; n++; }
            }
            if (n > 0) Emit(new TilesRevealed(p.Index, g.Key, n));
        }
    }

    public void ScriptJoinArmy(HeroState h, List<Stack> creatures) => JoinArmy(h, creatures);

    /// <summary>Starts a battle like StartCombat/SiegeTown: the pending battle is resolved by the client or QuickCombat.</summary>
    public void ScriptStartBattle(HeroState attacker, int defenderHero, int defenderObject, Tile where, List<Stack> army, bool siege) =>
        StartBattle(attacker, defenderHero, defenderObject, where, army, siege);

    /// <summary>Puts a hero definition on the map for a player (DeployReserveHero).</summary>
    public HeroState ScriptDeployHero(HeroDef def, int owner, Tile pos)
    {
        var h = S.OffMapHeroes.Find(x => x.DefId == def.Id);
        if (h != null) { S.OffMapHeroes.Remove(h); h.Pos = pos; h.Owner = owner; h.Alive = true; }
        else { h = HeroFromDef(def, owner, pos); SetUpHeroBook(h); }
        S.Heroes.Add(h);
        S.HeroPool.Remove(def.Id);
        RevealAround(h);
        Emit(new HeroHired(h.Id, h.DefId, -1));
        return h;
    }

    /// <summary>Moves a hero along the cheapest path as far as allowed, triggering visits like a player move.</summary>
    public string? ScriptMoveHero(HeroState h, Tile to, bool ignoreMovePoints)
    {
        int saved = h.MovePoints;
        if (ignoreMovePoints) h.MovePoints = int.MaxValue / 2;
        var err = DoMove(new MoveHeroCmd(h.Id, to) { Player = h.Owner });
        if (ignoreMovePoints) h.MovePoints = saved;
        return err;
    }

    /// <summary>Instantly places a hero (SetObjectPosition for heroes).</summary>
    public void ScriptTeleportHero(HeroState h, Tile to)
    {
        var from = h.Pos;
        h.Pos = to;
        if (h.InTown >= 0 && TownAt(to)?.Object != h.InTown) h.InTown = -1;
        RevealAround(h);
        Emit(new HeroMoved(h.Id, new[] { from, to }, h.MovePoints));
    }

    /// <summary>Adds experience exactly (no Enlightenment bonus) and applies any level-ups.</summary>
    public void ScriptAddExperienceRaw(HeroState h, long amount)
    {
        if (amount <= 0) return;
        h.Experience += amount;
        Emit(new ExperienceGained(h.Id, amount, h.Experience));
        int maxLevel = Map.HeroMaxLevel > 0 ? Map.HeroMaxLevel : 40;
        while (h.Level < maxLevel && h.Experience >= Core.Experience.For(h.Level + 1))
            LevelUp(h);
    }

    /// <summary>LevelUpHero: grant exactly the experience missing for the next level.</summary>
    public void ScriptLevelUp(HeroState h)
    {
        long next = Core.Experience.For(h.Level + 1);
        if (next > h.Experience) ScriptAddExperienceRaw(h, next - h.Experience);
    }

    public void ScriptGive(int player, int resource, int amount)
    {
        var p = ScriptPlayer(player, create: true);
        if (p == null) return;
        p.Purse[resource] = Math.Max(0, p.Purse[resource] + amount);
        var delta = new int[7];
        delta[resource] = amount;
        Emit(new ResourcesChanged(p.Index, delta, p.Purse.R.ToArray()));
    }

    public void ScriptEndGame(int? winnerTeam, string reason)
    {
        if (S.Finished) return;
        S.Finished = true;
        S.WinnerTeam = winnerTeam;
        Emit(new GameEnded(winnerTeam, reason));
    }

    public void ScriptEliminate(int player)
    {
        var p = PlayerByIndex(player);
        if (p != null && p.Alive) Eliminate(p);
    }

    public void ScriptCheckVictory() => CheckVictory();

    public PlayerState? Player(int index) => PlayerByIndex(index);

    /// <summary>
    /// The player object a map script acts on. The exe's game start (00c9fa80) creates every player number 1..8; one
    /// that is not in the game is a 004ffb60 player in state 4 (race RACE_NO_TYPE, team -1) and the script player natives
    /// (GetPlayerResource 005c19a0, SetPlayerResource 005c1c30, SetPlayerStartResources 005c1fb0, Has/GiveBorderguardKey
    /// 005c22a0 / 005c2540, OpenCircleFog 005da1e0, OpenRegionFog 005d9df0, IsObjectVisible 005cc2a0) refuse only a
    /// missing player or state 0, so they act on it. Our engine leaves such players out of <see cref="AdvState.Players"/>;
    /// their purse, keys and fog live in <see cref="AdvState.AbsentPlayers"/>, made on the first change.
    /// </summary>
    public PlayerState? ScriptPlayer(int index, bool create = false)
    {
        if (PlayerByIndex(index) is { } p) return p;
        if (index < 0 || index > 7) return null;
        var a = S.AbsentPlayers.Find(x => x.Index == index);
        if (a == null && create)
        {
            // 004ffb60 → 004ff2a0(race 2 = RACE_NO_TYPE, team -1, empty name); the purse starts empty (009d09c0).
            a = new PlayerState { Index = index, Team = -1 };
            foreach (var f in Map.Floors) a.Explored.Add(f == null ? Array.Empty<bool>() : new bool[f.Width * f.Height]);
            S.AbsentPlayers.Add(a);
        }
        return a;
    }

    /// <summary>
    /// Builds the next level for free, ignoring the daily limit (the free path of 00cd4a40). UpgradeTownBuilding passes
    /// <paramref name="raiseLimit"/> false: the exe leaves the map limit (+0x28) as it is.
    /// </summary>
    public void ScriptUpgradeBuilding(TownState t, string building, bool raiseLimit = true)
    {
        int next = t.Level(building) + 1;
        TownSpellsOnBuild(t, building, next - 1);   // the free path of 00cd4a40 runs the same effects
        t.Built[building] = next;
        if (HasTradeGuild(t) && building == "TB_SPECIAL_4") RollTradeGuild(t);
        if (building.StartsWith("TB_DWELLING_") && next == 1 && int.TryParse(building[^1..], out var tier))
            t.Available[tier - 1] += WeeklyGrowth(t, tier);
        if (raiseLimit && t.MaxLevel.GetValueOrDefault(building) < next) t.MaxLevel[building] = next;
        Emit(new BuildingBuilt(t.Object, building, next));
        StockArtifactMerchants();   // 00cd4a40 at 00cd4db7, as in DoBuild
        BuildingEffectsOnHeroes(t, building);
    }

    public void ScriptSetBuildingLevel(TownState t, string building, int level)
    {
        if (level <= 0) t.Built.Remove(building); else t.Built[building] = level;
        Emit(new BuildingBuilt(t.Object, building, level));
    }

    /// <summary>
    /// TransformTown (0x005c9ee0): the town object is removed (CRemoveObjectCmd) and a new town of the given type created
    /// in its place (CCreateTownCmd, Execute 0x0042e8a0 → CAdvMapTown ctor 0x00cd7340 → init FUN_00cd5be0) from the
    /// record the old town fills with vf 0xf4 (FUN_00cd3ce0): name, owner, capture / hero-deploy triggers, ship tile,
    /// specialization, editable name/biography, script, CanCaptureOnlyNotVisit, AllowQuickCombat and
    /// GarrisonBlockedForAI. The record's buildings, army slots and spells stay empty (FUN_004369e0), so the new town
    /// starts like a map town with no buildings list: town hall 1, every building allowed to the type's maximum,
    /// empty garrison and creature pools, newly rolled guild, runes, warcries and trade guild.
    /// </summary>
    public void ScriptTransformTown(TownState t, string type)
    {
        var o = Map.Objects[t.Object];
        var n = InitTown(o, type, t.Owner, t.Specialization, Array.Empty<TownBuildingInit>(), Array.Empty<ArmySlot>());
        n.Name = t.Name;
        n.AiGarrisonBlocked = t.AiGarrisonBlocked;
        int i = S.Towns.IndexOf(t);
        if (i >= 0) S.Towns[i] = n; else S.Towns.Add(n);
    }

    /// <summary>RazeTown: the town disappears from play (its razed static replaces it visually).</summary>
    public void ScriptRazeTown(TownState t)
    {
        S.Towns.Remove(t);
        foreach (var h in S.Heroes.Where(h => h.InTown == t.Object)) { h.InTown = -1; h.Garrisoned = false; }
        RemoveObject(t.Object);
        CheckEliminations();
    }

    /// <summary>
    /// ControlHeroCustomAbility (CControlHeroCustomAbilityCmd, Execute 0x0042a5b0 → hero vf 0x380 = FUN_00b56970): ability
    /// 1..4 is the book spell SPELL_ABILITY_CUSTOM1..4 (0x15c..0x15f). Mode 1 adds it to the known list and the book and
    /// takes it off the disabled list; 0 adds it the same way and puts it on the disabled list; -1 removes it from all.
    /// GAP: the hero's further int list +0x310 that the same calls fill is not identified.
    /// </summary>
    public void ScriptControlCustomAbility(HeroState h, int ability, int mode)
    {
        var spell = "SPELL_ABILITY_CUSTOM" + ability;
        if (mode == -1)
        {
            h.Spells.Remove(spell);
            h.SpellSources.Remove(spell);
            h.DisabledSpells.Remove(spell);
            return;
        }
        if (!h.Spells.Contains(spell)) h.Spells.Add(spell);
        if (mode == 0) { if (!h.DisabledSpells.Contains(spell)) h.DisabledSpells.Add(spell); }
        else h.DisabledSpells.Remove(spell);
    }

    public int ScriptHeroMorale(HeroState h) => HeroMorale(h);
    public int ScriptHeroLuck(HeroState h) => HeroLuck(h);
    public int ScriptMaxMana(HeroState h) => MaxMana(h);

    /// <summary>Applies a learned perk's immediate effects (as on level-up).</summary>
    public void ScriptLearnPerk(HeroState h, string perk)
    {
        if (h.Perks.Contains(perk)) return;
        h.Perks.Add(perk);
        ApplyPerkOnLearn(h, perk);
        Emit(new SkillLearned(h.Id, perk, Mastery.Basic));
    }

    public void ScriptSetSkill(HeroState h, string skill, Mastery m)
    {
        h.Skills.RemoveAll(s => s.SkillId == skill);
        if (m > Mastery.None) h.Skills.Add(new SkillEntry(skill, m));
        Emit(new SkillLearned(h.Id, skill, m));
    }

    // ---- dynamic objects (scripts move, remove and create map objects) ----------------------

    /// <summary>Recomputes the static blocking of the given tiles from the objects still standing on them.</summary>
    void RefreshBlocking(IEnumerable<Tile> tiles)
    {
        foreach (var t in tiles.Distinct())
        {
            var f = Map.Floor(t.Floor);
            if (f == null || !f.InBounds(t.X, t.Y)) continue;
            bool blocked = f.TerrainBlocked(t.X, t.Y), held = false;
            foreach (var o in Map.Objects)
            {
                if (S.Objects.TryGetValue(o.Index, out var os) && os.Removed) continue;
                if (o.Pos.Floor != t.Floor || !o.BlockedTiles.Contains(t)) continue;
                if (!o.ActiveTiles.Contains(t)) blocked = true;
                if (o.Kind != ObjectKind.Hero && o.MultiTile) held = true;
            }
            f.Blocked[t.Y * f.Width + t.X] = blocked;
            if (t.Y * f.Width + t.X < f.ObjectBlocked.Length) f.ObjectBlocked[t.Y * f.Width + t.X] = held;
        }
    }

    /// <summary>RemoveObject from a script: the object disappears and stops blocking its tiles.</summary>
    public void ScriptRemoveObjectAndUnblock(int index)
    {
        var o = Map.Objects[index];
        var tiles = o.BlockedTiles.ToList();
        RemoveObject(index);
        RefreshBlocking(tiles);
        _activeIndex = null;
        _actionIndex = null;
    }

    /// <summary>SetObjectPosition for non-hero objects: relocates footprint, blocking and entrance index.</summary>
    public void ScriptMoveObject(int index, Tile to)
    {
        var o = Map.Objects[index];
        var oldTiles = o.BlockedTiles.Concat(o.ActiveTiles).ToList();
        var from = o.Pos;
        o.Pos = to;
        RefreshBlocking(oldTiles.Concat(o.BlockedTiles));
        _activeIndex = null;
        _actionIndex = null;
        if (S.Towns.Find(t => t.Object == index) is { } town) town.Pos = to;
        Emit(new HeroMoved(-1 - index, new[] { from, to }, 0));
    }

    /// <summary>Re-applies a saved script placement to the static map (loading a save; no events).</summary>
    public void RestorePlacement(int index, Tile pos, float rot)
    {
        var o = Map.Objects[index];
        var oldTiles = o.BlockedTiles.Concat(o.ActiveTiles).ToList();
        o.Pos = pos;
        o.Rot = rot;
        RefreshBlocking(oldTiles.Concat(o.BlockedTiles));
        _activeIndex = null;
        _actionIndex = null;
    }

    /// <summary>Re-applies blocking for objects removed in the saved state (loading a save).</summary>
    public void RestoreRemovedBlocking()
    {
        var tiles = S.Objects.Where(kv => kv.Value.Removed && kv.Key < Map.Objects.Count)
            .SelectMany(kv => Map.Objects[kv.Key].BlockedTiles).ToList();
        RefreshBlocking(tiles);
        _activeIndex = null;
        _actionIndex = null;
    }

    /// <summary>SetObjectRotation: changes the footprint orientation (quarter turns).</summary>
    public void ScriptRotateObject(int index, float rot)
    {
        var o = Map.Objects[index];
        var oldTiles = o.BlockedTiles.Concat(o.ActiveTiles).ToList();
        o.Rot = rot;
        RefreshBlocking(oldTiles.Concat(o.BlockedTiles));
        _activeIndex = null;
        _actionIndex = null;
    }

    /// <summary>Adds a script-created object (CreateMonster, CreateArtifact…) and rolls its state.</summary>
    public MapObject ScriptAddObject(MapObject o, bool setup = true)
    {
        o.Index = Map.Objects.Count;
        Map.Objects.Add(o);
        if (setup) SetupObject(o);
        RefreshBlocking(o.BlockedTiles);
        _activeIndex = null;
        _actionIndex = null;
        return o;
    }

    /// <summary>SetObjectOwner on a hero: transfers the hero to another player (-1 = neutral not allowed).</summary>
    public void ScriptSetHeroOwner(HeroState h, int player)
    {
        h.Owner = player;
        RevealAround(h);
        Emit(new HeroHired(h.Id, h.DefId, -1));
    }
}
