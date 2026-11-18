using System.Text;
using Godot;
using H5.Combat;
using H5.Core.Data;

namespace H5Reborn.Combat;

/// <summary>
/// UI end-to-end check of the active abilities and combat spells (dev, `--combat-e2e=SET[,SET…]`): per case a local duel
/// is set up (both sides local, every other turn passed with Defend / Wait), the ability or spell is armed through the HUD
/// button and the 3D spellbook with synthetic mouse clicks, the target is hovered and clicked through the normal input
/// path, and the result is checked: the click submits the planned command, the engine accepts it, the played events
/// equal a dry run of the same command on a copy of the battle, something happened, and playback finishes (no freeze).
/// The targeting scan compares, cell by cell, what the cursor accepts with what the engine accepts.
/// SET = abilities | heroabilities | spells | creaturespells | all; `--combat-e2e-only=ID,…`, `--combat-e2e-range=FROM,COUNT`,
/// `--combat-e2e-out=DIR` (results.tsv + before/after screenshots). Results also go to stdout as `[E2E] RESULT` lines.
/// </summary>
public partial class CombatE2E : Node
{
    static string? _sets;
    static HashSet<string>? _only;
    static (int From, int Count)? _range;
    static string _out = "user://e2e";

    public static bool HandleArg(string arg)
    {
        if (arg.StartsWith("--combat-e2e="))
        {
            _sets = arg["--combat-e2e=".Length..];
            Callable.From(() => ((SceneTree)Engine.GetMainLoop()).Root.AddChild(new CombatE2E())).CallDeferred();
            return true;
        }
        if (arg.StartsWith("--combat-e2e-only=")) { _only = arg["--combat-e2e-only=".Length..].Split(',').ToHashSet(); return true; }
        if (arg.StartsWith("--combat-e2e-range="))
        {
            var p = arg["--combat-e2e-range=".Length..].Split(',');
            _range = (int.Parse(p[0]), int.Parse(p[1]));
            return true;
        }
        if (arg.StartsWith("--combat-e2e-out=")) { _out = arg["--combat-e2e-out=".Length..]; return true; }
        return false;
    }

    /// <summary>One check: an ability or spell, its user and the armies around it.</summary>
    sealed class Case
    {
        public string Kind = "";          // ability | heroability | spell | creaturespell
        public string Id = "";            // ability / spell id
        public string? Actor;             // creature that acts (null: the hero)
        public string Name => Kind + ":" + Id + (Actor != null && Kind != "ability" ? "@" + Actor : "");
        public List<(string C, int N)> Mine = new(), Theirs = new();
        public string? Corpse;            // a dead friendly stack of this creature
        public string? EnemyCorpse;
        public int CorpseCount = 10;
        public List<string> Perks = new();
        public List<string> Spells = new();
        public List<SkillEntry> Skills = new();
        public string? Town;              // hero preset town
        public bool EnemyBallista;
        public bool FriendAdj, EnemyAdj, Tent, EnemySummoned, FavoriteEnemy, EnemyHurt, FriendHurt;
        public string Prefer = "enemy";
    }

    static readonly HashSet<string> HeroAbilityPerks = new()
    {
        Sk.DivineGuidance, Sk.RetaliationStrike, Sk.Benediction, Sk.MarkOfDamned, Sk.MarkOfNecromancer, Sk.BansheeHowl, Sk.RainOfArrows,
        Sk.PowerfulBlow, Sk.ConsumeCorpse, "HERO_SKILL_COUNTERSPELL", Sk.MarkOfWizard, Sk.Banish, Sk.ImbueArrow, "HERO_SKILL_MELT_ARTIFACT",
    };

    readonly List<string> _rows = new();
    int _pass, _fail, _skip, _warn;

    public override void _Ready() => _ = RunAll();

    async Task RunAll()
    {
        await Frames(5);
        var cases = Build();
        if (_only != null) cases = cases.Where(c => _only.Contains(c.Id) || _only.Contains(c.Name)).ToList();
        if (_range is { } r) cases = cases.Skip(r.From).Take(r.Count).ToList();
        DirAccess.MakeDirRecursiveAbsolute(_out);
        var vp = GetViewport();
        GD.Print($"[E2E] {cases.Count} cases; viewport {vp.GetVisibleRect().Size}, window {DisplayServer.WindowGetSize()}, final {vp.GetFinalTransform()}");
        int i = 0;
        foreach (var c in cases)
        {
            i++;
            string tag = $"{(_range?.From ?? 0) + i:000}_{Safe(c.Name)}";
            GD.Print($"[E2E] BEGIN {c.Name}");
            (string status, string detail) res;
            try { res = await RunCase(c, tag); }
            catch (Exception e) { res = ("FAIL", "runner exception: " + e.Message.Replace('\n', ' ')); GD.PushError(e.ToString()); }
            CombatScreen.DevSubmitted = null;
            CombatScreen.DevEvent = null;
            CombatLauncher.FakeMouse = null;
            switch (res.status) { case "PASS": _pass++; break; case "WARN": _warn++; break; case "SKIP": _skip++; break; default: _fail++; break; }
            var row = $"{c.Name}\t{c.Actor ?? "hero"}\t{res.status}\t{res.detail.Replace('\t', ' ')}\t{tag}";
            _rows.Add(row);
            GD.Print($"[E2E] RESULT {row}");
            using (var f = Godot.FileAccess.Open(_out + "/results.tsv", Godot.FileAccess.ModeFlags.Write))
                f.StoreString(string.Join("\n", _rows) + "\n");
        }
        GD.Print($"[E2E] DONE pass {_pass} warn {_warn} fail {_fail} skip {_skip}");
        GetTree().Quit();
    }

    static string Safe(string s) => new(s.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '-').ToArray());

    // ------------------------------------------------------------------ cases

    static readonly string[] CreatureAbilities =
    {
        Ab.LayHands, "ABILITY_SEDUCE", Ab.ResurrectAllies, Ab.Dash, Ab.Explosion, Ab.HarmTouch, Ab.DeathWail, Ab.CallLightning,
        Ab.RageOfForest, Ab.BerserkerRage, Ab.OrderOfChief, Ab.Repair, Ab.ManaFeed, Ab.ScatterShot, Ab.Invisibility, Ab.Leap,
        Ab.SummonOther, Ab.BalorSummoning, Ab.Flamestrike, Ab.StormBolt, Ab.CallStorm, Ab.HarpoonStrike, Ab.Sabotage,
        Ab.SwallowGoblin, Ab.SacrificeGoblin, Ab.LuckGambler, Ab.RandomCaster, Ab.RandomCasterBless, Ab.Incinerate,
        Ab.AmmoSteal, Ab.SyphonMana, Ab.Scavenger, Ab.GoblinThrower, Ab.PowerFeed, Ab.SetSnares, Ab.DefileMagic,
        Ab.TreeantUnion, Ab.PackDive, Ab.BattleDive,
    };

    List<Case> Build()
    {
        var db = Game.Instance.Db;
        var sets = (_sets ?? "all").Split(',').ToHashSet();
        bool all = sets.Contains("all");
        var list = new List<Case>();
        Case Base(string kind, string id, string? actor, int n = 10)
        {
            var c = new Case { Kind = kind, Id = id, Actor = actor };
            if (actor != null) c.Mine.Add((actor, n));
            c.Mine.Add(("CREATURE_PEASANT", 40));
            c.Theirs.Add(("CREATURE_FOOTMAN", 20));
            c.Theirs.Add(("CREATURE_ARCHER", 20));
            return c;
        }
        if (all || sets.Contains("abilities"))
        {
            foreach (var id in CreatureAbilities)
            {
                var cr = db.Creatures.Values.Where(d => d.Abilities.Contains(id)).OrderBy(d => d.Upgrade).ThenBy(d => d.Id).FirstOrDefault();
                if (cr == null) { list.Add(new Case { Kind = "ability", Id = id }); continue; }
                var c = Base("ability", id, cr.Id, Math.Max(2, 60 / Math.Max(1, cr.Tier * cr.Tier)));
                c.Town = cr.Town;
                switch (id)
                {
                    case Ab.LayHands: case Ab.OrderOfChief: case Ab.TreeantUnion: case Ab.Repair:
                        c.Prefer = "friend"; c.FriendHurt = true; break;
                    case Ab.ResurrectAllies: case Ab.Scavenger: case Ab.BalorSummoning:
                        c.Corpse = "CREATURE_PEASANT"; c.Prefer = "corpse"; break;
                    case Ab.SwallowGoblin: case Ab.SacrificeGoblin:
                        c.Mine[1] = ("CREATURE_GOBLIN", 40); c.FriendAdj = true; c.Prefer = "friend"; break;
                    case Ab.GoblinThrower:
                        c.Mine[1] = ("CREATURE_GOBLIN", 40); c.FriendAdj = true; break;
                    case Ab.Incinerate: case "ABILITY_SEDUCE": case Ab.HarmTouch: c.EnemyAdj = true; break;
                    case Ab.AmmoSteal: c.Theirs.Reverse(); c.EnemyAdj = true; break;
                    case Ab.Sabotage: c.EnemyBallista = true; c.Prefer = "machine"; break;
                    case Ab.DefileMagic:
                        if (db.Creatures.Values.Where(d => !d.Upgrade && d.KnownSpells.Any(k => db.Spell(k.Spell)?.IsCombatSchool == true)).OrderBy(d => d.Id).FirstOrDefault() is { } caster)
                            c.Theirs[0] = (caster.Id, 10);
                        break;
                    case Ab.SummonOther: c.Prefer = "friend"; break;
                    case Ab.LuckGambler: case Ab.RandomCasterBless: c.Prefer = "enemy"; break;
                }
                if (id == Ab.Repair && db.Creatures.Values.Where(d => d.Abilities.Contains(Ab.Mechanical) && !d.Id.StartsWith("WAR_MACHINE")).OrderBy(d => d.Id).FirstOrDefault() is { } mech)
                    c.Mine[1] = (mech.Id, 10);
                if (id == Ab.TreeantUnion) c.Mine[1] = ("CREATURE_TREANT", 3);
                if (id == Ab.BalorSummoning && db.Creatures.Values.Where(d => d.Abilities.Contains(Ab.Demonic) && d.Tier == 1).OrderBy(d => d.Id).FirstOrDefault() is { } imp)
                { c.Corpse = imp.Id; c.CorpseCount = 100; }
                if (id == Ab.OrderOfChief) c.Mine[1] = ("CREATURE_GOBLIN", 40);
                list.Add(c);
            }
            // War machine abilities: the First Aid Tent (and the Plague Tent perk).
            var tent = Base("ability", "FIRST_AID", "WAR_MACHINE_FIRST_AID_TENT");
            tent.Mine.RemoveAt(0); tent.Tent = true; tent.Prefer = "friend"; tent.FriendHurt = true; tent.Perks.Add(Sk.FirstAid);
            list.Add(tent);
            var plague = Base("ability", "PLAGUE_TENT", "WAR_MACHINE_FIRST_AID_TENT");
            plague.Mine.RemoveAt(0); plague.Tent = true; plague.Perks.Add(Sk.FirstAid); plague.Perks.Add(Sk.PlagueTent);
            list.Add(plague);
            // Gating: an Inferno tier-1 stack with a Gating hero.
            var gate = Base("ability", Ab.Gating, "CREATURE_FAMILIAR", 20);
            gate.Town = "TOWN_INFERNO"; gate.Skills.Add(new SkillEntry(Sk.Gating, Mastery.Expert)); gate.Prefer = "cell";
            list.Add(gate);
        }
        if (all || sets.Contains("heroabilities"))
        {
            (string Id, string Perk, string Town)[] hero =
            {
                ("DIVINE_GUIDANCE", Sk.DivineGuidance, "TOWN_HEAVEN"), ("RETALIATION_STRIKE", Sk.RetaliationStrike, "TOWN_HEAVEN"),
                ("BENEDICTION", Sk.Benediction, "TOWN_HEAVEN"), ("MARK_OF_DAMNED", Sk.MarkOfDamned, "TOWN_INFERNO"),
                ("MARK_OF_NECROMANCER", Sk.MarkOfNecromancer, "TOWN_NECROMANCY"), ("BANSHEE_HOWL", Sk.BansheeHowl, "TOWN_NECROMANCY"),
                ("RAIN_OF_ARROWS", Sk.RainOfArrows, "TOWN_PRESERVE"), ("POWERFUL_BLOW", Sk.PowerfulBlow, "TOWN_STRONGHOLD"),
                ("CONSUME_CORPSE", Sk.ConsumeCorpse, "TOWN_INFERNO"), ("COUNTERSPELL", "HERO_SKILL_COUNTERSPELL", "TOWN_ACADEMY"),
                ("MARK_OF_WIZARD", Sk.MarkOfWizard, "TOWN_ACADEMY"), ("BANISH", Sk.Banish, "TOWN_ACADEMY"),
                ("IMBUE_ARROW:SPELL_MAGIC_ARROW", Sk.ImbueArrow, "TOWN_PRESERVE"),
            };
            foreach (var (id, perk, town) in hero)
            {
                var c = Base("heroability", id, null);
                c.Mine.Insert(0, ("CREATURE_FOOTMAN", 10));
                c.Perks.Add(perk); c.Town = town;
                if (id is "DIVINE_GUIDANCE" or "RETALIATION_STRIKE" or "POWERFUL_BLOW") c.Prefer = "friend";
                if (id == "MARK_OF_NECROMANCER") c.Prefer = "friend";
                if (id == "CONSUME_CORPSE") { c.Corpse = "CREATURE_PEASANT"; c.Prefer = "corpse"; }
                if (id == "BANISH") c.EnemySummoned = true;
                if (id == "RAIN_OF_ARROWS") c.FavoriteEnemy = true;
                if (id == "COUNTERSPELL") c.Spells.Add("SPELL_MAGIC_ARROW");
                if (id.StartsWith("IMBUE_ARROW")) c.Spells.Add("SPELL_MAGIC_ARROW");
                list.Add(c);
            }
        }
        if (all || sets.Contains("spells"))
        {
            foreach (var sp in db.Spells.Values.Where(s => s.IsCombatSchool).OrderBy(s => s.School).ThenBy(s => s.Level).ThenBy(s => s.Id))
            {
                var c = Base("spell", sp.Id, null);
                c.Mine.Insert(0, ("CREATURE_FOOTMAN", 10));
                c.Spells.Add(sp.Id);
                c.Town = sp.School switch { "MAGIC_SCHOOL_RUNIC" => "TOWN_DWARF", "MAGIC_SCHOOL_WARCRIES" => "TOWN_STRONGHOLD", _ => null };
                c.Prefer = sp.Target is "TARGET_FRIENDLY" or "TARGET_ALLY" ? "friend" : sp.CanSelectDead ? "corpse" : "enemy";
                if (sp.CanSelectDead) c.Corpse = "CREATURE_FOOTMAN";
                if (sp.School == "MAGIC_SCHOOL_RUNIC") c.Skills.Add(new SkillEntry(Sk.Runelore, Mastery.Expert));
                c.FriendHurt = true; c.EnemyHurt = true;
                if (sp.Id.Contains("UNSUMMON") || sp.Id.Contains("BANISH")) c.EnemySummoned = true;
                list.Add(c);
            }
        }
        if (all || sets.Contains("creaturespells"))
        {
            foreach (var cr in db.Creatures.Values.Where(d => d.KnownSpells.Count > 0).OrderBy(d => d.Id))
                foreach (var (spell, _) in cr.KnownSpells)
                {
                    var sp = db.Spell(spell);
                    if (sp == null || sp.School == "MAGIC_SCHOOL_SPECIAL") continue;   // ability entries: the ability cases
                    var c = Base("creaturespell", spell, cr.Id, Math.Max(2, 60 / Math.Max(1, cr.Tier * cr.Tier)));
                    c.Town = cr.Town;
                    c.Prefer = sp.Target is "TARGET_FRIENDLY" or "TARGET_ALLY" ? "friend" : sp.CanSelectDead ? "corpse" : "enemy";
                    if (sp.CanSelectDead) c.Corpse = "CREATURE_PEASANT";
                    c.FriendHurt = true; c.EnemyHurt = true;
                    list.Add(c);
                }
        }
        return list;
    }

    // ------------------------------------------------------------------ one case

    CombatScreen? _screen;

    async Task<(string, string)> RunCase(Case c, string tag)
    {
        var db = Game.Instance.Db;
        if (c.Actor == null && c.Kind == "ability") return ("SKIP", "no creature has this ability in the data");
        // Set-up: duel preset heroes (models, classes), the case's armies, spells and perks.
        int presetA = PresetFor(c.Town), presetB = PresetFor(null, avoid: presetA);
        ulong seed = 12345;
        var (ha, hb) = H5.Adventure.Engine.Adventure.DuelHeroes(H5Reborn.Adventure.AdventureSession.SharedDb, presetA, presetB, seed);
        ha.Army = c.Mine.Select(m => new ArmySlot(m.C, m.N)).ToList();
        if (c.Corpse != null) ha.Army.Add(new ArmySlot(c.Corpse, c.CorpseCount));
        hb.Army = c.Theirs.Select(m => new ArmySlot(m.C, m.N)).ToList();
        ha.Ballista = ha.AmmoCart = false; ha.FirstAidTent = c.Tent;
        hb.Ballista = c.EnemyBallista; hb.AmmoCart = hb.FirstAidTent = false;
        var st = BattleSetup.Create(db, ArmySetup.FromHero(ha), ArmySetup.FromHero(hb), seed, skipTactics: true);
        var h0 = st.Heroes[0];
        h0.Book = null;
        h0.Spells = c.Spells.ToList();
        h0.Wands.Clear();
        // Only the case's own hero ability: the others would share the Special button.
        h0.Perks.RemoveAll(p => HeroAbilityPerks.Contains(p));
        if (c.Kind != "ability" || c.Id != Ab.Gating) h0.Skills.RemoveAll(s => s.SkillId == Sk.Gating);
        foreach (var p in c.Perks) if (!h0.Perks.Contains(p)) h0.Perks.Add(p);
        foreach (var sk in c.Skills) { h0.Skills.RemoveAll(s => s.SkillId == sk.SkillId); h0.Skills.Add(sk); }
        h0.Mana = h0.MaxMana = 999;
        if (c.FavoriteEnemy) foreach (var t in c.Theirs) h0.FavoriteEnemies.Add(t.C);
        var h1 = st.Heroes[1];
        h1.Book = null; h1.Spells.Clear(); h1.Wands.Clear();
        Unit? Mine(string id) => st.Units.FirstOrDefault(u => u.Side == 0 && u.CreatureId == id && u.Alive);
        var actor = c.Actor != null ? Mine(c.Actor) : null;
        if (c.Actor != null && actor == null) return ("SKIP", $"set-up: {c.Actor} not on the field");
        if (c.Corpse != null && st.Units.LastOrDefault(u => u.Side == 0 && u.CreatureId == c.Corpse) is { } corpse) corpse.Count = 0;
        var friend = st.Units.FirstOrDefault(u => u.Side == 0 && u.Alive && u.Kind == UnitKind.Creature && u != actor);
        var enemy = st.Units.FirstOrDefault(u => u.Side == 1 && u.Alive && u.Kind == UnitKind.Creature);
        if (c.FriendAdj && actor != null && friend != null) MoveNextTo(st, friend, actor);
        if (c.EnemyAdj && actor != null && enemy != null) MoveNextTo(st, enemy, actor);
        if (c.FriendHurt && friend != null && friend.Count > 2) { friend.Count = friend.Count * 2 / 3; }
        if (c.EnemyHurt && enemy != null && enemy.Count > 2) { enemy.Count = enemy.Count * 2 / 3; }
        if (c.EnemySummoned && enemy != null) enemy.Summoned = true;

        var session = new LocalCombatSession(db, st, presetA, presetB, seed, new[] { 0, 1 });
        CombatScreen.Open(session);
        await Frames(3);
        _screen = GetTree().CurrentScene as CombatScreen;
        if (_screen == null) return ("FAIL", "combat screen did not open");
        await Frames(20);
        var b = session.Battle;

        // Pass turns until the actor acts.
        bool Ours(LegalActions la) => la.Side == 0 && (c.Actor == null ? la.IsHero : !la.IsHero && b.State.Unit(la.Actor)?.CreatureId == c.Actor);
        LegalActions? lact = null;
        for (int pass = 0; pass < 80; pass++)
        {
            if (!await Idle(20000)) return ("FAIL", "freeze before the actor's turn (playback did not finish)");
            if (session.Ended) return ("SKIP", "set-up: the battle ended before the actor's turn");
            lact = b.GetLegalActions();
            if (lact == null) { await Frames(1); continue; }
            if (Ours(lact)) break;
            var passCmd = PassCmd(b, lact);
            _screen.DevSubmitPass(passCmd);
            if (session.LastRejection != null) _screen.DevSubmitPass(new WaitCmd(lact.Actor) { Side = lact.Side });
            lact = null;
            await Frames(2);
        }
        if (lact == null) return ("SKIP", "set-up: the actor never got a turn");

        // Engine side: is the ability / spell offered at all?
        bool isSpell = c.Kind is "spell" or "creaturespell";
        var abOpt = isSpell ? null : lact.Abilities.Find(o => o.Ability == c.Id);
        var spOpt = isSpell ? lact.Spells.Find(o => o.Spell == c.Id) : null;
        if (abOpt == null && spOpt == null)
            return ("SKIP", "engine does not offer it in this set-up; offered: "
                + string.Join(" ", isSpell ? lact.Spells.Select(s => s.Spell) : lact.Abilities.Select(a => a.Ability)));

        string bookNote = "";
        var submitted = new List<BattleCommand>();
        var events = new List<BattleEvent>();
        CombatScreen.DevSubmitted = cmd => submitted.Add(cmd);
        CombatScreen.DevEvent = e => events.Add(e);

        // Arm it through the HUD.
        var hud = _screen.Hud;
        if (!isSpell)
        {
            if (hud.Screen.Find("OnJog-Special") is not { } sbtn || !sbtn.IsVisibleInTree())
                return ("FAIL", "the Special ability button is not shown" + (hud.Screen.Find("OnJog-Spellbook")?.IsVisibleInTree() == true ? " (Spellbook shown)" : ""));
            if (lact.Abilities[0].Ability != c.Id)
                return ("FAIL", $"several abilities ({string.Join(" ", lact.Abilities.Select(a => a.Ability))}): the Special button arms only the first");
            await Click(sbtn.GetGlobalRect().GetCenter());
        }
        else
        {
            if (hud.Screen.Find("OnJog-Spellbook") is { } bbtn && bbtn.IsVisibleInTree()) await Click(bbtn.GetGlobalRect().GetCenter());
            else
            {
                // The button is hidden (Special shown instead): the S key still opens the book.
                bookNote = "; Spellbook button hidden" + (hud.Screen.Find("OnJog-Special")?.IsVisibleInTree() == true ? " (Special shown instead)" : "") + ", book opened with S";
                foreach (var pressed in new[] { true, false })
                    Input.ParseInputEvent(new InputEventKey { Keycode = Key.S, PhysicalKeycode = Key.S, Pressed = pressed });
            }
            await Frames(10);
            if (hud.DevSpellBook is null) return ("FAIL", "the spellbook did not open");
            await Frames(150);
            string what = "";
            for (int step = 0; step < 12 && hud.DevSpellBook is { } book; step++)
            {
                var at = book.DevNextClick(c.Id, out what);
                if (at == null)
                {
                    if (what == "turning") { await Frames(10); continue; }
                    break;
                }
                if (step == 0) Shot(tag + "_book");
                GD.Print("[E2E] book click: " + what);
                await Click(at.Value);
                await Frames(what.StartsWith("spell") ? 5 : 45);
            }
            if (hud.DevSpellBook != null)
            {
                Shot(tag + "_book");
                return ("FAIL", "could not pick the spell in the book (" + what + ")");
            }
        }
        await Frames(3);

        bool noTarget = abOpt?.Targeting == Battle.AbilityTargeting.None || spOpt?.Targeting == Battle.SpellTargeting.None;
        BattleCommand? planned = null;
        string scanNote = bookNote, selfNote = "";
        List<BattleEvent>? predicted = null;
        if (noTarget)
        {
            if (submitted.Count == 0) return ("FAIL", "the button armed nothing and submitted nothing (armed: " + _screen.DevArmed + ")");
            planned = submitted[0];
        }
        else
        {
            var expect = (isSpell ? "spell:" : "ability:") + c.Id;
            if (_screen.DevArmed != expect) return ("FAIL", $"not armed: {_screen.DevArmed}, expected {expect}");
            // Targeting scan: per cell, the click's command (cursor accepts) against the engine's verdict on the natural command.
            var s = b.State;
            var accepted = new List<(Vector2I Cell, Vector2 Mouse, BattleCommand Cmd, CombatCursor Cur)>();
            int uiOnly = 0, engineOnly = 0;
            bool selfOk = false;
            var uiOnlyCells = new List<string>(); var engineOnlyCells = new List<string>();
            for (int x = -1; x <= s.Width; x++)
            for (int y = 0; y < s.Height; y++)
            {
                var cell = new Vector2I(x, y);
                var mouse = _screen.Arena.Camera.UnprojectPosition(_screen.Arena.Grid.CellCenter(cell));
                if (_screen.Arena.CellAtScreen(mouse, bordered: true) != cell) continue;   // not hoverable from here
                var (cmd, cur) = _screen.DevResolve(cell, mouse);
                bool ui = cmd != null && Matches(cmd, c.Id);
                var natural = Natural(b, lact, c, abOpt, spOpt, new Cell(x, y));
                bool eng = natural != null && DryRun(db, b, natural).Ok;
                bool uiEng = ui && DryRun(db, b, cmd!).Ok;
                if (ui && uiEng) accepted.Add((cell, mouse, cmd!, cur));
                if (ui && !uiEng) { uiOnly++; if (uiOnlyCells.Count < 4) uiOnlyCells.Add($"{x},{y}" + (uiOnlyCells.Count == 0 ? $" [{DryRun(db, b, cmd!).Error}]" : "")); }
                if (eng && !ui && natural is UseAbilityCmd { TargetId: int self } && self == lact.Actor) { selfOk = true; continue; }
                if (eng && !ui) { engineOnly++; if (engineOnlyCells.Count < 4) engineOnlyCells.Add($"{x},{y}"); }
            }
            if (uiOnly > 0) scanNote += $"; cursor accepts {uiOnly} cells the engine rejects ({string.Join(" ", uiOnlyCells)})";
            if (engineOnly > 0) scanNote += $"; cursor refuses {engineOnly} cells the engine accepts ({string.Join(" ", engineOnlyCells)})";
            if (selfOk) selfNote = "; (engine also accepts the user itself as target, the cursor does not)";
            if (accepted.Count == 0)
            {
                Shot(tag + "_pre");
                return ("FAIL", "no target the cursor offers is accepted by the engine" + scanNote);
            }
            var pick = accepted.OrderBy(a => Score(b, c, a.Cmd, a.Cell)).First();
            planned = pick.Cmd;
            predicted = DryRun(db, b, planned).Events.ToList();
            CombatLauncher.FakeMouse = pick.Mouse;
            _screen.DevFakeCursor();
            await Frames(4);
            Shot(tag + "_pre");
            await Click(pick.Mouse);
            if (pick.Cur is not (CombatCursor.Cast or CombatCursor.Ability or CombatCursor.Interact))
                scanNote += $"; cursor {pick.Cur}";
        }
        if (predicted == null && planned != null) { }
        // Wait for the submission and the playback.
        for (int f = 0; f < 30 && submitted.Count == 0; f++) await Frames(1);
        if (submitted.Count == 0) return ("FAIL", "the click submitted nothing" + scanNote);
        var sent = submitted[0];
        if (!noTarget && MessageJson.Serialize(sent) != MessageJson.Serialize(planned!))
            return ("FAIL", $"submitted {MessageJson.Serialize(sent)} instead of {MessageJson.Serialize(planned!)}" + scanNote);
        if (session.LastRejection is { } why) return ("FAIL", "engine rejected the command: " + why + scanNote);
        CombatLauncher.FakeMouse = null;
        if (!await Idle(45000)) { Shot(tag + "_post"); return ("FAIL", "freeze: playback did not finish in 45 s" + scanNote); }
        await Frames(10);
        Shot(tag + "_post");
        var played = events.ToList();
        string Kinds(IEnumerable<BattleEvent> evs) => string.Join(",", evs.Select(e => e.GetType().Name).Distinct());
        bool effect = played.Any(e => e is Damaged or Healed or EffectAdded or UnitSummoned or Moved or Teleported or FieldChanged
            or ManaChanged or StackChanged or AtbChanged or SpellResisted or UnitDied or Missed or AttackStarted);
        bool mentioned = played.Any(e => e is SpellCast sc && sc.Spell == c.Id || e is AbilityTriggered at && (at.Ability == c.Id || c.Id.StartsWith(at.Ability)));
        if (predicted != null)
        {
            var pj = MessageJson.Serialize(predicted.Take(played.Count));
            var aj = MessageJson.Serialize(played.Take(predicted.Count));
            if (pj != aj || played.Count < predicted.Count)
                return ("FAIL", $"played events differ from the engine's dry run (played {played.Count}, predicted {predicted.Count})" + scanNote);
        }
        if (!effect && !mentioned) return ("FAIL", "nothing happened (events: " + Kinds(played) + ")" + scanNote);
        var detail = $"{(noTarget ? "no target" : TargetText(b, sent))}; events {Kinds(played)}" + (mentioned ? "" : "; no SpellCast/AbilityTriggered for it") + scanNote + selfNote;
        return (scanNote.Length > 0 || !mentioned ? "WARN" : "PASS", detail);
    }

    static string TargetText(Battle b, BattleCommand c) => c switch
    {
        CastSpellCmd { TargetId: int t } => "target " + b.State.Unit(t)?.CreatureId,
        UseAbilityCmd { TargetId: int t } => "target " + b.State.Unit(t)?.CreatureId,
        CastSpellCmd { Cell: { } cl } => $"cell {cl.X},{cl.Y}",
        UseAbilityCmd { Cell: { } cl } => $"cell {cl.X},{cl.Y}",
        _ => "",
    };

    static bool Matches(BattleCommand cmd, string id) =>
        cmd is CastSpellCmd cs && cs.Spell == id || cmd is UseAbilityCmd ua && ua.Ability == id;

    static BattleCommand? Natural(Battle b, LegalActions la, Case c, AbilityOption? ab, SpellOption? sp, Cell cell)
    {
        var s = b.State;
        var occ = s.At(cell) ?? s.Units.FirstOrDefault(u => !u.Alive && !u.Removed && u.Kind == UnitKind.Creature && u.Occupies(cell));
        if (ab != null)
            return ab.Targeting == Battle.AbilityTargeting.Cell ? new UseAbilityCmd(la.Actor, ab.Ability, null, cell) { Side = 0 }
                : occ != null ? new UseAbilityCmd(la.Actor, ab.Ability, occ.Id) { Side = 0 } : null;
        if (sp != null)
            return sp.Targeting switch
            {
                Battle.SpellTargeting.Cell => s.InBounds(cell) ? new CastSpellCmd(la.Actor, sp.Spell, null, cell) { Side = 0 } : null,
                Battle.SpellTargeting.UnitAndCell => occ != null ? new CastSpellCmd(la.Actor, sp.Spell, occ.Id, occ.Pos) { Side = 0 } : null,
                _ => occ != null ? new CastSpellCmd(la.Actor, sp.Spell, occ.Id) { Side = 0 } : null,
            };
        return null;
    }

    static ApplyResult DryRun(GameDatabase db, Battle b, BattleCommand cmd) => new Battle(db, b.State.Clone()).Apply(cmd);

    /// <summary>Target preference: lower first.</summary>
    static int Score(Battle b, Case c, BattleCommand cmd, Vector2I cell)
    {
        var s = b.State;
        int? tid = cmd is CastSpellCmd cs ? cs.TargetId : cmd is UseAbilityCmd ua ? ua.TargetId : null;
        var t = tid is int id ? s.Unit(id) : null;
        var cl = new Cell(cell.X, cell.Y);
        int nearEnemy = s.Living.Where(u => u.Side == 1 && u.Kind == UnitKind.Creature).Select(u => Battle.Octile(u.Pos, cl)).DefaultIfEmpty(99).Min();
        return c.Prefer switch
        {
            "friend" => t is { Side: 0 } ? (t.Kind == UnitKind.Creature ? 0 : 5) : t == null ? 50 : 100,
            "corpse" => t is { Alive: false } ? 0 : t is { Side: 0 } ? 10 : 100,
"machine" => t is { Side: 1, IsWarMachine: true } ? 0 : t is { Side: 1 } ? 5 : 100,
            "cell" => Math.Abs(cell.X - s.Width / 2) + Math.Abs(cell.Y - s.Height / 2),
            _ => t is { Side: 1 } ? (t.Kind == UnitKind.Creature ? 0 : 5) : t == null ? 10 + nearEnemy : 100,
        };
    }

    static BattleCommand PassCmd(Battle b, LegalActions la) =>
        la.IsHero || !la.CanDefend ? new WaitCmd(la.Actor) { Side = la.Side } : new DefendCmd(la.Actor) { Side = la.Side };

    static int PresetFor(string? town, int avoid = -1)
    {
        var presets = Game.Instance.Db.DuelPresets;
        for (int i = 0; i < presets.Count; i++)
            if (i != avoid && (town == null || presets[i].Hero.Shared?.Town == town)) return i;
        return avoid == 0 ? 1 : 0;
    }

    static void MoveNextTo(BattleState s, Unit mover, Unit anchor)
    {
        var taken = new HashSet<Cell>(s.Obstacles);
        foreach (var u in s.Units) if (u.Alive && u != mover) foreach (var cc in u.Cells()) taken.Add(cc);
        for (int x = anchor.Pos.X - mover.Size; x <= anchor.Pos.X + anchor.Size; x++)
        for (int y = anchor.Pos.Y - mover.Size; y <= anchor.Pos.Y + anchor.Size; y++)
        {
            var p = new Cell(x, y);
            var cells = mover.CellsAt(p).ToList();
            if (cells.All(cc => s.InBounds(cc) && !taken.Contains(cc)))
            {
                mover.Pos = mover.StartPos = p;
                return;
            }
        }
    }

    // ------------------------------------------------------------------ input / timing

    async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>Waits until the screen has played everything (several idle frames in a row); false on timeout.</summary>
    async Task<bool> Idle(int timeoutMs)
    {
        ulong start = Time.GetTicksMsec();
        int quiet = 0;
        while (quiet < 4)
        {
            if (Time.GetTicksMsec() - start > (ulong)timeoutMs) return false;
            await Frames(1);
            quiet = _screen is { } sc && IsInstanceValid(sc) && sc.DevIdle ? quiet + 1 : 0;
        }
        return true;
    }

    Vector2 ToWindow(Vector2 vp) => GetViewport().GetFinalTransform() * vp;

    async Task Click(Vector2 vp)
    {
        var w = ToWindow(vp);
        Input.ParseInputEvent(new InputEventMouseMotion { Position = w, GlobalPosition = w });
        await Frames(2);
        Input.ParseInputEvent(new InputEventMouseButton { Position = w, GlobalPosition = w, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        await Frames(1);
        Input.ParseInputEvent(new InputEventMouseButton { Position = w, GlobalPosition = w, ButtonIndex = MouseButton.Left, Pressed = false });
        await Frames(2);
    }

    void Shot(string name)
    {
        var img = GetViewport().GetTexture().GetImage();
        img.Resize(img.GetWidth() / 2, img.GetHeight() / 2, Image.Interpolation.Bilinear);
        img.SavePng($"{_out}/{name}.png");
    }
}
