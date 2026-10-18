using System.Text.Json.Nodes;
using System.Xml.Linq;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;
using H5.Core.IO;
using H5.Core.Xdb;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.AdvNet;

// Ghost Mode (original multiplayer option "Selecting this option will enable a Ghost Mode"):
// while other players take their turns, each of your heroes is represented by a ghost that you
// move over the map, spending energy, to hinder the active player. Data used:
//   DefaultStats.xdb <GhostMode>: MaxSkillLevel 30, EnergyReviveBase 2000, EnergyRevivePerMinute 200,
//     EnergyPerLevel 1000, BaseVisibleRange 12, BaseMoveCost 200, TouchObjectCost 500,
//     DevourDiceSize 3, Revenge LevelLosedBase 0 / LevelLosedPerRevenge 0.2
//   RefTables/GhostMode/Classes.xdb: ghost class per hero faction (Town) with start levels of the
//     six skills (Speed, Devour, Haunt, Possess, Curse, Revenge)
//   UI/GhostInfo texts: "Energy is needed to move, curse objects, dispel enemy curses, guard selected
//     buildings and to improve skills. Amount of energy depends on duration of the enemy turns."
//   Skill texts: Haunt (a haunted mine produces fewer resources, at higher levels devours stored
//     ones), Curse (decreases all primary skills of a nearby enemy hero; also removes curses from
//     friendly heroes), Possess (neutral stack may be controlled by the ghost in combat, doubling
//     attack/defence), Devour (shows the power of the enemy / chance to win), Revenge (damage to
//     the enemy ghost when removing its curse), Speed (less energy per move).
//
// NOT traced in H5_Game.exe (TODO(exe)): the per-level magnitudes of Haunt/Curse/Possess/Speed,
// the energy cap, ghost movement rules over blocked terrain, and the "phases" of ghost mode used
// with simultaneous turns. The values below are explicit placeholders, isolated in GhostRules.

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
            MaxSkillLevel = (int)stats.Get("GhostMode/MaxSkillLevel", 30),
            EnergyReviveBase = (int)stats.Get("GhostMode/EnergyReviveBase", 2000),
            EnergyRevivePerMinute = (int)stats.Get("GhostMode/EnergyRevivePerMinute", 200),
            EnergyPerLevel = (int)stats.Get("GhostMode/EnergyPerLevel", 1000),
            BaseVisibleRange = (int)stats.Get("GhostMode/BaseVisibleRange", 12),
            BaseMoveCost = (int)stats.Get("GhostMode/BaseMoveCost", 200),
            TouchObjectCost = (int)stats.Get("GhostMode/TouchObjectCost", 500),
            DevourDiceSize = (int)stats.Get("GhostMode/DevourDiceSize", 3),
            RevengeLevelLostBase = stats.Get("GhostMode/Revenge/LevelLosedBase", 0),
            RevengeLevelLostPerRevenge = stats.Get("GhostMode/Revenge/LevelLosedPerRevenge", 0.2),
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
    public const string Speed = "GHOST_SKILL_SPEED", Devour = "GHOST_SKILL_DEVOUR", Haunt = "GHOST_SKILL_HAUNT",
        Possess = "GHOST_SKILL_POSSESS", Curse = "GHOST_SKILL_CURSE", Revenge = "GHOST_SKILL_REVENGE";
}

/// <summary>Placeholder magnitudes (see the file header): one place to replace once traced in the exe.</summary>
public static class GhostRules
{
    /// <summary>Speed: −3% move cost per level above 1, at least 25% of the base cost.</summary>
    public static int MoveCost(GhostData d, int speed, bool diagonal) =>
        (int)Math.Max(d.BaseMoveCost * 0.25, d.BaseMoveCost * (1 - 0.03 * (speed - 1)) * (diagonal ? 1.41 : 1.0));
    /// <summary>Energy needed to raise a skill from <paramref name="level"/> to the next level.</summary>
    public static int UpgradeCost(GhostData d, int level) => d.EnergyPerLevel * (level + 1);
    /// <summary>Haunt: the mine yields 10% less per haunt level (never below zero).</summary>
    public static int HauntedYield(int amount, int hauntLevel) => Math.Max(0, (int)(amount * (1 - 0.1 * hauntLevel)));
    /// <summary>Curse: −1 to each primary skill per 3 curse levels (at least 1).</summary>
    public static int CursePenalty(int curseLevel) => Math.Max(1, curseLevel / 3);
    /// <summary>Energy cap: one enemy turn's base revive ×3.</summary>
    public static int EnergyCap(GhostData d) => d.EnergyReviveBase * 3;
}

public sealed class Ghost
{
    public int HeroId;
    public int Owner;
    public string Class = "";
    public Tile Pos;
    public int Energy;
    public Dictionary<string, int> Levels = new();
    public int Level(string skill) => Levels.TryGetValue(skill, out var l) ? l : 0;
}

public sealed class GhostEffect
{
    public string Kind = "";          // "haunt" (object), "curse" (hero), "possess" (object)
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
    readonly GhostData _d;
    readonly H5.Adventure.Engine.Adventure _g;
    public readonly List<Ghost> Ghosts = new();
    public readonly List<GhostEffect> Effects = new();
    /// <summary>Map player whose turn the ghosts are haunting; -1 when inactive.</summary>
    public int ActiveFor { get; private set; } = -1;
    long _lastRegenMs;

    public GhostMode(GhostData data, H5.Adventure.Engine.Adventure game)
    {
        _d = data;
        _g = game;
        // Haunted mines produce less for their owner.
        game.MineIncomeHook = (obj, player, kind, amount) =>
            Effects.Where(e => e.Kind == "haunt" && e.Target == obj && e.Owner != player).Aggregate(amount, (a, e) => GhostRules.HauntedYield(a, e.Level));
    }

    public GhostData Data => _d;

    /// <summary>A new turn began: every human player except <paramref name="active"/> gets ghosts on its heroes.</summary>
    public void BeginTurn(int active, IReadOnlySet<int> humanPlayers, long nowMs)
    {
        ActiveFor = active;
        _lastRegenMs = nowMs;
        foreach (var h in _g.S.Heroes.Where(h => h.Alive && h.Owner != active && humanPlayers.Contains(h.Owner)))
        {
            var gh = Ghosts.Find(x => x.HeroId == h.Id);
            if (gh == null)
            {
                var cls = _d.ClassFor(h.Town);
                gh = new Ghost { HeroId = h.Id, Owner = h.Owner, Class = cls.Id, Levels = new(cls.StartLevels) };
                Ghosts.Add(gh);
            }
            gh.Pos = h.Pos;
            gh.Energy = Math.Min(GhostRules.EnergyCap(_d), gh.Energy + _d.EnergyReviveBase);
        }
        Ghosts.RemoveAll(x => _g.S.Hero(x.HeroId) == null);
    }

    /// <summary>Energy grows with the duration of the enemy turn (EnergyRevivePerMinute).</summary>
    public bool Regenerate(long nowMs)
    {
        if (ActiveFor < 0) return false;
        long minutes = (nowMs - _lastRegenMs) / 60000;
        if (minutes <= 0) return false;
        _lastRegenMs += minutes * 60000;
        foreach (var gh in Ghosts.Where(x => x.Owner != ActiveFor))
            gh.Energy = Math.Min(GhostRules.EnergyCap(_d), gh.Energy + (int)(minutes * _d.EnergyRevivePerMinute));
        return true;
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
                if (cost > gh.Energy) return "not enough energy";
                gh.Energy -= cost;
                gh.Pos = to;
                return null;
            }
            case "upgrade":
            {
                var skill = (string?)c["skill"] ?? "";
                int lvl = gh.Level(skill);
                if (lvl >= _d.MaxSkillLevel) return "skill at maximum";
                int cost = GhostRules.UpgradeCost(_d, lvl);
                if (cost > gh.Energy) return "not enough energy";
                gh.Energy -= cost;
                gh.Levels[skill] = lvl + 1;
                return null;
            }
            case "haunt":
            case "possess":
            {
                int obj = (int)c["object"]!;
                if (obj < 0 || obj >= _g.Map.Objects.Count) return "no such object";
                var o = _g.Map.Objects[obj];
                if (!Near(gh.Pos, o.Pos)) return "too far";
                bool haunt = (string?)c["action"] == "haunt";
                if (haunt && o.Kind != ObjectKind.Mine) return "only mines can be haunted";
                if (!haunt && o.Kind != ObjectKind.Monster) return "only neutral creatures can be possessed";
                if (gh.Energy < _d.TouchObjectCost) return "not enough energy";
                gh.Energy -= _d.TouchObjectCost;
                Effects.RemoveAll(e => e.Target == obj && e.Kind == (haunt ? "haunt" : "possess"));
                Effects.Add(new GhostEffect { Kind = haunt ? "haunt" : "possess", Target = obj, Owner = player, HeroIdOfGhost = gh.HeroId,
                    Level = gh.Level(haunt ? GhostSkills.Haunt : GhostSkills.Possess) });
                return null;
            }
            case "curse":
            {
                int heroId = (int)c["target"]!;
                var h = _g.S.Hero(heroId);
                if (h == null || !Near(gh.Pos, h.Pos)) return "too far";
                if (gh.Energy < _d.TouchObjectCost) return "not enough energy";
                gh.Energy -= _d.TouchObjectCost;
                if (h.Owner == player || Allied(h.Owner, player))
                {
                    // Removing curses from a friendly hero; Revenge drains the cursing ghost.
                    foreach (var e in Effects.Where(e => e.Kind == "curse" && e.Target == heroId).ToList())
                    {
                        Uncurse(h, e);
                        if (Ghosts.Find(x => x.HeroId == e.HeroIdOfGhost) is { } enemy)
                        {
                            double lost = _d.RevengeLevelLostBase + _d.RevengeLevelLostPerRevenge * gh.Level(GhostSkills.Revenge);
                            foreach (var k in enemy.Levels.Keys.ToList()) enemy.Levels[k] = Math.Max(1, enemy.Levels[k] - (int)Math.Round(lost));
                        }
                    }
                    return null;
                }
                if (Effects.Any(e => e.Kind == "curse" && e.Target == heroId && e.Owner == player)) return "already cursed";
                int pen = GhostRules.CursePenalty(gh.Level(GhostSkills.Curse));
                h.Attack -= pen; h.Defence -= pen; h.SpellPower -= pen; h.Knowledge -= pen;
                Effects.Add(new GhostEffect { Kind = "curse", Target = heroId, Owner = player, HeroIdOfGhost = gh.HeroId, Level = gh.Level(GhostSkills.Curse), Penalty = pen });
                return null;
            }
            default: return "unknown ghost action";
        }
    }

    void Uncurse(HeroState h, GhostEffect e)
    {
        h.Attack += e.Penalty; h.Defence += e.Penalty; h.SpellPower += e.Penalty; h.Knowledge += e.Penalty;
        Effects.Remove(e);
    }

    bool Allied(int a, int b) => _g.S.Players.Find(p => p.Index == a) is { } pa && _g.S.Players.Find(p => p.Index == b) is { } pb && pa.Team == pb.Team;

    static bool Near(Tile a, Tile b) => a.Floor == b.Floor && Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;

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
                o["energy"] = gh.Energy;
                var lv = new JsonObject();
                foreach (var (k, v) in gh.Levels) lv[k] = v;
                o["levels"] = lv;
            }
            ghosts.Add(o);
        }
        var effects = new JsonArray();
        foreach (var e in Effects.Where(e => e.Owner == me || (e.Kind == "curse" && _g.S.Hero(e.Target)?.Owner == me)))
            effects.Add(new JsonObject { ["kind"] = e.Kind, ["target"] = e.Target, ["owner"] = e.Owner, ["level"] = e.Level });
        return new JsonObject { ["activeFor"] = ActiveFor, ["ghosts"] = ghosts, ["effects"] = effects };
    }
}
