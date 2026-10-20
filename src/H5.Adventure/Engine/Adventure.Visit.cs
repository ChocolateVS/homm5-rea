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

    /// <summary>
    /// Fortunate Adventurer on a treasure amount (CHero vcall 0x178 = FUN_00b4b540): with skill 0x21 the amount
    /// grows by max(1, rand(5..25) · amount / 100).
    /// </summary>
    int FortunateAdventurer(HeroState h, int amount)
    {
        if (!h.HasPerk(FortunateAdventurerPerk)) return amount;
        return amount + Math.Max(1, S.Rng.Range(5, 25) * amount / 100);
    }

    // Treasure visit 00a82c40 with the rewards of FUN_00a82720 (the amount goes through vcall 0x178, see above).
    void VisitTreasure(HeroState h, PlayerState p, MapObject o, ObjState os)
    {
        switch (o.Shared.Type)
        {
            case "TREASURE_CAMPFIRE":
            {
                int n = FortunateAdventurer(h, os.Amount);
                GiveOne(p, os.ResourceKind, n);
                GiveOne(p, Purse.Gold, 100 * n);
                RemoveObject(o.Index);
                break;
            }
            case "TREASURE_CHEST":
            {
                if (os.ArtifactId != null) { GiveArtifact(h, os.ArtifactId); RemoveObject(o.Index); break; }
                // Gold (amount' + 1) · 500 with amount' after vcall 0x178; experience amount · 500 through the
                // hero's experience modifiers (vcall 0x19c), added unchanged by CTakeChestCmd (00426c60, +0x198).
                int a = os.Variant + 1;
                int gold = (FortunateAdventurer(h, a) + 1) * 500;
                int exp = (int)ExperienceModifiers(h, a * 500L);
                S.Choice = new PendingChoice { Kind = "chest", Hero = h.Id, Object = o.Index, Gold = gold, Experience = exp };
                Emit(new ChoiceOffered("chest", h.Id, o.Index, gold, exp));
                break;
            }
            case "TREASURE_FLOATSAM":
                // amount' 2: 5 wood; 3: 5 wood + 200 gold; 4: 10 wood + 500 gold; anything else nothing.
                switch (FortunateAdventurer(h, os.Variant + 1))
                {
                    case 2: GiveOne(p, Purse.Wood, 5); break;
                    case 3: GiveOne(p, Purse.Wood, 5); GiveOne(p, Purse.Gold, 200); break;
                    case 4: GiveOne(p, Purse.Wood, 10); GiveOne(p, Purse.Gold, 500); break;
                    default: Emit(new ObjectVisited(h.Id, o.Index, "msg:2")); break;   // 00a82c40: nothing in the purse → [2]
                }
                RemoveObject(o.Index);
                break;
            case "TREASURE_SEA_CHEST":
                // 1500 gold unless amount' is 1.
                if (FortunateAdventurer(h, os.Variant + 1) != 1) GiveOne(p, Purse.Gold, 1500);
                else Emit(new ObjectVisited(h.Id, o.Index, "msg:2"));   // 00a82c40: nothing in the purse → [2]
                RemoveObject(o.Index);
                break;
            case "TREASURE_SHIPWRECK":
                if (os.ArtifactId != null) GiveArtifact(h, os.ArtifactId);
                else if (os.Amount > 0) GiveOne(p, Purse.Gold, os.Amount);
                RemoveObject(o.Index);
                break;
            default:
                if (os.ResourceKind >= 0 && os.Amount > 0) GiveOne(p, os.ResourceKind, FortunateAdventurer(h, os.Amount));
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
            // 00b02740 cases 0x32..0x35: once per hero (vcall 0x1f4, else [3]), FUN_00b01980 raises the attributes
            // DefaultStats RaiseAttributesBuildings lists for the building type.
            case "BUILDING_MERCENARY_CAMP":
            case "BUILDING_MARLETTO_TOWER":
            case "BUILDING_STAR_AXIS":
            case "BUILDING_GARDEN_OF_REVELATION":
                OncePerHero(h, o, () => RaiseAttributes(h, type));
                break;
            // Case 0x36: 1000 experience through the hero's modifiers (vcall 0x19c, then +0x198).
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
                // Case 0x6a: visited → [3]; level 10+ (vcall 0x20) or skill 0x29 (Wisdom) in the hero's list (vcall
                // 0x188) → RaiseAttributesBuildings and [2]; otherwise [4] FirstVisitForbidden.
                if (h.VisitedOnce.Contains(o.Index)) { Emit(new Message("already_visited", "Already visited.")); break; }
                if (h.Level < 10 && h.MasteryOf("HERO_SKILL_WISDOM") == Mastery.None && !h.HasPerk("HERO_SKILL_WISDOM"))
                {
                    Emit(new ObjectVisited(h.Id, o.Index, "msg:4"));
                    break;
                }
                OncePerHero(h, o, () => RaiseAttributes(h, type));
                break;

            // Next-battle morale/luck and movement locations (p.261-264).
            case "BUILDING_NOMADS_SHAMAN":
                // 00b02740 case 0x76 → 00af9d30(…, 10): +10 to the hit points percent ([4]) until the next battle.
                BattleBonusVisit(h, o, (4, 10));
                break;
            case "BUILDING_FORTUITOUS_SANCTUARY":
                // Case 0x77: the day counter (world vfunc 0xa8) % 7 picks the bonus (jump table 00b06618).
                BattleBonusVisit(h, o, (S.DayOfWeek - 1) switch
                {
                    0 => new[] { (2, 3) },          // 00af9ba0: attack +3
                    1 => new[] { (1, 1) },          // 00af9a10: morale +1
                    2 => new[] { (3, 3) },          // defence +3
                    3 => new[] { (0, 1) },          // luck +1
                    4 => new[] { (5, 1) },          // 00af9e80: initiative +1
                    5 => new[] { (2, 2), (3, 2) },  // attack +2, defence +2
                    _ => new[] { (6, 1) },          // speed +1
                });
                break;
            case "BUILDING_BUOY": OncePerHero(h, o, () => h.NextBattleMorale += 1, untilBattle: true); break;
            case "BUILDING_FAERIE_RING": OncePerHero(h, o, () => h.NextBattleLuck += 1, untilBattle: true); break;
            case "BUILDING_MERMAIDS": OncePerHero(h, o, () => h.NextBattleLuck += 1, untilBattle: true); break;
            case "BUILDING_FONTAIN_OF_FORTUNE":
                // 00b03035: luck = rng & 3, 0 → −1 (+1 instead with Fortunate Adventurer, skill 0x21).
                OncePerHero(h, o, () =>
                {
                    int r = S.Rng.Next(4);
                    h.NextBattleLuck += r > 0 ? r : h.HasPerk(FortunateAdventurerPerk) ? 1 : -1;
                }, untilBattle: true);
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
                // 00b02740 case 0xf: the hero's stables flag (vcall 0x210 / 0x214, hero +0x18c; cleared by the hero's
                // new-week handler 00b50880) set → [3]. Otherwise set it, movement + VisitStablesMovementBonus now
                // (RPGStats +0x7c), [2]; while it is set the maximum land movement has ForWeekStablesMaxLandMovementBonus
                // (+0x80, 00b4dd40).
                if (h.StablesThisWeek) { Emit(new ObjectVisited(h.Id, o.Index, "msg:3")); break; }
                h.StablesThisWeek = true;
                h.MovePoints += adv.Int("VisitStablesMovementBonus", 600);
                Emit(new ObjectVisited(h.Id, o.Index, "msg:2"));
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
            {
                // Case 0x1c: one drink per hero per day from any well (vcall 0x208 / 0x20c, hero +0x174, cleared by the
                // new-day handler 00b50470): set → [3]; max mana (vcall 0x12c) − mana < 1 → [4]; otherwise mana = max
                // (vcall 0x128) with an ICON_MANA flyoff.
                int max = MaxMana(h);
                if (h.DrankFromWellToday) Emit(new ObjectVisited(h.Id, o.Index, "msg:3"));
                else if (max - h.Mana < 1) Emit(new ObjectVisited(h.Id, o.Index, "msg:4"));
                else { h.Mana = max; h.DrankFromWellToday = true; Emit(new ObjectVisited(h.Id, o.Index, type)); }
                break;
            }
            case "BUILDING_MAGIC_SPRING":
            {
                // Case 0x14: one hero per week per spring (the object's weekly flag +0xe8, cleared by its
                // IAdvMapWeeklyUpdate 009ce1e0): set → [4]; 2 · max mana − mana < 1 → [3]; otherwise the flag, mana =
                // 2 · max (vcall 0x128) with an ICON_MANA flyoff.
                int twice = 2 * MaxMana(h);
                if (os.LastVisitWeek == S.Week) Emit(new ObjectVisited(h.Id, o.Index, "msg:4"));
                else if (twice - h.Mana < 1) Emit(new ObjectVisited(h.Id, o.Index, "msg:3"));
                else { os.LastVisitWeek = S.Week; h.Mana = twice; Emit(new ObjectVisited(h.Id, o.Index, type)); }
                break;
            }

            // Weekly income buildings (per object).
            // 00b02740 cases 9 / 8 / 0x44: a weekly flag on the object ([3] while set); amounts through Fortunate
            // Adventurer (vcall 0x178).
            case "BUILDING_WINDMILL":
                // Resource rand(1..5) (ore, mercury, crystal, sulfur, gems), rand(3..6) of it.
                Weekly(o, os, () => GiveOne(p, S.Rng.Range(1, 5), FortunateAdventurer(h, S.Rng.Range(3, 6))));
                break;
            case "BUILDING_WATER_WHEEL":
                // 500 gold in the first week of the first month (FUN_005d6990 / 0058bce0), else 1000.
                Weekly(o, os, () => GiveOne(p, Purse.Gold, FortunateAdventurer(h, S.Week == 1 ? 500 : 1000)));
                break;
            case "BUILDING_MYSTICAL_GARDEN":
                // rand(1..2): 1 → 500 gold, else 5 gems.
                Weekly(o, os, () =>
                {
                    if (S.Rng.Range(1, 2) == 1) GiveOne(p, Purse.Gold, FortunateAdventurer(h, 500));
                    else GiveOne(p, Purse.Gem, FortunateAdventurer(h, 5));
                });
                break;

            // One-off loot.
            // One-off loot (00b02740 cases 10 / 0xd / 0xe): a looted flag on the object, which stays; once set every
            // visit shows [3]. Rolls on world streams with inclusive bounds (FUN_008e7840).
            case "BUILDING_LEAN_TO":
                if (Looted(h, o, os)) break;
                // Resource 0..5 (wood … gems), 1..4 of it, through Fortunate Adventurer (vcall 0x178); then [2].
                GiveOne(p, S.Rng.Range(0, 5), FortunateAdventurer(h, S.Rng.Range(1, 4)));
                Emit(new ObjectVisited(h.Id, o.Index, "msg:2"));
                break;
            case "BUILDING_WAGON":
            {
                if (Looted(h, o, os)) break;
                // r 1..10: 1 → [3]; 2..5 → resource 0..5, 2..5 of it (vcall 0x178), [2]; 6..10 → an artifact.
                int r = S.Rng.Range(1, 10);
                if (r <= 1) Emit(new ObjectVisited(h.Id, o.Index, "msg:3"));
                else if (r <= 5)
                {
                    GiveOne(p, S.Rng.Range(0, 5), FortunateAdventurer(h, S.Rng.Range(2, 5)));
                    Emit(new ObjectVisited(h.Id, o.Index, "msg:2"));
                }
                // TODO(exe): the artifact's choice (00b03e09 → FUN_00afa990) is not traced; a random minor one is used.
                else if (RandomArtifact("ARTF_CLASS_MINOR") is { } wa) GiveArtifact(h, wa);
                break;
            }
            case "BUILDING_SKELETON":
                if (Looted(h, o, os)) break;
                // r 1..10: up to 8 → [3]; 9..10 → an artifact (FUN_00afa7a0).
                // TODO(exe): the artifact's choice is not traced; a random one is used.
                if (S.Rng.Range(1, 10) <= 8) Emit(new ObjectVisited(h.Id, o.Index, "msg:3"));
                else if (RandomArtifact(null) is { } sa) GiveArtifact(h, sa);
                break;
            case "BUILDING_TOMB_OF_THE_WARRIOR":
            {
                // Case 0x67: morale −3 until the next battle (00af9a10, keyed on the object through vcall 0x1fc) unless
                // already applied since the last battle. Looted (object +0xe9): [3] with the penalty, else [4]. Not
                // looted: the penalty, then an artifact (00afa990 → 00afa7a0) shown with [2]; the tomb stays unlooted
                // only when the hero cannot take it (00afa610 result 1).
                // TODO(exe): which artifact 00afa7a0 draws (world vcall 0x124(2) → 008e7930) is not traced; any one is used.
                bool penalty = h.VisitedSinceBattle.Add(o.Index);
                if (penalty) h.NextBattleMorale -= 3;
                if (os.TimesLooted > 0) { Emit(new ObjectVisited(h.Id, o.Index, penalty ? "msg:3" : "msg:4")); break; }
                os.TimesLooted++;
                if (RandomArtifact(null) is { } ta) { Emit(new ObjectVisited(h.Id, o.Index, "msg:2")); GiveArtifact(h, ta); }
                break;
            }
            case "BUILDING_SIRENS":
                Sirens(h, o);
                break;

            // Exploration.
            case "BUILDING_REDWOORD_OBSERVATORY":
                // Case 0x20: radius 30 around the observatory's own position (object vcall 0xa4 → 0098f4e0), then [2].
                Reveal(p, o.Pos, 30);
                Emit(new ObjectVisited(h.Id, o.Index, "msg:2"));
                break;
            case "BUILDING_HUT_OF_MAGI":
                foreach (var eye in Map.Objects.Where(e => e.Shared.Type == "BUILDING_EYE_OF_MAGI" && e.Shared.Path.Contains("Eye_Of_Magi")))
                    Reveal(p, eye.Pos, 10);   // TODO(exe): reveal radius around each Eye
                Emit(new ObjectVisited(h.Id, o.Index, type));
                break;
            case "BUILDING_LIGHTHOUSE":
                Capture(o, os, p);
                break;
            case DenOfThieves:
            {
                // 00bca4f0: a den not yet the visitor's changes owner (dens owned, player vfunc 0x134 ∓1) and shows
                // messagesFileRef[2]; every visit then opens the thieves' guild (00b04c72: dens owned + 1).
                bool denCaptured = os.Owner != p.Index;
                Capture(o, os, p);
                Emit(new ObjectVisited(h.Id, o.Index, denCaptured ? type + ":captured" : type));
                break;
            }

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
            case "BUILDING_MONOLITH_ONE_WAY_EXIT":
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

    /// <summary>FUN_00b01980: the attribute raises of DefaultStats RaiseAttributesBuildings for a building type.</summary>
    void RaiseAttributes(HeroState h, string type)
    {
        var item = Core.Stats.Node("RaiseAttributesBuildings")?.Elements("Item").FirstOrDefault(i => i.Element("BuildingType")?.Value.Trim() == type);
        foreach (var a in item?.Element("Attributes")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            int v = int.TryParse(a.Element("Value")?.Value, out var n) ? n : 0;
            switch (a.Element("HeroAttribute")?.Value.Trim())
            {
                case "HERO_ATTRIB_OFFENCE": AddStat(h, "Attack", v); break;
                case "HERO_ATTRIB_DEFENCE": AddStat(h, "Defence", v); break;
                case "HERO_ATTRIB_SPELLPOWER": AddStat(h, "SpellPower", v); break;
                case "HERO_ATTRIB_KNOWLEDGE": AddStat(h, "Knowledge", v); break;
            }
        }
    }

    void OncePerHero(HeroState h, MapObject o, Action effect, bool untilBattle = false)
    {
        var set = untilBattle ? h.VisitedSinceBattle : h.VisitedOnce;
        if (!set.Add(o.Index)) { Emit(new Message("already_visited", "Already visited.")); return; }
        effect();
        Emit(new ObjectVisited(h.Id, o.Index, o.Shared.Type));
    }

    /// <summary>
    /// The next-battle bonus buildings (exe 00af9a10 / 00af9ba0 / 00af9d30 / 00af9e80): once per hero until the next battle
    /// (hero vfunc 0x1fc on the object), the fields (index into luck, morale, attack, defence, hit points %, initiative,
    /// speed) are added (vfunc 0x200); each changed field gets a flyoff. A repeat visit shows messagesFileRef[3]
    /// (00ce14d0(3) → 009c90e0).
    /// </summary>
    void BattleBonusVisit(HeroState h, MapObject o, params (int Field, int Amount)[] bonus)
    {
        if (!h.VisitedSinceBattle.Add(o.Index)) { Emit(new ObjectVisited(h.Id, o.Index, "battlebonus:again")); return; }
        foreach (var (field, amount) in bonus)
            switch (field)
            {
                case 0: h.NextBattleLuck += amount; break;
                case 1: h.NextBattleMorale += amount; break;
                case 2: h.NextBattleAttack += amount; break;
                case 3: h.NextBattleDefence += amount; break;
                case 4: h.NextBattleHitPoints += amount; break;
                case 5: h.NextBattleInitiative += amount; break;
                case 6: h.NextBattleSpeed += amount; break;
            }
        Emit(new ObjectVisited(h.Id, o.Index, "battlebonus:" + string.Join(",", bonus.Select(b => $"{b.Field}={b.Amount}"))));
    }

    void Weekly(MapObject o, ObjState os, Action effect)
    {
        if (os.LastVisitWeek == S.Week) { Emit(new Message("already_visited_week", "Come back next week.")); return; }
        os.LastVisitWeek = S.Week;
        effect();
        Emit(new ObjectVisited(-1, o.Index, o.Shared.Type));
    }

    /// <summary>The loot sites' looted flag (object +0xe9): set on the first visit; afterwards the visit shows [3].</summary>
    bool Looted(HeroState h, MapObject o, ObjState os)
    {
        if (os.TimesLooted > 0) { Emit(new ObjectVisited(h.Id, o.Index, "msg:3")); return true; }
        os.TimesLooted++;
        return false;
    }

    void Offer(HeroState h, MapObject o, string kind)
    {
        S.Choice = new PendingChoice { Kind = kind, Hero = h.Id, Object = o.Index };
        Emit(new ChoiceOffered(kind, h.Id, o.Index, 0, 0));
    }

    /// <summary>
    /// Sirens (00b02740 case 0x3d): once per hero (vcall 0x1f4) else [3]. Every stack of more than one creature loses
    /// (count · 30 + 50) / 100 (00b05761, FUN_004bad00); the experience is the creatures lost × the hit points with the
    /// hero's bonuses (FUN_004bc470). None → [3] and the hero is not marked; otherwise the experience through +0x198
    /// (no modifiers), the mark (vcall 0x1f8) and [2].
    /// </summary>
    void Sirens(HeroState h, MapObject o)
    {
        if (h.VisitedOnce.Contains(o.Index)) { Emit(new ObjectVisited(h.Id, o.Index, "msg:3")); return; }
        long exp = 0;
        foreach (var s in h.Army.Live().ToList())
        {
            if (s.Count <= 1) continue;
            int killed = Math.Min(s.Count, (s.Count * 30 + 50) / 100);
            s.Count -= killed;
            exp += (long)killed * CreatureHitPoints(h, s.Creature);
        }
        if (exp < 1) { Emit(new ObjectVisited(h.Id, o.Index, "msg:3")); return; }
        Emit(new ArmyChanged(h.Id, -1));
        GainExperience(h, exp, modifiersApplied: true);
        h.VisitedOnce.Add(o.Index);
        Emit(new ObjectVisited(h.Id, o.Index, "msg:2"));
    }

    /// <summary>
    /// Teleports (exe CAdvMapTeleport visit 00c24dc0 → 00c252f0; exits FUN_00afabe0 / 00afadd0): the exits are the
    /// buildings of the group (building +0x9c, GroupID) of the same type — one-way entrance → one-way exits — less the
    /// entrance itself; one is drawn at random (rng % count). No exit, or the drawn exit's entrance tile not free
    /// (FUN_00ceca20 → 00ce9ed0), shows messagesFileRef[2]; the whirlpool (00c250d0) goes without that check.
    /// A one-way exit shows its [2].
    /// </summary>
    void Teleport(HeroState h, MapObject o)
    {
        if (o.Shared.Type == "BUILDING_MONOLITH_ONE_WAY_EXIT") { Emit(new ObjectVisited(h.Id, o.Index, "msg:2")); return; }
        var group = o.Props.GetValueOrDefault("GroupID", "0");
        var exitType = o.Shared.Type switch
        {
            "BUILDING_MONOLITH_ONE_WAY_ENTRANCE" => "BUILDING_MONOLITH_ONE_WAY_EXIT",
            _ => o.Shared.Type,
        };
        var exits = Map.Objects.Where(e => e.Index != o.Index && !S.Obj(e.Index).Removed && e.Shared.Type == exitType && e.Props.GetValueOrDefault("GroupID", "0") == group)
            .OrderBy(e => e.Index).ToList();
        bool whirlpool = o.Shared.Type == "BUILDING_WHIRLPOOL";
        if (exits.Count == 0)
        {
            if (!whirlpool) Emit(new ObjectVisited(h.Id, o.Index, "msg:2"));
            return;
        }
        var dest = exits[S.Rng.Next(exits.Count)];
        var to = dest.ActiveTiles.FirstOrDefault(dest.Pos);
        // TODO(exe): FUN_00ce9ed0's full test is not decoded; a hero standing on the exit blocks it.
        if (!whirlpool && HeroAt(to, h.Id) != null) { Emit(new ObjectVisited(h.Id, o.Index, "msg:2")); return; }
        if (whirlpool) ApplyWhirlpoolLoss(h);
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
        if (!force && !CanLearnSpell(h, spell)) return false;
        h.Spells.Add(spell);
        Emit(new SpellLearned(h.Id, spell));
        return true;
    }

    bool CanLearnSpell(HeroState h, string spell)
    {
        var def = Core.Spell(spell);
        if (def != null)
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
                if (c.Option == 1) GainExperience(h, ch.Experience, modifiersApplied: true); else GiveOne(p, Purse.Gold, ch.Gold);
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
            case "monster_flee":
                // CFleeFightDialogLogEntry: Pursue! → the fight; Leave them → CMonsterFleeCmd.
                if (c.Accept) { if (!Refused(h, -1, ch.Object)) StartBattle(h, -1, ch.Object, O(ch.Object).Pos, S.Obj(ch.Object).Guards.Select(g => new Stack(g.Creature, g.Count)).ToList(), siege: false); }
                else LetMonstersFlee(h, ch.Object);
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
