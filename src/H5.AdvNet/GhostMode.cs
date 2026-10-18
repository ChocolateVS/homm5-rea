using System.Text.Json.Nodes;
using System.Xml.Linq;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using H5.Core.Data;
using H5.Core.IO;
using H5.Core.Xdb;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.AdvNet;

// Ghost Mode (original multiplayer option "Selecting this option will enable a Ghost Mode"):
// while other players take their turns, each of your heroes is represented by a ghost that you
// move over the map, spending energy, to hinder the active player.
//
// Ported from H5_Game.exe (NWorld::CGhost, CAdvMapGhost, CGhostEnchanted; addresses below):
//   * data: DefaultStats <GhostMode> lives at RPGStats+0xadc (serializer 009559c0): MaxSkillLevel,
//     EnergyReviveBase, EnergyRevivePerMinute, EnergyPerLevel, BaseVisibleRange, BaseMoveCost,
//     TouchObjectCost, DevourDiceSize, Revenge{LevelLosedBase, LevelLosedPerRevenge}.
//   * skills, in the exe's index order (004d4480): 0 Devour, 1 Haunt, 2 Possess, 3 Curse, 4 Revenge,
//     5 Speed. Each skill has a level and accumulated experience (CGhost+0x18, 8 bytes per skill).
//   * creation (00ca2a30): class start levels clamped to [1, MaxSkillLevel], experience set to the
//     threshold of that level; the default upgrade skill is the highest start level. At most 8 ghosts
//     per player (00ca2c30).
//   * experience to reach level L (00ca1320): EnergyPerLevel·(L·(L+1)/2 − 1).
//   * improving a skill (00ca18c0) invests energy as experience (capped at the max level's threshold)
//     and raises the level while the threshold of the next level is reached.
//   * energy (00ca1790): an enemy turn revives EnergyReviveBase + whole minutes · EnergyRevivePerMinute;
//     spending (00ca12f0) fails when energy is insufficient; adding (00ca1350) has no cap.
//   * movement (00bc7b30, 00ca9a20): one step costs BaseMoveCost / Speed level, a diagonal step that
//     cost rounded ×√2.
//   * haunt (0044dac0): a haunted mine loses, per day and per haunting ghost, L/7 + T[L%7][day%7] with
//     T the exe's 7×7 spread table (00f2c850) — exactly L per week.
//   * curse (00ca1580): each cursing ghost lowers the hero by max(1, CurseLevel/2).
//   * possess (0044d920, 0044db60): the player whose ghosts have the largest total Possess level controls
//     the stack; when it is attacked the possession takes hold if Σ(PossessLevel d4) beats
//     Σ((1 + 2·upgraded + tier) d4) for that creature; a possessed stack fights with doubled attack and
//     defence (skill text).
//   * devour (00ca1da0, 00ca1ef0): ghost-versus-ghost contests roll Devour-level dice of 1..DevourDiceSize,
//     re-rolled up to 10 times on a tie.
//   * revenge (00ca1430): levels taken = round(power + 0.01), plus one with probability equal to the
//     remaining fraction, power = (RevengeLevel / n) · LevelLosedPerRevenge + LevelLosedBase.
//   * touching objects (haunt / curse / possess / guard / dispel) costs TouchObjectCost and takes effect
//     on the next turn (UI texts "Your ghost will haunt this object next turn"); one action per ghost.
// Not traced (TODO(exe)): how the curse penalty is split between the four primary skills (exe has the
// split helpers 00ca15d0/00ca15f0, callers unresolved — we lower all four, per the skill text), the
// exact devour loser penalty, and the creature fields read by the possess roll (+0x8c / +0x100 read as
// tier / upgraded).

public sealed class GhostSkillDef
{
    public string Id = "";
    public string Name = "", Description = "";
}

public sealed class GhostClassDef
{
    public string Id = "";
    public string Town = "";
    public string Name = "";
    public string Character = "", Face = "", LargeFace = "", Selection = "";
    public Dictionary<string, int> StartLevels = new();
}

public sealed class GhostData
{
    public int MaxSkillLevel = 30, EnergyReviveBase = 2000, EnergyRevivePerMinute = 200, EnergyPerLevel = 1000,
        BaseVisibleRange = 12, BaseMoveCost = 200, TouchObjectCost = 500, DevourDiceSize = 3;
    public double RevengeLevelLostBase, RevengeLevelLostPerRevenge = 0.2;
    public List<GhostClassDef> Classes = new();
    public List<GhostSkillDef> Skills = new();

    public static GhostData Load(PakFileSystem fs, RpgStats stats)
    {
        var d = new GhostData
        {
            MaxSkillLevel = Math.Max(1, (int)stats.Get("GhostMode/MaxSkillLevel", 30)),
            EnergyReviveBase = Math.Max(0, (int)stats.Get("GhostMode/EnergyReviveBase", 2000)),
            EnergyRevivePerMinute = Math.Max(0, (int)stats.Get("GhostMode/EnergyRevivePerMinute", 200)),
            EnergyPerLevel = Math.Max(1, (int)stats.Get("GhostMode/EnergyPerLevel", 1000)),
            BaseVisibleRange = Math.Max(0, (int)stats.Get("GhostMode/BaseVisibleRange", 12)),
            BaseMoveCost = Math.Max(1, (int)stats.Get("GhostMode/BaseMoveCost", 200)),
            TouchObjectCost = Math.Max(0, (int)stats.Get("GhostMode/TouchObjectCost", 500)),
            DevourDiceSize = Math.Max(1, (int)stats.Get("GhostMode/DevourDiceSize", 3)),
            RevengeLevelLostBase = Math.Max(0, stats.Get("GhostMode/Revenge/LevelLosedBase", 0)),
            RevengeLevelLostPerRevenge = Math.Max(0, stats.Get("GhostMode/Revenge/LevelLosedPerRevenge", 0.2)),
        };
        var xdb = new XdbLoader(fs);
        const string dir = "GameMechanics/RefTables/GhostMode/";
        if (fs.Exists(dir + "Classes.xdb"))
            foreach (var item in xdb.Load(dir + "Classes.xdb").Root!.Element("objects")!.Elements("Item"))
            {
                var obj = item.Element("obj")!;
                var c = new GhostClassDef
                {
                    Id = (string?)item.Element("ID") ?? "",
                    Town = (string?)obj.Element("Town") ?? "",
                    Name = xdb.ReadText((string?)obj.Element("UI")?.Element("Name")?.Attribute("href"), dir + "Classes.xdb")?.Trim() ?? "",
                    Character = Href(obj.Element("Art")?.Element("Character"), dir),
                    Face = Href(obj.Element("Art")?.Element("Face"), dir),
                    LargeFace = Href(obj.Element("Art")?.Element("LargeFace"), dir),
                    Selection = Href(obj.Element("Art")?.Element("Selection"), dir),
                };
                foreach (var s in obj.Element("skills")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                    c.StartLevels[(string?)s.Element("Skill") ?? ""] = (int?)s.Element("StartLevel") ?? 1;
                d.Classes.Add(c);
            }
        if (fs.Exists(dir + "Skills.xdb"))
            foreach (var item in xdb.Load(dir + "Skills.xdb").Root!.Element("objects")!.Elements("Item"))
            {
                var obj = item.Element("obj")!;
                d.Skills.Add(new GhostSkillDef
                {
                    Id = (string?)item.Element("ID") ?? "",
                    Name = xdb.ReadText((string?)obj.Element("Name")?.Attribute("href"), dir + "Skills.xdb")?.Trim() ?? "",
                    Description = xdb.ReadText((string?)obj.Element("Description")?.Attribute("href"), dir + "Skills.xdb")?.Trim() ?? "",
                });
            }
        return d;
    }

    static string Href(XElement? e, string dir) => XdbRef.Parse((string?)e?.Attribute("href"), dir + "Classes.xdb").Path;

    public GhostClassDef ClassFor(string town) =>
        Classes.Find(c => c.Town == town) ?? Classes.FirstOrDefault() ?? new GhostClassDef { Id = "GHOST_CLASS_NONE" };
}

public static class GhostSkills
{
    public const string Devour = "GHOST_SKILL_DEVOUR", Haunt = "GHOST_SKILL_HAUNT", Possess = "GHOST_SKILL_POSSESS",
        Curse = "GHOST_SKILL_CURSE", Revenge = "GHOST_SKILL_REVENGE", Speed = "GHOST_SKILL_SPEED";
    /// <summary>The exe's skill index order (004d4480).</summary>
    public static readonly string[] All = { Devour, Haunt, Possess, Curse, Revenge, Speed };
}

/// <summary>Ghost mode formulas, as ported from H5_Game.exe (see the file header for addresses).</summary>
public static class GhostRules
{
    /// <summary>Experience needed to reach <paramref name="level"/> (00ca1320).</summary>
    public static int ExperienceFor(GhostData d, int level) => (d.EnergyPerLevel * level + d.EnergyPerLevel) * level / 2 - d.EnergyPerLevel;

    /// <summary>One step's energy cost: BaseMoveCost / Speed, a diagonal step rounded ×√2 (00bc7b30, 00ca9a20).</summary>
    public static int MoveCost(GhostData d, int speed, bool diagonal)
    {
        int step = d.BaseMoveCost / Math.Max(1, speed);
        return diagonal ? (int)Math.Round(step * 1.41421354f, MidpointRounding.ToEven) : step;
    }

    /// <summary>Energy revived by an enemy turn of <paramref name="elapsedMs"/> (00ca1790).</summary>
    public static int Revive(GhostData d, long elapsedMs) => d.EnergyReviveBase + (int)(elapsedMs / 60000) * d.EnergyRevivePerMinute;

    /// <summary>The exe's 7×7 spread table for haunted mines (00f2c850): row L%7 has L%7 ones over the week.</summary>
    static readonly int[,] HauntSpread =
    {
        { 0, 0, 0, 0, 0, 0, 0 },
        { 0, 0, 0, 1, 0, 0, 0 },
        { 0, 1, 0, 0, 0, 1, 0 },
        { 0, 1, 0, 1, 0, 1, 0 },
        { 1, 0, 1, 0, 1, 0, 1 },
        { 1, 0, 1, 1, 1, 0, 1 },
        { 1, 1, 1, 0, 1, 1, 1 },
    };

    /// <summary>Resources a ghost of Haunt level <paramref name="level"/> takes from a mine on <paramref name="day"/> (0044dac0).</summary>
    public static int HauntLoss(int level, int day) => HauntSpread[level % 7, ((day % 7) + 7) % 7] + level / 7;

    /// <summary>Primary-skill penalty of one cursing ghost (00ca1580).</summary>
    public static int CursePenalty(int curseLevel) => Math.Max(1, curseLevel / 2);

    /// <summary>Sum of <paramref name="dice"/> rolls of 1..<paramref name="sides"/> (008c0540, 00ca13f0).</summary>
    public static int Dice(Rng rng, int dice, int sides)
    {
        int sum = 0;
        for (int i = 0; i < dice; i++) sum += 1 + rng.Next(Math.Max(1, sides));
        return sum;
    }

    /// <summary>Levels taken by a Revenge of <paramref name="revengeLevel"/> split <paramref name="n"/> ways (00ca1430).</summary>
    public static int RevengeLevels(GhostData d, Rng rng, int revengeLevel, int n)
    {
        if (n < 1) return 0;
        float power = revengeLevel / n * (float)d.RevengeLevelLostPerRevenge + (float)d.RevengeLevelLostBase;
        int levels = (int)Math.Round(power + 0.01f, MidpointRounding.ToEven);
        if (rng.NextDouble() < power - levels) levels++;
        return levels;
    }

    /// <summary>Dice the defending creature rolls against possession (0044db60): 1 + 2·upgraded + tier.</summary>
    public static int PossessResistDice(CreatureDef? c) => c == null ? 1 : 1 + (c.Upgrade ? 2 : 0) + c.Tier;
}

public sealed class Ghost
{
    public int HeroId;
    public int Owner;
    public string Class = "";
    public Tile Pos;
    /// <summary>Stored energy (CGhost+0xc).</summary>
    public int Energy;
    /// <summary>Energy revived by the current enemy turn and the part of it already spent.</summary>
    public int Revived, RevivedSpent;
    public Dictionary<string, int> Levels = new();
    public Dictionary<string, int> Experience = new();
    public string DefaultUpgrade = GhostSkills.Devour;
    /// <summary>The ghost's single action: "haunt"/"possess"/"guard" (object), "curse"/"dispel" (hero); pending until next turn.</summary>
    public string? Action;
    public int ActionTarget = -1;
    public bool ActionPending;
    public int Level(string skill) => Levels.TryGetValue(skill, out var l) ? l : 0;
    public int Available => Energy + Revived - RevivedSpent;
}

public sealed class GhostEffect
{
    public string Kind = "";          // "haunt" / "possess" / "guard" (object), "curse" (hero)
    public int Target;
    public int Owner;
    public int Level;
    public int HeroIdOfGhost;
    /// <summary>Applied primary-skill penalty (curses), so it can be removed exactly.</summary>
    public int Penalty;
}

/// <summary>Host-side ghost mode state and rules for one game.</summary>
public sealed class GhostMode
{
    public const int MaxGhostsPerPlayer = 8;
    readonly GhostData _d;
    readonly H5.Adventure.Engine.Adventure _g;
    public readonly List<Ghost> Ghosts = new();
    public readonly List<GhostEffect> Effects = new();
    /// <summary>Map player whose turn the ghosts are haunting; -1 when inactive.</summary>
    public int ActiveFor { get; private set; } = -1;
    long _turnStartMs;

    public GhostMode(GhostData data, H5.Adventure.Engine.Adventure game)
    {
        _d = data;
        _g = game;
        // Haunted mines yield less; past zero the ghosts devour the owner's stock (skill text).
        game.MineIncomeHook = (obj, player, kind, amount) =>
        {
            int loss = Effects.Where(e => e.Kind == "haunt" && e.Target == obj && e.Owner != player)
                .Sum(e => GhostRules.HauntLoss(e.Level, _g.S.Day));
            int net = amount - loss;
            if (net >= 0) return net;
            var p = _g.S.Players.Find(x => x.Index == player);
            return p == null ? 0 : -Math.Min(-net, p.Purse[kind]);
        };
    }

    public GhostData Data => _d;

    Ghost NewGhost(HeroState h)
    {
        var cls = _d.ClassFor(h.Town);
        var gh = new Ghost { HeroId = h.Id, Owner = h.Owner, Class = cls.Id };
        int best = 0;
        foreach (var skill in GhostSkills.All)
        {
            int lvl = Math.Clamp(cls.StartLevels.TryGetValue(skill, out var l) ? l : 1, 1, _d.MaxSkillLevel);
            gh.Levels[skill] = lvl;
            gh.Experience[skill] = GhostRules.ExperienceFor(_d, lvl);
            if (lvl > best) { best = lvl; gh.DefaultUpgrade = skill; }
        }
        return gh;
    }

    /// <summary>A new turn began: every human player except <paramref name="active"/> gets ghosts on its heroes.</summary>
    public void BeginTurn(int active, IReadOnlySet<int> humanPlayers, long nowMs)
    {
        // The finished turn's revived energy that was not spent is stored.
        foreach (var gh in Ghosts) { gh.Energy = gh.Available; gh.Revived = gh.RevivedSpent = 0; }
        // Actions ordered during the previous turn take effect now.
        foreach (var gh in Ghosts.Where(x => x.ActionPending)) ApplyAction(gh);

        ActiveFor = active;
        _turnStartMs = nowMs;
        foreach (var h in _g.S.Heroes.Where(h => h.Alive && h.Owner != active && humanPlayers.Contains(h.Owner)))
        {
            var gh = Ghosts.Find(x => x.HeroId == h.Id);
            if (gh == null)
            {
                if (Ghosts.Count(x => x.Owner == h.Owner) >= MaxGhostsPerPlayer) continue;
                Ghosts.Add(gh = NewGhost(h));
            }
            gh.Pos = h.Pos;
        }
        foreach (var gh in Ghosts.Where(x => x.Owner != active)) gh.Revived = GhostRules.Revive(_d, 0);
        foreach (var gone in Ghosts.Where(x => _g.S.Hero(x.HeroId) == null).ToList()) Dismiss(gone);
    }

    /// <summary>Energy revived grows with the duration of the enemy turn (00ca1790).</summary>
    public bool Regenerate(long nowMs)
    {
        if (ActiveFor < 0) return false;
        int revive = GhostRules.Revive(_d, nowMs - _turnStartMs);
        bool changed = false;
        foreach (var gh in Ghosts.Where(x => x.Owner != ActiveFor && x.Revived != revive))
        {
            gh.Revived = revive;
            changed = true;
        }
        return changed;
    }

    bool Spend(Ghost gh, int amount)
    {
        if (amount < 0 || amount > gh.Available) return false;
        int fromRevived = Math.Min(amount, gh.Revived - gh.RevivedSpent);
        gh.RevivedSpent += fromRevived;
        gh.Energy -= amount - fromRevived;
        return true;
    }

    /// <summary>Invests energy into a skill as experience (00ca18c0). Returns the energy actually spent.</summary>
    public int Improve(Ghost gh, string skill, int amount)
    {
        if (!GhostSkills.All.Contains(skill) || amount <= 0) return 0;
        int exp = gh.Experience.TryGetValue(skill, out var e) ? e : 0;
        amount = Math.Min(Math.Min(amount, GhostRules.ExperienceFor(_d, _d.MaxSkillLevel) - exp), gh.Available);
        if (amount <= 0 || !Spend(gh, amount)) return 0;
        gh.Experience[skill] = exp += amount;
        while (gh.Level(skill) < _d.MaxSkillLevel && GhostRules.ExperienceFor(_d, gh.Level(skill) + 1) <= exp)
            gh.Levels[skill] = gh.Level(skill) + 1;
        return amount;
    }

    /// <summary>Loses one level of a skill; experience drops to that level's threshold (00ca1ae0).</summary>
    void LoseLevel(Ghost gh, string skill)
    {
        if (gh.Level(skill) <= 1) return;
        gh.Levels[skill] = gh.Level(skill) - 1;
        gh.Experience[skill] = GhostRules.ExperienceFor(_d, gh.Level(skill));
    }

    public string? Command(int player, JsonObject c)
    {
        if (ActiveFor < 0 || player == ActiveFor) return "ghost mode is not active for you";
        var gh = Ghosts.Find(x => x.HeroId == (int?)c["hero"] && x.Owner == player);
        if (gh == null) return "no such ghost";
        switch ((string?)c["action"])
        {
            case "move":
            {
                var to = new Tile((int)c["x"]!, (int)c["y"]!, (int?)c["floor"] ?? gh.Pos.Floor);
                var f = _g.Map.Floor(to.Floor);
                if (f == null || !f.InBounds(to.X, to.Y)) return "outside the map";
                int cost = 0;
                var cur = gh.Pos;
                while (cur != to)
                {
                    int dx = Math.Sign(to.X - cur.X), dy = Math.Sign(to.Y - cur.Y);
                    cost += GhostRules.MoveCost(_d, gh.Level(GhostSkills.Speed), dx != 0 && dy != 0);
                    cur = new Tile(cur.X + dx, cur.Y + dy, to.Floor);
                }
                if (!Spend(gh, cost)) return "not enough energy";
                gh.Pos = to;
                return null;
            }
            case "upgrade":
            {
                var skill = (string?)c["skill"] ?? gh.DefaultUpgrade;
                if (!GhostSkills.All.Contains(skill)) return "invalid ghost skill";
                if (gh.Level(skill) >= _d.MaxSkillLevel) return "skill at maximum";
                // Default investment: what the next level still needs.
                int need = GhostRules.ExperienceFor(_d, gh.Level(skill) + 1) - (gh.Experience.TryGetValue(skill, out var e) ? e : 0);
                int amount = (int?)c["amount"] ?? need;
                if (amount > gh.Available) return "not enough energy";
                return Improve(gh, skill, amount) > 0 ? null : "not enough energy";
            }
            case "default":
            {
                var skill = (string?)c["skill"] ?? "";
                if (!GhostSkills.All.Contains(skill)) return "invalid ghost skill";
                gh.DefaultUpgrade = skill;
                return null;
            }
            case "dismiss":
                Dismiss(gh);
                return null;
            case "haunt":
            case "possess":
            case "guard":
            {
                int obj = (int)c["object"]!;
                if (obj < 0 || obj >= _g.Map.Objects.Count) return "no such object";
                var o = _g.Map.Objects[obj];
                if (!Near(gh.Pos, o.Pos)) return "too far";
                var action = (string)c["action"]!;
                switch (action)
                {
                    case "haunt" when o.Kind != ObjectKind.Mine: return "Can't haunt this object";
                    case "possess" when o.Kind != ObjectKind.Monster: return "Can't possess this monster";
                    case "guard" when ObjectOwner(obj) != player: return "Can't guard this object";
                }
                if (Effects.Find(e => e.Kind == "guard" && e.Target == obj && e.Owner != player && !Allied(e.Owner, player)) is { } guard)
                {
                    // An enemy ghost guards the object: the touch becomes an attack decided by Devour.
                    if (!Spend(gh, _d.TouchObjectCost)) return "not enough energy";
                    return DevourContest(gh, guard) ? Order(gh, action, obj, paid: true) : null;
                }
                return Order(gh, action, obj);
            }
            case "curse":
            case "dispel":
            {
                int heroId = (int)c["target"]!;
                var h = _g.S.Hero(heroId);
                if (h == null || !Near(gh.Pos, h.Pos)) return "too far";
                bool friendly = h.Owner == player || Allied(h.Owner, player);
                var action = (string)c["action"]! == "curse" && friendly ? "dispel" : (string)c["action"]!;
                if (action == "dispel" && !friendly) return "Can't dispel this object";
                if (action == "curse" && friendly) return "Can't curse this hero";
                if (action == "curse" && Effects.Any(e => e.Kind == "curse" && e.Target == heroId && e.Owner == player)) return "Can't curse this hero";
                return Order(gh, action, heroId);
            }
            default: return "unknown ghost action";
        }
    }

    /// <summary>Orders the ghost's single action (effective next turn), paying the touch cost.</summary>
    string? Order(Ghost gh, string action, int target, bool paid = false)
    {
        if (!paid && !Spend(gh, _d.TouchObjectCost)) return "Not enough energy";
        ClearEffect(gh);
        gh.Action = action;
        gh.ActionTarget = target;
        gh.ActionPending = true;
        return null;
    }

    void ApplyAction(Ghost gh)
    {
        gh.ActionPending = false;
        switch (gh.Action)
        {
            case "haunt":
                Effects.Add(new GhostEffect { Kind = "haunt", Target = gh.ActionTarget, Owner = gh.Owner, HeroIdOfGhost = gh.HeroId, Level = gh.Level(GhostSkills.Haunt) });
                break;
            case "possess":
                Effects.Add(new GhostEffect { Kind = "possess", Target = gh.ActionTarget, Owner = gh.Owner, HeroIdOfGhost = gh.HeroId, Level = gh.Level(GhostSkills.Possess) });
                break;
            case "guard":
                Effects.Add(new GhostEffect { Kind = "guard", Target = gh.ActionTarget, Owner = gh.Owner, HeroIdOfGhost = gh.HeroId, Level = gh.Level(GhostSkills.Devour) });
                break;
            case "curse" when _g.S.Hero(gh.ActionTarget) is { } h:
            {
                int pen = GhostRules.CursePenalty(gh.Level(GhostSkills.Curse));
                h.Attack -= pen; h.Defence -= pen; h.SpellPower -= pen; h.Knowledge -= pen;
                Effects.Add(new GhostEffect { Kind = "curse", Target = h.Id, Owner = gh.Owner, HeroIdOfGhost = gh.HeroId, Level = gh.Level(GhostSkills.Curse), Penalty = pen });
                break;
            }
            case "dispel" when _g.S.Hero(gh.ActionTarget) is { } h:
            {
                var curses = Effects.Where(e => e.Kind == "curse" && e.Target == h.Id).ToList();
                foreach (var e in curses)
                {
                    Uncurse(h, e);
                    // Revenge: the cursing ghost loses levels (00ca1430); taken from its default upgrade skill.
                    if (Ghosts.Find(x => x.HeroId == e.HeroIdOfGhost) is { } enemy)
                        for (int i = GhostRules.RevengeLevels(_d, _g.S.Rng, gh.Level(GhostSkills.Revenge), curses.Count); i > 0; i--)
                            LoseLevel(enemy, enemy.DefaultUpgrade);
                }
                gh.Action = null;
                break;
            }
        }
    }

    /// <summary>
    /// Ghost-versus-ghost contest (00ca1ef0): both roll Devour dice, re-rolled up to 10 times on a tie.
    /// The loser loses a level of its default upgrade skill and, if it was the guard, its guard. TODO(exe):
    /// the exact loser penalty (the exe also rolls Revenge here).
    /// </summary>
    bool DevourContest(Ghost attacker, GhostEffect guard)
    {
        var defender = Ghosts.Find(x => x.HeroId == guard.HeroIdOfGhost);
        if (defender == null) { Effects.Remove(guard); return true; }
        int a = 0, d = 0;
        for (int i = 0; i < 10 && a == d; i++)
        {
            a = GhostRules.Dice(_g.S.Rng, attacker.Level(GhostSkills.Devour), _d.DevourDiceSize);
            d = GhostRules.Dice(_g.S.Rng, defender.Level(GhostSkills.Devour), _d.DevourDiceSize);
        }
        if (a > d)
        {
            LoseLevel(defender, defender.DefaultUpgrade);
            Effects.Remove(guard);
            defender.Action = null;
            return true;
        }
        LoseLevel(attacker, attacker.DefaultUpgrade);
        return false;
    }

    void ClearEffect(Ghost gh)
    {
        foreach (var e in Effects.Where(e => e.HeroIdOfGhost == gh.HeroId).ToList())
        {
            if (e.Kind == "curse" && _g.S.Hero(e.Target) is { } h) Uncurse(h, e);
            else Effects.Remove(e);
        }
        gh.Action = null;
        gh.ActionPending = false;
    }

    void Dismiss(Ghost gh)
    {
        ClearEffect(gh);
        Ghosts.Remove(gh);
    }

    void Uncurse(HeroState h, GhostEffect e)
    {
        h.Attack += e.Penalty; h.Defence += e.Penalty; h.SpellPower += e.Penalty; h.Knowledge += e.Penalty;
        Effects.Remove(e);
    }

    int ObjectOwner(int obj) => _g.S.Town(obj)?.Owner ?? _g.S.Obj(obj)?.Owner ?? -1;

    bool Allied(int a, int b) => _g.S.Players.Find(p => p.Index == a) is { } pa && _g.S.Players.Find(p => p.Index == b) is { } pb && pa.Team == pb.Team;

    static bool Near(Tile a, Tile b) => a.Floor == b.Floor && Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;

    /// <summary>
    /// Possession when a neutral stack on map object <paramref name="obj"/> is attacked (0044d920,
    /// 0044db60): the player with the largest total Possess level on it rolls Σ(level d4) against the
    /// creature's resistance dice. Returns the controlling player, or -1 when the stack is not possessed.
    /// </summary>
    public int RollPossession(int obj, string creature, GameDatabase core, Rng rng)
    {
        var byPlayer = Effects.Where(e => e.Kind == "possess" && e.Target == obj)
            .GroupBy(e => e.Owner).Select(g => (Player: g.Key, Level: g.Sum(e => e.Level))).ToList();
        if (byPlayer.Count == 0) return -1;
        var best = byPlayer.OrderByDescending(x => x.Level).ThenBy(x => x.Player).First();
        core.Creatures.TryGetValue(creature, out var def);
        int resist = GhostRules.Dice(rng, GhostRules.PossessResistDice(def), 4);
        int ghost = GhostRules.Dice(rng, best.Level, 4);
        return resist < ghost ? best.Player : -1;
    }

    /// <summary>A possessed stack fights with doubled attack and defence (Possess skill text).</summary>
    public static void ApplyPossession(BattleState battle, int side)
    {
        foreach (var u in battle.Units.Where(u => u.Side == side && !u.IsWarMachine))
        {
            u.BaseAttack *= 2;
            u.BaseDefence *= 2;
        }
    }

    /// <summary>What a player sees of ghost mode: its own ghosts fully; other ghosts on explored tiles.</summary>
    public JsonObject ViewFor(int me, AdvMap map)
    {
        var mine = _g.S.Players.Find(p => p.Index == me);
        var ghosts = new JsonArray();
        foreach (var gh in Ghosts)
        {
            bool own = gh.Owner == me;
            if (!own && (mine == null || !ViewFilter.Sees(map, mine, gh.Pos))) continue;
            var o = new JsonObject { ["hero"] = gh.HeroId, ["owner"] = gh.Owner, ["class"] = gh.Class, ["x"] = gh.Pos.X, ["y"] = gh.Pos.Y, ["floor"] = gh.Pos.Floor };
            if (own)
            {
                o["energy"] = gh.Available;
                o["revived"] = gh.Revived;
                o["default"] = gh.DefaultUpgrade;
                if (gh.Action != null) { o["action"] = gh.Action; o["target"] = gh.ActionTarget; o["pending"] = gh.ActionPending; }
                var lv = new JsonObject();
                var xp = new JsonObject();
                foreach (var skill in GhostSkills.All)
                {
                    lv[skill] = gh.Level(skill);
                    xp[skill] = gh.Experience.TryGetValue(skill, out var e) ? e : 0;
                }
                o["levels"] = lv;
                o["experience"] = xp;
            }
            ghosts.Add(o);
        }
        var effects = new JsonArray();
        foreach (var e in Effects.Where(e => e.Owner == me || (e.Kind == "curse" && _g.S.Hero(e.Target)?.Owner == me)))
            effects.Add(new JsonObject { ["kind"] = e.Kind, ["target"] = e.Target, ["owner"] = e.Owner, ["level"] = e.Level });
        return new JsonObject
        {
            ["activeFor"] = ActiveFor,
            ["ghosts"] = ghosts,
            ["effects"] = effects,
            ["maxLevel"] = _d.MaxSkillLevel,
            ["energyPerLevel"] = _d.EnergyPerLevel,
            ["touchCost"] = _d.TouchObjectCost,
        };
    }
}
