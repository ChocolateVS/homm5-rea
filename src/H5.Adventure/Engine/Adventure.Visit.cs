using H5.Adventure.Data;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Effects of visiting adventure-map objects. Numbers come from the fan manual
// "Map Locations" chapter (p.260-270) and DefaultStats.xdb; behaviour that the manual
// does not pin down is marked TODO(exe).
public sealed partial class Adventure
{
    /// <summary>Handles whatever is on the hero's tile (towns, pickups, buildings). Returns false if nothing was there.</summary>
    bool VisitTile(HeroState h, Tile t)
    {
        bool any = false;
        if (TownAt(t) is { } town)
        {
            if (ScriptHooks != null && ScriptHooks.InterceptVisit(h, O(town.Object))) Emit(new ObjectVisited(h.Id, town.Object, "script"));
            else VisitTown(h, town);
            any = true;
        }
        foreach (var o in ObjectsAt(t).ToList())
        {
            if (o.Kind == ObjectKind.Town) continue;
            if (ScriptHooks != null && ScriptHooks.InterceptVisit(h, o))
            {
                Emit(new ObjectVisited(h.Id, o.Index, "script"));
                any = true;
                continue;
            }
            Visit(h, o);
            any = true;
            if (S.Battle != null || S.Choice != null) break;
        }
        return any;
    }

    void Visit(HeroState h, MapObject o)
    {
        var os = S.Obj(o.Index);
        var p = PlayerByIndex(h.Owner)!;
        // Guarded objects (artifacts, mines, banks…) must be fought for first.
        // Creature banks ask first (messagesFileRef[2] "Search": search the place?).
        if (os.Guards.Count > 0 && o.Kind == ObjectKind.Building && AdventureDb.BankFor(o.Shared.Type) != null)
        {
            Offer(h, o, "bank_search");
            return;
        }
        // Troops an enemy left in its flagged building defend it.
        if (IsArmyHolder(o) && os.Owner >= 0 && !Allied(os.Owner, p.Index) && os.Army.Live().Any())
        {
            if (Refused(h, -1, o.Index)) return;
            StartBattle(h, -1, o.Index, h.Pos, os.Army.Live().Select(g => new Stack(g.Creature, g.Count)).ToList(), siege: false);
            return;
        }
        if (os.Guards.Count > 0 && o.Kind is not ObjectKind.Monster)
        {
            if (Refused(h, -1, o.Index)) return;
            StartBattle(h, -1, o.Index, h.Pos, os.Guards.Select(g => new Stack(g.Creature, g.Count)).ToList(), siege: false);
            return;
        }
        switch (o.Kind)
        {
            case ObjectKind.Treasure: VisitTreasure(h, p, o, os); break;
            case ObjectKind.Artifact:
                if (os.SpellId != null) LearnSpell(h, os.SpellId, force: true);
                else if (os.ArtifactId != null) GiveArtifact(h, os.ArtifactId);
                RemoveObject(o.Index);
                break;
            case ObjectKind.Mine:
            case ObjectKind.DwarvenWarren:
            case ObjectKind.AbandonedMine:
            {
                // The owner's visit opens the garrison screen (CGarrisonDialogLogEntry, 005a5d90 case 0x1c) to leave guards.
                bool own = os.Owner == p.Index;
                Capture(o, os, p);
                if (own) Emit(new ObjectVisited(h.Id, o.Index, "garrison"));
                break;
            }
            case ObjectKind.Garrison:
                Capture(o, os, p);
                Emit(new ObjectVisited(h.Id, o.Index, "garrison"));
                break;
            case ObjectKind.Dwelling:
                // exe 005a5d90 case 3 (CHeroVisitDwellingDialogLogEntry): every visit opens the hire screen.
                Capture(o, os, p);
                Emit(new ObjectVisited(h.Id, o.Index, "dwelling"));
                break;
            case ObjectKind.Shipyard:
                Capture(o, os, p);
                VisitShipyard(h, o);
                break;
            case ObjectKind.Shrine:
                // AdvMapShrineShared messagesFileRef: [2] SpellLearned, [3] AlreadyLearned, [4] SpellCantBeLearned.
                Emit(new ObjectVisited(h.Id, o.Index, os.SpellId == null ? "shrine"
                    : h.Spells.Contains(os.SpellId) ? "shrine:known"
                    : LearnSpell(h, os.SpellId, force: false) ? "shrine:learned" : "shrine:cant"));
                break;
            case ObjectKind.Tent:
                // AdvMapTentShared messagesFileRef: keymaster [2] first_visit / [3] second_visit;
                // border guard [2] nopass / [3] pass ("Do you wish to pass at this time?").
                if (o.Shared.Type == "BUILDING_KEYMASTER_TENT")
                    Emit(new ObjectVisited(h.Id, o.Index, p.Keys.Add(o.Shared.Color) ? $"key:{o.Shared.Color}" : $"key_again:{o.Shared.Color}"));
                else if (p.Keys.Contains(o.Shared.Color)) Offer(h, o, "border_guard");
                else Emit(new ObjectVisited(h.Id, o.Index, "border_guard_closed"));
                break;
            case ObjectKind.Prison:
                FreePrisoner(h, o);
                break;
            case ObjectKind.Building:
                VisitBuilding(h, p, o, os);
                break;
            case ObjectKind.SeerHut:
                VisitSeerHut(h, o);
                break;
            case ObjectKind.Sphinx:
                VisitSphinx(h, o);
                break;
            case ObjectKind.HillFort:
            case ObjectKind.Sign:
            case ObjectKind.Cartographer:
                // Interactive or scripted objects: reported to the client/script layer.
                Emit(new ObjectVisited(h.Id, o.Index, o.Shared.Type));
                break;
        }
    }

    /// <summary>Flagged buildings that hold an owner's army (005ff8f0 own-building case).</summary>
    static bool IsArmyHolder(MapObject o) => o.Kind is ObjectKind.Mine or ObjectKind.AbandonedMine or ObjectKind.DwarvenWarren or ObjectKind.Garrison;

    void Capture(MapObject o, ObjState os, PlayerState p)
    {
        if (os.Owner == p.Index) return;
        os.Owner = p.Index;
        Emit(new ObjectCaptured(o.Index, p.Index));
    }

    void RemoveObject(int index)
    {
        S.Obj(index).Removed = true;
        Emit(new ObjectRemoved(index));
    }

    void VisitTreasure(HeroState h, PlayerState p, MapObject o, ObjState os)
    {
        switch (o.Shared.Type)
        {
            case "TREASURE_CAMPFIRE":
                GiveOne(p, os.ResourceKind, os.Amount);
                GiveOne(p, Purse.Gold, 100 * os.Amount);
                RemoveObject(o.Index);
                break;
            case "TREASURE_CHEST":
                if (os.ArtifactId != null) { GiveArtifact(h, os.ArtifactId); RemoveObject(o.Index); break; }
                int[] gold = { 1000, 1500, 2000 }, exp = { 500, 1000, 1500 };
                int v = Math.Clamp(os.Variant, 0, 2);
                S.Choice = new PendingChoice { Kind = "chest", Hero = h.Id, Object = o.Index, Gold = gold[v], Experience = exp[v] };
                Emit(new ChoiceOffered("chest", h.Id, o.Index, gold[v], exp[v]));
                break;
            case "TREASURE_FLOATSAM":
                switch (os.Variant)
                {
                    case 1: GiveOne(p, Purse.Wood, 5); break;
                    case 2: GiveOne(p, Purse.Wood, 5); GiveOne(p, Purse.Gold, 200); break;
                    case 3: GiveOne(p, Purse.Wood, 10); GiveOne(p, Purse.Gold, 500); break;
                }
                RemoveObject(o.Index);
                break;
            case "TREASURE_SEA_CHEST":
                if (os.Variant > 0) GiveOne(p, Purse.Gold, 1500);
                RemoveObject(o.Index);
                break;
            case "TREASURE_SHIPWRECK":
                if (os.ArtifactId != null) GiveArtifact(h, os.ArtifactId);
                else if (os.Amount > 0) GiveOne(p, Purse.Gold, os.Amount);
                RemoveObject(o.Index);
                break;
            default:
                if (os.ResourceKind >= 0 && os.Amount > 0) GiveOne(p, os.ResourceKind, os.Amount);
                RemoveObject(o.Index);
                break;
        }
    }

    // ------------------------------------------------------------------ buildings

    void VisitBuilding(HeroState h, PlayerState p, MapObject o, ObjState os)
    {
        var adv = Db.Stats.Root.Element("adventure");
        string type = o.Shared.Type;
        if (AdventureDb.BankFor(type) is { } bankId) { VisitBank(h, p, o, os, bankId); return; }
        switch (type)
        {
            case "BUILDING_MERCENARY_CAMP": OncePerHero(h, o, () => AddStat(h, "Attack", 1)); break;
            case "BUILDING_MARLETTO_TOWER": OncePerHero(h, o, () => AddStat(h, "Defence", 1)); break;
            case "BUILDING_STAR_AXIS": OncePerHero(h, o, () => AddStat(h, "SpellPower", 1)); break;
            case "BUILDING_GARDEN_OF_REVELATION": OncePerHero(h, o, () => AddStat(h, "Knowledge", 1)); break;
            case "BUILDING_LEARNING_STONE": OncePerHero(h, o, () => GainExperience(h, 1000)); break;
            case "BUILDING_ARENA":
                OncePerHero(h, o, () => Offer(h, o, "attack_or_defence_2"));
                break;
            case "BUILDING_WAR_ACADEMY":
                OncePerHero(h, o, () => Offer(h, o, "attack_or_defence_1"));
                break;
            case "BUILDING_SCHOOL_OF_MAGIC":
                OncePerHero(h, o, () => Offer(h, o, "spellpower_or_knowledge_1"));
                break;
            case "BUILDING_LIBRARY_OF_ENLIGHTENMENT":
                if (h.Level < 10) { Emit(new Message("library_level", "The Library of Enlightenment requires a hero of level 10 or more.")); break; }
                OncePerHero(h, o, () => { AddStat(h, "Knowledge", 2); AddStat(h, "SpellPower", 2); });
                break;

            // Next-battle morale/luck and movement locations (p.261-264).
            case "BUILDING_BUOY": OncePerHero(h, o, () => h.NextBattleMorale += 1, untilBattle: true); break;
            case "BUILDING_FAERIE_RING": OncePerHero(h, o, () => h.NextBattleLuck += 1, untilBattle: true); break;
            case "BUILDING_MERMAIDS": OncePerHero(h, o, () => h.NextBattleLuck += 1, untilBattle: true); break;
            case "BUILDING_FONTAIN_OF_FORTUNE":
                OncePerHero(h, o, () => h.NextBattleLuck += new[] { 3, 2, -1 }[S.Rng.Next(3)], untilBattle: true);
                break;
            case "BUILDING_FONTAIN_OF_YOUTH":
                OncePerHero(h, o, () => { h.NextBattleMorale += 1; h.MovePoints += adv.Int("VisitFountainOfYouthMovementBonus", 400); }, untilBattle: true);
                break;
            case "BUILDING_OASIS":
                OncePerHero(h, o, () => { h.NextBattleMorale += 1; h.MovePoints += adv.Int("VisitOasisMovementBonus", 800); }, untilBattle: true);
                break;
            case "BUILDING_RALLY_FLAG":
                OncePerHero(h, o, () => { h.NextBattleMorale += 1; h.NextBattleLuck += 1; h.MovePoints += adv.Int("VisitRallyFlagMovementBonus", 400); }, untilBattle: true);
                break;
            case "BUILDING_STABLES":
                if (!h.StablesThisWeek) { h.StablesThisWeek = true; h.MovePoints += adv.Int("VisitStablesMovementBonus", 600); }
                Emit(new ObjectVisited(h.Id, o.Index, type));
                break;
            case "BUILDING_TEMPLE":
                OncePerHero(h, o, () => h.NextBattleMorale += S.DayOfWeek == 7 ? 2 : 1, untilBattle: true);
                break;
            case "BUILDING_IDOL_OF_FORTUNE":
                OncePerHero(h, o, () =>
                {
                    int d = S.DayOfWeek;
                    if (d is 1 or 3 or 5 or 7) h.NextBattleLuck += 1;
                    if (d is 2 or 4 or 6 or 7) h.NextBattleMorale += 1;
                }, untilBattle: true);
                break;
            case "BUILDING_LAKE_OF_SCARLET_SWAN":
                OncePerHero(h, o, () => { h.NextBattleLuck += 2; h.MovePoints = 0; }, untilBattle: true);
                break;
            case "BUILDING_SANCTUARY":
                h.MovePoints = 0;
                Emit(new ObjectVisited(h.Id, o.Index, type));
                break;

            // Mana.
            case "BUILDING_MAGIC_WELL":
                if (h.VisitedDay.Add(o.Index)) h.Mana = Math.Max(h.Mana, MaxMana(h));
                Emit(new ObjectVisited(h.Id, o.Index, type));
                break;
            case "BUILDING_MAGIC_SPRING":
                if (h.VisitedWeek.Add(o.Index)) h.Mana = Math.Max(h.Mana, 2 * MaxMana(h));
                Emit(new ObjectVisited(h.Id, o.Index, type));
                break;

            // Weekly income buildings (per object).
            case "BUILDING_WINDMILL":
                Weekly(o, os, () => GiveOne(p, 1 + S.Rng.Next(6), S.Rng.Range(3, 6)));
                break;
            case "BUILDING_WATER_WHEEL":
                Weekly(o, os, () => GiveOne(p, Purse.Gold, S.Week == 1 ? 500 : 1000));
                break;
            case "BUILDING_MYSTICAL_GARDEN":
                // 500 gold or "a few" gems (TODO(exe): exact gem amount; 5 used).
                Weekly(o, os, () => { if (S.Rng.Next(2) == 0) GiveOne(p, Purse.Gold, 500); else GiveOne(p, Purse.Gem, 5); });
                break;

            // One-off loot.
            case "BUILDING_LEAN_TO":
                // TODO(exe): "a few resources"; 1–4 of a random rare resource used.
                GiveOne(p, 1 + S.Rng.Next(5), S.Rng.Range(1, 4));
                RemoveOnce(o, os);
                break;
            case "BUILDING_WAGON":
                if (S.Rng.Next(2) == 0 && RandomArtifact("ARTF_CLASS_MINOR") is { } wa) GiveArtifact(h, wa);
                else GiveOne(p, S.Rng.Next(2) == 0 ? Purse.Wood : Purse.Ore, S.Rng.Range(2, 5));
                RemoveOnce(o, os);
                break;
            case "BUILDING_SKELETON":
                if (S.Rng.Next(2) == 0 && RandomArtifact(null) is { } sa) GiveArtifact(h, sa);
                RemoveOnce(o, os);
                break;
            case "BUILDING_TOMB_OF_THE_WARRIOR":
                OncePerHero(h, o, () =>
                {
                    if (RandomArtifact(null) is { } ta) GiveArtifact(h, ta);
                    h.NextBattleMorale -= 3;
                });
                break;
            case "BUILDING_SIRENS":
                OncePerHero(h, o, () => Sirens(h));
                break;

            // Exploration.
            case "BUILDING_REDWOORD_OBSERVATORY":
                Reveal(p, h.Pos, 30);
                Emit(new ObjectVisited(h.Id, o.Index, type));
                break;
            case "BUILDING_HUT_OF_MAGI":
                foreach (var eye in Map.Objects.Where(e => e.Shared.Type == "BUILDING_EYE_OF_MAGI" && e.Shared.Path.Contains("Eye_Of_Magi")))
                    Reveal(p, eye.Pos, 10);   // TODO(exe): reveal radius around each Eye
                Emit(new ObjectVisited(h.Id, o.Index, type));
                break;
            case "BUILDING_LIGHTHOUSE":
                Capture(o, os, p);
                break;

            case "BUILDING_WITCH_HUT":
                VisitWitchHut(h, p, o, os);
                break;
            case "BUILDING_TREE_OF_KNOWLEDGE":
                VisitTreeOfKnowledge(h, o);
                break;
            case "BUILDING_OBELISK":
                VisitObelisk(h, p, o);
                break;

            // Teleports.
            case "BUILDING_SUBTERRA_GATE":
            case "BUILDING_MONOLITH_ONE_WAY_ENTRANCE":
            case "BUILDING_MONOLITH_TWO_WAY":
            case "BUILDING_WHIRLPOOL":
                Teleport(h, o);
                break;

            default:
                // Markets, taverns, factories, witch huts, Sylanna's ancients, Memory Mentors, altars…:
                // interactive screens driven by the client (TODO: dedicated commands).
                Emit(new ObjectVisited(h.Id, o.Index, type));
                break;
        }
    }

    void OncePerHero(HeroState h, MapObject o, Action effect, bool untilBattle = false)
    {
        var set = untilBattle ? h.VisitedSinceBattle : h.VisitedOnce;
        if (!set.Add(o.Index)) { Emit(new Message("already_visited", "Already visited.")); return; }
        effect();
        Emit(new ObjectVisited(h.Id, o.Index, o.Shared.Type));
    }

    void Weekly(MapObject o, ObjState os, Action effect)
    {
        if (os.LastVisitWeek == S.Week) { Emit(new Message("already_visited_week", "Come back next week.")); return; }
        os.LastVisitWeek = S.Week;
        effect();
        Emit(new ObjectVisited(-1, o.Index, o.Shared.Type));
    }

    void RemoveOnce(MapObject o, ObjState os)
    {
        os.TimesLooted++;
        Emit(new ObjectVisited(-1, o.Index, o.Shared.Type));
        RemoveObject(o.Index);
    }

    void Offer(HeroState h, MapObject o, string kind)
    {
        S.Choice = new PendingChoice { Kind = kind, Hero = h.Id, Object = o.Index };
        Emit(new ChoiceOffered(kind, h.Id, o.Index, 0, 0));
    }

    /// <summary>Sirens kill 30% of the army and grant experience equal to the hit points taken (p.263).</summary>
    void Sirens(HeroState h)
    {
        long hp = 0;
        foreach (var s in h.Army.Live().ToList())
        {
            int killed = (int)(s.Count * 0.3);
            if (Core.Creatures.TryGetValue(s.Creature, out var cd)) hp += (long)killed * cd.Health;
            s.Count -= killed;
        }
        Emit(new ArmyChanged(h.Id, -1));
        GainExperience(h, hp);
    }

    void Teleport(HeroState h, MapObject o)
    {
        var group = o.Props.GetValueOrDefault("GroupID", "0");
        var exitType = o.Shared.Type switch
        {
            "BUILDING_MONOLITH_ONE_WAY_ENTRANCE" => "BUILDING_MONOLITH_ONE_WAY_EXIT",
            _ => o.Shared.Type,
        };
        var exits = Map.Objects.Where(e => e.Index != o.Index && e.Shared.Type == exitType && e.Props.GetValueOrDefault("GroupID", "0") == group)
            .OrderBy(e => e.Index).ToList();
        if (exits.Count == 0) { Emit(new Message("teleport_none", "Nothing happens.")); return; }
        var dest = exits[S.Rng.Next(exits.Count)];
        var to = dest.ActiveTiles.FirstOrDefault(dest.Pos);
        if (o.Shared.Type == "BUILDING_WHIRLPOOL") ApplyWhirlpoolLoss(h);
        h.Pos = to;
        SyncBoat(h);
        Emit(new HeroMoved(h.Id, new[] { o.ActiveTiles.FirstOrDefault(o.Pos), to }, h.MovePoints));
        RevealAround(h);
    }

    void FreePrisoner(HeroState h, MapObject o)
    {
        // AdvMapPrisonShared messagesFileRef: [2] PrisonEmpty, [3] PrisonCantHire, [4] (named NoExit) the freeing text.
        if (o.Hero == null) { Emit(new ObjectVisited(h.Id, o.Index, "prison:empty")); return; }
        if (S.Heroes.Count(x => x.Alive && x.Owner == h.Owner) >= 8) { Emit(new ObjectVisited(h.Id, o.Index, "prison:full")); return; }
        var freed = HeroFromInstance(o.Hero, h.Owner, h.Pos);
        // The freed hero appears next to the prison (TODO(exe): exact placement).
        freed.Pos = FreeTileNear(h.Pos) ?? h.Pos;
        freed.MaxMovePoints = ComputeMaxMovePoints(freed);
        S.Heroes.Add(freed);
        S.HeroPool.Remove(freed.DefId);
        Emit(new ObjectVisited(h.Id, o.Index, "prison:freed"));
        RemoveObject(o.Index);
        Emit(new HeroHired(freed.Id, freed.DefId, -1));
    }

    Tile? FreeTileNear(Tile t)
    {
        foreach (var (dx, dy) in Dirs)
        {
            var n = t.Offset(dx, dy);
            var f = Map.Floor(n.Floor);
            if (f != null && f.InBounds(n.X, n.Y) && !f.IsBlocked(n.X, n.Y) && !f.IsWater(n.X, n.Y) && HeroAt(n) == null && !ObjectsAt(n).Any())
                return n;
        }
        return null;
    }

    // ------------------------------------------------------------------ banks

    void VisitBank(HeroState h, PlayerState p, MapObject o, ObjState os, string bankId)
    {
        var bank = Db.Banks[bankId];
        if (os.Guards.Count == 0)
        {
            if (os.RechargeDay > S.Day || (bank.RechargesCount == 0 && os.TimesLooted > 0))
            {
                // Looting an empty site costs morale/luck (BankXxx/Luck|MoraleLossOnEmpty).
                h.NextBattleMorale -= bank.MoraleLossOnEmpty;
                h.NextBattleLuck -= bank.LuckLossOnEmpty;
                // messagesFileRef[3] "SearchedEmpty".
                Emit(new ObjectVisited(h.Id, o.Index, "bank:empty"));
                return;
            }
            RollBank(o, bankId);
            if (os.Guards.Count > 0) Offer(h, o, "bank_search");
        }
    }

    /// <summary>Reward after defeating a bank's guards: money and artifacts/spells between RewardMin and RewardMax.</summary>
    void LootBank(HeroState h, MapObject o, ObjState os)
    {
        if (AdventureDb.BankFor(o.Shared.Type) is not { } bankId || !Db.Banks.TryGetValue(bankId, out var bank) || os.Variant < 0) return;
        var v = bank.Variants[Math.Min(os.Variant, bank.Variants.Count - 1)];
        var p = PlayerByIndex(h.Owner)!;
        var min = new Purse(v.Min.Money).R;
        var max = new Purse(v.Max.Money).R;
        for (int i = 0; i < 7; i++)
        {
            int a = S.Rng.Range(min[i], Math.Max(min[i], max[i]));
            if (a > 0) GiveOne(p, i, a);
        }
        void Arts(int lo, int hi, string cls)
        {
            int n = S.Rng.Range(lo, Math.Max(lo, hi));
            for (int i = 0; i < n; i++) if (RandomArtifact(cls) is { } a) GiveArtifact(h, a);
        }
        Arts(v.Min.Minor, v.Max.Minor, "ARTF_CLASS_MINOR");
        Arts(v.Min.Major, v.Max.Major, "ARTF_CLASS_MAJOR");
        Arts(v.Min.Relic, v.Max.Relic, "ARTF_CLASS_RELIC");
        int spells = S.Rng.Range(v.Min.Spells, Math.Max(v.Min.Spells, v.Max.Spells));
        for (int i = 0; i < spells; i++)
            if (RandomSpell(null, S.Rng.Range(Math.Max(1, v.Min.SpellLevel), Math.Max(v.Min.SpellLevel, v.Max.SpellLevel))) is { } sp)
                LearnSpell(h, sp, force: true);
        os.TimesLooted++;
        os.RechargeDay = bank.DaysForRecharge > 0 && bank.RechargesCount != 0 ? S.Day + bank.DaysForRecharge : int.MaxValue;
    }

    // ------------------------------------------------------------------ hero gains

    void AddStat(HeroState h, string stat, int delta)
    {
        switch (stat)
        {
            case "Attack": h.Attack += delta; break;
            case "Defence": h.Defence += delta; break;
            case "SpellPower": h.SpellPower += delta; break;
            case "Knowledge": h.Knowledge += delta; break;
        }
        Emit(new HeroStatChanged(h.Id, stat, delta));
    }

    void GiveArtifact(HeroState h, string id)
    {
        h.Artifacts.Add(id);
        Emit(new ArtifactGained(h.Id, id));
    }

    /// <summary>
    /// Learns a spell. Shrines and guilds require the right school mastery for higher circles
    /// (level 3: Basic, 4: Advanced, 5: Expert); Barbarians cannot learn spells.
    /// TODO(exe): confirm the mastery thresholds and Wisdom-like rules for TotE.
    /// </summary>
    bool LearnSpell(HeroState h, string spell, bool force)
    {
        if (h.Spells.Contains(spell)) return false;
        var def = Core.Spell(spell);
        if (!force && def != null)
        {
            if (h.Class == "HERO_CLASS_BARBARIAN" && def.School != "MAGIC_SCHOOL_WARCRIES") return false;
            var skill = def.School switch
            {
                "MAGIC_SCHOOL_DESTRUCTIVE" => "HERO_SKILL_DESTRUCTIVE_MAGIC",
                "MAGIC_SCHOOL_DARK" => "HERO_SKILL_DARK_MAGIC",
                "MAGIC_SCHOOL_LIGHT" => "HERO_SKILL_LIGHT_MAGIC",
                "MAGIC_SCHOOL_SUMMONING" => "HERO_SKILL_SUMMONING_MAGIC",
                _ => null,
            };
            int need = def.Level <= 2 ? 0 : def.Level - 2;
            if (skill != null && (int)h.MasteryOf(skill) < need) return false;
        }
        h.Spells.Add(spell);
        Emit(new SpellLearned(h.Id, spell));
        return true;
    }

    string? DoAnswer(AnswerChoiceCmd c)
    {
        var ch = S.Choice!;
        S.Choice = null;
        var h = S.Hero(ch.Hero);
        if (h == null) return null;
        var p = PlayerByIndex(h.Owner)!;
        switch (ch.Kind)
        {
            case "chest":
                if (c.Option == 1) GainExperience(h, ch.Experience); else GiveOne(p, Purse.Gold, ch.Gold);
                RemoveObject(ch.Object);
                break;
            case "attack_or_defence_2": AddStat(h, c.Option == 1 ? "Defence" : "Attack", 2); break;
            case "attack_or_defence_1": AddStat(h, c.Option == 1 ? "Defence" : "Attack", 1); break;
            case "spellpower_or_knowledge_1": AddStat(h, c.Option == 1 ? "Knowledge" : "SpellPower", 1); break;
            case "shipyard":
                if (c.Accept)
                {
                    var err = BuildShipAt(O(ch.Object), p);
                    if (err == "not enough resources") Emit(new Message("shipyard_no_money", "You do not have enough gold and wood to build another boat."));
                }
                break;
            case "bank_search":
                if (c.Accept && S.Obj(ch.Object).Guards.Count > 0 && !Refused(h, -1, ch.Object))
                    StartBattle(h, -1, ch.Object, h.Pos, S.Obj(ch.Object).Guards.Select(g => new Stack(g.Creature, g.Count)).ToList(), siege: false);
                break;
            case "border_guard":
                if (c.Accept) { RemoveObject(ch.Object); Emit(new ObjectVisited(h.Id, ch.Object, "border_guard_open")); }
                break;
            case "join_free":
                if (c.Accept) JoinArmy(h, ch.Creatures);
                RemoveObject(ch.Object);
                break;
            case "join_paid":
                if (c.Accept && p.Purse[Purse.Gold] >= ch.Gold)
                {
                    GiveOne(p, Purse.Gold, -ch.Gold);
                    JoinArmy(h, ch.Creatures);
                    RemoveObject(ch.Object);
                }
                else if (!Refused(h, -1, ch.Object))
                    StartBattle(h, -1, ch.Object, O(ch.Object).Pos, S.Obj(ch.Object).Guards.Select(g => new Stack(g.Creature, g.Count)).ToList(), siege: false);
                break;
        }
        return null;
    }

    void JoinArmy(HeroState h, List<Stack> creatures)
    {
        foreach (var s in creatures)
            if (!h.Army.TryAdd(s.Creature, s.Count)) break;
        Emit(new ArmyChanged(h.Id, -1));
    }
}
